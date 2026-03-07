using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

public sealed class ZeroLengthFileScanner : ScannerBase
{
    public override string Name => "Zero-Length Files";
    public override string Description => "Find empty files with 0 bytes";
    public override string IconGlyph => "\uE7C3"; // Storage icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 500),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var scanned = 0;

        await Task.Run(() => ScanDir(config.RootPath, 0, config, ref scanned, progress, ct), ct);

        progress.Report(new ScanProgress($"Found {_results.Count} zero-length files", _results.Count, _results.Count));
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

                    if (entry.SizeBytes == 0)
                    {
                        _results.Add(new ScanResultItem
                        {
                            FullPath = entry.FullPath,
                            Name = entry.Name,
                            SizeBytes = 0,
                            Modified = entry.LastWriteTime,
                            Detail = Path.GetExtension(entry.Name),
                        });
                    }
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
