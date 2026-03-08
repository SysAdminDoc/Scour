using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

public sealed class TempFileScanner : ScannerBase
{
    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".bak", ".old", ".orig",
        ".log", ".~", ".swp", ".swo",
        ".dmp", ".crash", ".stackdump",
        ".chk", ".gid", ".cnt",
        ".cache", ".lck", ".lock",
    };

    public override string Name => "Temp Files";
    public override string Description => "Find temporary and junk files";
    public override string IconGlyph => "\uE74D"; // Delete icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Type", nameof(ScanResultItem.Detail), 120),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var scanned = 0;

        await Task.Run(() => ScanDir(config.RootPath, 0, config, ref scanned, progress, ct), ct);

        var totalSize = _results.Sum(r => r.SizeBytes);
        var fmt = new ScanResultItem { FullPath = "", SizeBytes = totalSize }.SizeFormatted;
        progress.Report(new ScanProgress($"Found {_results.Count} temp files ({fmt})", _results.Count, _results.Count));
    }

    private void ScanDir(string path, int depth, ScanConfig config,
        ref int scanned, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (config.MaxDepth >= 0 && depth > config.MaxDepth) return;

        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(path))
            {
                if (entry.IsDirectory)
                {
                    if (entry.IsReparsePoint) continue;
                    if (config.SkipHidden && entry.IsHidden) continue;
                    if (config.SkipSystem && entry.IsSystem) continue;
                    if (config.ExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    ScanDir(entry.FullPath, depth + 1, config, ref scanned, progress, ct);
                }
                else
                {
                    scanned++;
                    if (scanned % 1000 == 0)
                        progress.Report(new ScanProgress($"Scanning: {path}", scanned, 0, true));

                    var reason = GetTempReason(entry);
                    if (reason != null)
                    {
                        AddResult(new ScanResultItem
                        {
                            FullPath = entry.FullPath,
                            Name = entry.Name,
                            SizeBytes = entry.SizeBytes,
                            Modified = entry.LastWriteTime,
                            Detail = reason,
                        });
                    }
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static string? GetTempReason(FileEntry entry)
    {
        var ext = Path.GetExtension(entry.Name);

        if (TempExtensions.Contains(ext))
            return $"Temp extension ({ext})";

        // Check name-based patterns
        var lower = entry.Name.ToLowerInvariant();
        if (lower == "thumbs.db") return "Windows thumbnail cache";
        if (lower == "desktop.ini") return "Windows folder settings";
        if (lower == ".ds_store") return "macOS metadata";
        if (lower.StartsWith("._")) return "macOS resource fork";
        if (lower.StartsWith("~$")) return "Office lock file";
        if (lower.StartsWith("~") && !lower.StartsWith("~$")) return "Temp/backup file";
        if (lower.StartsWith("etilqs_")) return "SQLite temp file";

        return null;
    }
}
