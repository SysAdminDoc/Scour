using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Scour.Core.Interfaces;

namespace Scour.Core.Native;

/// <summary>
/// Reads the NTFS Master File Table (MFT) directly via the USN Change Journal API.
/// This is orders of magnitude faster than recursive directory enumeration for building
/// a complete file index. Requires administrative privileges.
///
/// Uses FSCTL_ENUM_USN_DATA to enumerate all MFT records on an NTFS volume.
/// </summary>
public sealed class MftReader
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref MFT_ENUM_DATA_V0 lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        out USN_JOURNAL_DATA_V0 lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_ENUM_DATA_V0
    {
        public long StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public long UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public long MaximumSize;
        public long AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_RECORD_V2
    {
        public int RecordLength;
        public short MajorVersion;
        public short MinorVersion;
        public long FileReferenceNumber;
        public long ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public int Reason;
        public int SourceInfo;
        public int SecurityId;
        public int FileAttributes;
        public short FileNameLength;
        public short FileNameOffset;
        // FileName follows immediately after this struct
    }

    private const int FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>
    /// Result entry from MFT enumeration.
    /// </summary>
    public struct MftEntry
    {
        public long FileReferenceNumber;
        public long ParentFileReferenceNumber;
        public string FileName;
        public bool IsDirectory;
        public int FileAttributes;
    }

    /// <summary>
    /// Check if the current process has admin privileges (required for MFT reading).
    /// </summary>
    public static bool IsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Enumerate all MFT entries on a given drive letter.
    /// Returns a dictionary mapping FileReferenceNumber to MftEntry.
    /// </summary>
    public static Dictionary<long, MftEntry>? EnumerateMft(
        char driveLetter, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var volumePath = $"\\\\.\\{driveLetter}:";
        var handle = CreateFileW(volumePath, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            progress?.Report(new ScanProgress(
                $"Cannot open volume {driveLetter}: (error {err}). Run as Administrator.", 0, 0));
            return null;
        }

        try
        {
            // Query the USN journal to get the HighUsn
            if (!DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero, 0, out USN_JOURNAL_DATA_V0 journalData,
                Marshal.SizeOf<USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero))
            {
                progress?.Report(new ScanProgress(
                    $"Cannot query USN journal on {driveLetter}: - volume may not be NTFS", 0, 0));
                return null;
            }

            var entries = new Dictionary<long, MftEntry>(500_000);
            var enumData = new MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = journalData.NextUsn
            };

            const int bufferSize = 128 * 1024; // 128KB buffer
            var buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                var count = 0;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!DeviceIoControl(handle, FSCTL_ENUM_USN_DATA,
                        ref enumData, Marshal.SizeOf<MFT_ENUM_DATA_V0>(),
                        buffer, bufferSize, out int bytesReturned, IntPtr.Zero))
                    {
                        break; // No more data
                    }

                    if (bytesReturned <= 8) break;

                    // First 8 bytes = next StartFileReferenceNumber
                    enumData.StartFileReferenceNumber = Marshal.ReadInt64(buffer);

                    var offset = 8; // Skip the 8-byte next-USN value

                    while (offset < bytesReturned)
                    {
                        var recordPtr = IntPtr.Add(buffer, offset);
                        var recordLength = Marshal.ReadInt32(recordPtr);
                        if (recordLength <= 0) break;

                        var record = Marshal.PtrToStructure<USN_RECORD_V2>(recordPtr);
                        var fileNamePtr = IntPtr.Add(recordPtr, record.FileNameOffset);
                        var fileName = Marshal.PtrToStringUni(fileNamePtr, record.FileNameLength / 2);

                        if (fileName != null)
                        {
                            // Mask to 48-bit file reference (remove sequence number)
                            var frn = record.FileReferenceNumber & 0x0000FFFFFFFFFFFF;
                            var parentFrn = record.ParentFileReferenceNumber & 0x0000FFFFFFFFFFFF;

                            entries[frn] = new MftEntry
                            {
                                FileReferenceNumber = frn,
                                ParentFileReferenceNumber = parentFrn,
                                FileName = fileName,
                                IsDirectory = (record.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
                                FileAttributes = record.FileAttributes,
                            };

                            count++;
                            if (count % 100_000 == 0)
                                progress?.Report(new ScanProgress(
                                    $"Reading MFT: {count:N0} entries indexed...", count, 0, true));
                        }

                        offset += recordLength;
                    }
                }

                progress?.Report(new ScanProgress(
                    $"MFT read complete: {count:N0} entries on {driveLetter}:", count, count));

                return entries;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// Reconstruct the full path for an MFT entry by walking up the parent chain.
    /// </summary>
    public static string? ResolvePath(long fileRef, Dictionary<long, MftEntry> entries, char driveLetter)
    {
        var parts = new List<string>();
        var current = fileRef;
        var visited = new HashSet<long>();

        while (entries.TryGetValue(current, out var entry))
        {
            if (!visited.Add(current)) break; // Circular reference guard
            parts.Add(entry.FileName);
            current = entry.ParentFileReferenceNumber;

            // Root directory reference (usually FRN 5)
            if (current == entry.FileReferenceNumber || current < 5)
                break;
        }

        if (parts.Count == 0) return null;

        parts.Reverse();
        return $"{driveLetter}:\\{string.Join('\\', parts)}";
    }

    /// <summary>
    /// Build a full path lookup for all entries (parallelized).
    /// Returns a dictionary of FileReferenceNumber -> full path.
    /// </summary>
    public static ConcurrentDictionary<long, string> BuildPathIndex(
        Dictionary<long, MftEntry> entries, char driveLetter,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var paths = new ConcurrentDictionary<long, string>();
        var done = 0;
        var total = entries.Count;

        // Use a path cache to avoid re-resolving parent chains
        var cache = new ConcurrentDictionary<long, string>();

        Parallel.ForEach(entries, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct,
        },
        kvp =>
        {
            var path = ResolvePathCached(kvp.Key, entries, driveLetter, cache);
            if (path != null)
                paths[kvp.Key] = path;

            var d = Interlocked.Increment(ref done);
            if (d % 200_000 == 0)
                progress?.Report(new ScanProgress(
                    $"Building paths: {d:N0}/{total:N0}", d, total));
        });

        return paths;
    }

    private static string? ResolvePathCached(long fileRef,
        Dictionary<long, MftEntry> entries, char driveLetter,
        ConcurrentDictionary<long, string> cache)
    {
        if (cache.TryGetValue(fileRef, out var cached))
            return cached;

        var parts = new List<string>();
        var current = fileRef;
        var visited = new HashSet<long>();

        while (entries.TryGetValue(current, out var entry))
        {
            if (cache.TryGetValue(current, out var partial))
            {
                parts.Reverse();
                var result = partial + (parts.Count > 0 ? "\\" + string.Join('\\', parts) : "");
                cache[fileRef] = result;
                return result;
            }

            if (!visited.Add(current)) break;
            parts.Add(entry.FileName);
            current = entry.ParentFileReferenceNumber;

            if (current == entry.FileReferenceNumber || current < 5)
                break;
        }

        if (parts.Count == 0) return null;

        parts.Reverse();
        var fullPath = $"{driveLetter}:\\{string.Join('\\', parts)}";
        cache[fileRef] = fullPath;
        return fullPath;
    }
}
