using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

public sealed class BrokenSymlinkScanner : ScannerBase
{
    public override string Name => "Broken Links";
    public override string Description => "Find broken symbolic links and junctions";
    public override string IconGlyph => "\uE71B"; // Link icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 500),
        new("Type", nameof(ScanResultItem.Detail), 140),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var scanned = 0;

        await Task.Run(() => ScanDir(config.RootPath, 0, config, ref scanned, progress, ct), ct);

        progress.Report(new ScanProgress($"Found {_results.Count} broken links", _results.Count, _results.Count));
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
                if (scanned % 1000 == 0)
                    progress.Report(new ScanProgress($"Scanning: {path}", scanned, 0, true));

                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    if (config.SkipHidden && entry.IsHidden) continue;
                    if (config.SkipSystem && entry.IsSystem) continue;
                    if (config.ExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    ScanDir(entry.FullPath, depth + 1, config, ref scanned, progress, ct);
                    continue;
                }

                if (!entry.IsReparsePoint) continue;

                // Check if the target exists
                var isBroken = false;
                var linkType = entry.IsDirectory ? "Junction/Dir symlink" : "File symlink";

                try
                {
                    if (entry.IsDirectory)
                        isBroken = !Directory.Exists(entry.FullPath);
                    else
                        isBroken = !File.Exists(entry.FullPath);
                }
                catch
                {
                    isBroken = true;
                }

                if (isBroken)
                {
                    AddResult(new ScanResultItem
                    {
                        FullPath = entry.FullPath,
                        Name = entry.Name,
                        IsDirectory = entry.IsDirectory,
                        Modified = entry.LastWriteTime,
                        Detail = linkType,
                    });
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
