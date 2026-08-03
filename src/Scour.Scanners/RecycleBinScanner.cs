using System.Buffers.Binary;
using System.Security;
using System.Security.Principal;
using System.Text;
using Scour.Core;
using Scour.Core.Interfaces;

namespace Scour.Scanners;

/// <summary>
/// Reads Windows Recycle Bin metadata across volumes without changing its
/// contents. The $I record carries the original path, SID-owned bin, size,
/// and deletion timestamp while the matching $R entry carries the data.
/// </summary>
public sealed class RecycleBinScanner : ScannerBase
{
    private readonly IReadOnlyList<string>? _volumeRoots;

    public RecycleBinScanner(IEnumerable<string>? volumeRoots = null)
    {
        _volumeRoots = volumeRoots?.ToList();
    }

    public override string Name => "Recycle Bin";
    public override string Description => "Inspect deleted items by volume, original path, and user SID";
    public override string IconGlyph => "\uE74D"; // Delete icon
    public override string DeleteActionLabel => "\uE8B5  Read-only Inspection";

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Original name", nameof(ScanResultItem.Name), 240),
        new("Recycle path", nameof(ScanResultItem.FullPath), 430),
        new("Size", nameof(ScanResultItem.SizeFormatted), 100, true),
        new("Deleted", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Original path / SID", nameof(ScanResultItem.Detail), 460),
    ];

    public override Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var roots = _volumeRoots ?? DiscoverVolumeRoots();
        progress.Report(new ScanProgress($"Inspecting {roots.Count} volume recycle bins...", 0, roots.Count));

        var volumeIndex = 0;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            ScanVolume(root, ct);
            volumeIndex++;
            progress.Report(new ScanProgress($"Checked {volumeIndex}/{roots.Count} recycle bins", volumeIndex, roots.Count));
        }

        var totalBytes = _results.Sum(item => item.SizeBytes);
        var formatted = new ScanResultItem { FullPath = "", SizeBytes = totalBytes }.SizeFormatted;
        progress.Report(new ScanProgress(
            _results.Count == 0
                ? "Recycle Bins are empty or inaccessible"
                : $"Found {_results.Count} deleted item(s) ({formatted}); inspection is read-only",
            _results.Count,
            _results.Count));
        return Task.CompletedTask;
    }

    public override Task DeleteSelectedAsync(
        IEnumerable<ScanResultItem> items,
        DeleteMode mode,
        IProgress<ScanProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new ScanProgress("Recycle Bin inspection is read-only; no items were changed", 0, 0));
        return Task.CompletedTask;
    }

    private void ScanVolume(string volumeRoot, CancellationToken ct)
    {
        var recycleRoot = Path.Combine(volumeRoot, "$Recycle.Bin");
        foreach (var sidDirectory in EnumerateDirectories(recycleRoot))
        {
            ct.ThrowIfCancellationRequested();
            var sid = Path.GetFileName(sidDirectory);
            var account = ResolveSid(sid);

            foreach (var infoPath in EnumerateFiles(sidDirectory, "$I*"))
            {
                ct.ThrowIfCancellationRequested();
                var record = ParseInfoRecord(infoPath);
                if (record == null) continue;

                var suffix = Path.GetFileName(infoPath)[2..];
                var dataPath = Path.Combine(sidDirectory, "$R" + suffix);
                var originalName = string.IsNullOrWhiteSpace(record.OriginalPath)
                    ? Path.GetFileName(dataPath)
                    : Path.GetFileName(record.OriginalPath);
                var recyclePath = File.Exists(dataPath) || Directory.Exists(dataPath) ? dataPath : infoPath;

                AddResult(new ScanResultItem
                {
                    FullPath = recyclePath,
                    Name = originalName,
                    SizeBytes = record.OriginalSize,
                    Modified = record.DeletedAt,
                    Detail = $"Original: {record.OriginalPath} · SID: {sid} ({account})",
                    IsDirectory = Directory.Exists(dataPath),
                    IsSelected = false,
                });
            }
        }
    }

    private static List<string> DiscoverVolumeRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                    roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return roots;
    }

    private static RecycleInfo? ParseInfoRecord(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 28) return null;

            var version = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0, 8));
            if (version == 0) return null;

            var size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8, 8));
            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16, 8));
            var pathLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24, 4));
            var availableCharacters = (data.Length - 28) / 2;
            var characters = Math.Clamp(pathLength, 0, availableCharacters);
            var originalPath = characters == 0
                ? ""
                : Encoding.Unicode.GetString(data, 28, characters * 2).TrimEnd('\0');

            var deletedAt = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            return new RecycleInfo(Math.Max(0, size), deletedAt, originalPath);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ResolveSid(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid)
                .Translate(typeof(NTAccount))
                .Value;
        }
        catch (IdentityNotMappedException)
        {
            return "unresolved";
        }
        catch (ArgumentException)
        {
            return "unresolved";
        }
        catch (SecurityException)
        {
            return "unresolved";
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed record RecycleInfo(long OriginalSize, DateTime DeletedAt, string OriginalPath);
}
