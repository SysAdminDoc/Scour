using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

public sealed class OldFileScanner : ScannerBase
{
    private static readonly int DefaultStaleDays = 365;

    public override string Name => "Old Files";
    public override string Description => "Find files not modified in over a year";
    public override string IconGlyph => "\uE823"; // Clock icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Age", nameof(ScanResultItem.Detail), 120),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var scanned = 0;
        var cutoff = DateTime.Now.AddDays(-DefaultStaleDays);

        await Task.Run(() => ScanDir(config.RootPath, 0, config, cutoff, ref scanned, progress, ct), ct);

        // Sort oldest first
        _results.Sort((a, b) => a.Modified.CompareTo(b.Modified));

        var totalSize = _results.Sum(r => r.SizeBytes);
        var fmt = new ScanResultItem { FullPath = "", SizeBytes = totalSize }.SizeFormatted;
        progress.Report(new ScanProgress($"Found {_results.Count} old files ({fmt})", _results.Count, _results.Count));
    }

    private void ScanDir(string path, int depth, ScanConfig config, DateTime cutoff,
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
                    ScanDir(entry.FullPath, depth + 1, config, cutoff, ref scanned, progress, ct);
                }
                else
                {
                    scanned++;
                    if (scanned % 1000 == 0)
                        progress.Report(new ScanProgress($"Scanning: {path}", scanned, 0, true));

                    if (entry.SizeBytes == 0 && config.Ignore0KbFiles) continue;

                    if (entry.LastWriteTime < cutoff && entry.LastWriteTime != default)
                    {
                        var age = DateTime.Now - entry.LastWriteTime;
                        var ageStr = age.TotalDays >= 365
                            ? $"{age.TotalDays / 365:F1} years"
                            : $"{(int)age.TotalDays} days";

                        _results.Add(new ScanResultItem
                        {
                            FullPath = entry.FullPath,
                            Name = entry.Name,
                            SizeBytes = entry.SizeBytes,
                            Modified = entry.LastWriteTime,
                            Detail = ageStr,
                            IsSelected = false, // don't auto-select for safety
                        });
                    }
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
