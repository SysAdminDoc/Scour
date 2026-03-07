using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Scour.Core.Native;

/// <summary>
/// High-performance filesystem enumeration using Win32 FindFirstFileW/FindNextFileW.
/// Bypasses .NET's System.IO overhead for scanning millions of entries.
/// </summary>
public static class Win32FileSystem
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_HIDDEN = 0x02;
    private const uint FILE_ATTRIBUTE_SYSTEM = 0x04;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    /// <summary>
    /// Enumerate all files and subdirectories in a directory using Win32 API.
    /// </summary>
    public static IEnumerable<FileEntry> EnumerateEntries(string path)
    {
        // Ensure long path prefix
        var searchPath = EnsureLongPath(path) + @"\*";

        var handle = FindFirstFileW(searchPath, out var data);
        if (handle.IsInvalid)
            yield break;

        try
        {
            do
            {
                if (data.cFileName is "." or "..")
                    continue;

                yield return new FileEntry
                {
                    Name = data.cFileName,
                    FullPath = Path.Combine(path, data.cFileName),
                    IsDirectory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0,
                    IsHidden = (data.dwFileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0,
                    IsSystem = (data.dwFileAttributes & FILE_ATTRIBUTE_SYSTEM) != 0,
                    IsReparsePoint = (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0,
                    SizeBytes = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                    LastWriteTime = FileTimeToDateTime(data.ftLastWriteTime),
                    Attributes = data.dwFileAttributes
                };
            }
            while (FindNextFileW(handle, out data));
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// Get only subdirectories (faster than full enumeration when files aren't needed).
    /// </summary>
    public static IEnumerable<FileEntry> EnumerateDirectories(string path)
    {
        return EnumerateEntries(path).Where(e => e.IsDirectory);
    }

    /// <summary>
    /// Get only files.
    /// </summary>
    public static IEnumerable<FileEntry> EnumerateFiles(string path)
    {
        return EnumerateEntries(path).Where(e => !e.IsDirectory);
    }

    /// <summary>
    /// Send a file or directory to the Recycle Bin.
    /// </summary>
    public static bool SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + '\0' + '\0', // double-null terminated
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        };
        return SHFileOperation(ref op) == 0;
    }

    /// <summary>
    /// Permanently delete a directory (must be empty).
    /// </summary>
    public static void DeleteDirectory(string path)
    {
        Directory.Delete(EnsureLongPath(path), false);
    }

    /// <summary>
    /// Permanently delete a file.
    /// </summary>
    public static void DeleteFile(string path)
    {
        File.Delete(EnsureLongPath(path));
    }

    public static string EnsureLongPath(string path)
    {
        if (path.StartsWith(@"\\?\"))
            return path;
        if (path.StartsWith(@"\\"))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    private static DateTime FileTimeToDateTime(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        var ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        if (ticks <= 0) return default;
        try { return DateTime.FromFileTimeUtc(ticks).ToLocalTime(); }
        catch { return default; }
    }
}

public sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle() : base(true) { }

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr hFindFile);

    protected override bool ReleaseHandle() => FindClose(handle);
}

public struct FileEntry
{
    public string Name;
    public string FullPath;
    public bool IsDirectory;
    public bool IsHidden;
    public bool IsSystem;
    public bool IsReparsePoint;
    public long SizeBytes;
    public DateTime LastWriteTime;
    public uint Attributes;
}
