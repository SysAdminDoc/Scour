using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Services;

namespace Scour.Scanners;

public sealed class EmptyDirectoryScanner : ScannerBase
{
    public override string Name => "Empty Folders";
    public override string Description => "Find and remove empty directories";
    public override string IconGlyph => "\uE8B7"; // Folder icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Path", nameof(ScanResultItem.FullPath), 500),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Detail", nameof(ScanResultItem.Detail), 200),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var walker = new FileSystemWalker(config);

        progress.Report(new ScanProgress("Scanning directories...", 0, 0, true));
        var allDirs = await walker.GetAllDirectoriesAsync(progress, ct);

        progress.Report(new ScanProgress("Analyzing...", 0, allDirs.Count));

        // Build parent->children lookup
        var childMap = new Dictionary<string, List<Core.Services.DirectoryInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in allDirs)
        {
            var parent = Path.GetDirectoryName(dir.FullPath) ?? "";
            if (!childMap.TryGetValue(parent, out var list))
            {
                list = [];
                childMap[parent] = list;
            }
            list.Add(dir);
        }

        // Bottom-up: a directory is empty if it has no non-ignored files
        // AND all its subdirectories are also empty
        var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Sort by depth descending (leaves first)
        allDirs.Sort((a, b) => b.Depth.CompareTo(a.Depth));

        var processed = 0;
        foreach (var dir in allDirs)
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            if (processed % 50 == 0)
                progress.Report(new ScanProgress($"Analyzing: {dir.FullPath}", processed, allDirs.Count));

            // Skip root
            if (string.Equals(dir.FullPath, config.RootPath, StringComparison.OrdinalIgnoreCase))
                continue;

            // Has non-ignored files? Not empty.
            if (dir.HasNonIgnoredFiles)
                continue;

            // Check if all subdirectories are in the empty set
            var allSubsEmpty = true;
            if (childMap.TryGetValue(dir.FullPath, out var children))
            {
                foreach (var child in children)
                {
                    if (!emptySet.Contains(child.FullPath))
                    {
                        allSubsEmpty = false;
                        break;
                    }
                }
            }

            if (!allSubsEmpty) continue;

            emptySet.Add(dir.FullPath);

            var detail = dir.FileCount > 0 ? $"{dir.FileCount} ignored file(s)" : "Empty";

            AddResult(new ScanResultItem
            {
                FullPath = dir.FullPath,
                Name = dir.Name,
                IsDirectory = true,
                Modified = System.IO.Directory.GetLastWriteTime(dir.FullPath),
                Detail = detail,
            });
        }

        progress.Report(new ScanProgress($"Found {_results.Count} empty directories", _results.Count, _results.Count));
    }
}
