using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

/// <summary>
/// Finds files and directories with paths exceeding 260 characters (MAX_PATH).
/// These paths can cause issues with older applications and scripts.
/// </summary>
public sealed class LongPathScanner : ScannerBase
{
    private const int MAX_PATH = 260;

    public override string Name => "Long Paths";
    public override string Description => "Find paths exceeding 260 characters (MAX_PATH)";
    public override string IconGlyph => "\uE8B7"; // Rename icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 200),
        new("Path", nameof(ScanResultItem.FullPath), 500),
        new("Length", nameof(ScanResultItem.Detail), 80, true),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 130),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var scanned = 0;

        await Task.Run(() => ScanDir(config.RootPath, 0, config, ref scanned, progress, ct), ct);

        // Sort by path length descending
        _results.Sort((a, b) =>
        {
            var lenA = int.TryParse(a.Detail, out var la) ? la : 0;
            var lenB = int.TryParse(b.Detail, out var lb) ? lb : 0;
            return lenB.CompareTo(lenA);
        });

        progress.Report(new ScanProgress($"Found {_results.Count} long paths", _results.Count, _results.Count));
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
                scanned++;
                if (scanned % 5000 == 0)
                    progress.Report(new ScanProgress($"Scanning: {path}", scanned, 0, true));

                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    if (config.SkipHidden && entry.IsHidden) continue;
                    if (config.SkipSystem && entry.IsSystem) continue;
                    if (config.ExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;

                    // Check directory path length too
                    if (entry.FullPath.Length > MAX_PATH)
                    {
                        _results.Add(new ScanResultItem
                        {
                            FullPath = entry.FullPath,
                            Name = entry.Name,
                            IsDirectory = true,
                            Modified = entry.LastWriteTime,
                            Detail = entry.FullPath.Length.ToString(),
                            Group = "Directory",
                            IsSelected = false,
                        });
                    }

                    ScanDir(entry.FullPath, depth + 1, config, ref scanned, progress, ct);
                    continue;
                }

                if (entry.IsDirectory) continue;

                if (entry.FullPath.Length > MAX_PATH)
                {
                    _results.Add(new ScanResultItem
                    {
                        FullPath = entry.FullPath,
                        Name = entry.Name,
                        SizeBytes = entry.SizeBytes,
                        Modified = entry.LastWriteTime,
                        Detail = entry.FullPath.Length.ToString(),
                        Group = "File",
                        IsSelected = false,
                    });
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
