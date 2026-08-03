using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

public sealed class BigFileScanner : ScannerBase
{
    private int _topN = 100;

    public FolderSizeNode? FolderSizeRoot { get; private set; }

    public override string Name => "Big Files";
    public override string Description => "Find the largest files consuming disk space";
    public override string IconGlyph => "\uE7C3"; // Storage icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Size", nameof(ScanResultItem.SizeFormatted), 100, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
    ];

    public override void Reset()
    {
        base.Reset();
        FolderSizeRoot = null;
    }

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        FolderSizeRoot = new FolderSizeNode(config.RootPath, config.RootPath);

        var heap = new SortedSet<(long Size, string Path, DateTime Modified)>(
            Comparer<(long Size, string Path, DateTime Modified)>.Create((a, b) =>
            {
                var cmp = a.Size.CompareTo(b.Size);
                return cmp != 0 ? cmp : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            }));

        var scanned = 0;

        await Task.Run(() => ScanDir(config.RootPath, 0, config, heap, ref scanned, FolderSizeRoot, progress, ct), ct);

        foreach (var entry in heap.Reverse())
        {
            AddResult(new ScanResultItem
            {
                FullPath = entry.Path,
                Name = Path.GetFileName(entry.Path),
                SizeBytes = entry.Size,
                Modified = entry.Modified,
                IsSelected = false,
            });
        }

        var totalSize = FolderSizeRoot.SizeBytes;
        var fmt = new ScanResultItem { FullPath = "", SizeBytes = totalSize }.SizeFormatted;
        progress.Report(new ScanProgress($"Found top {_results.Count} files ({fmt} total)", _results.Count, _results.Count));
    }

    private long ScanDir(string path, int depth, ScanConfig config,
        SortedSet<(long, string, DateTime)> heap, ref int scanned,
        FolderSizeNode node, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (config.MaxDepth >= 0 && depth > config.MaxDepth) return 0;

        var totalSize = 0L;
        var directFileSize = 0L;
        var directFileCount = 0;

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
                    var child = new FolderSizeNode(entry.Name, entry.FullPath);
                    var childSize = ScanDir(entry.FullPath, depth + 1, config, heap, ref scanned, child, progress, ct);
                    if (childSize > 0)
                    {
                        child.SizeBytes = childSize;
                        node.Children.Add(child);
                        totalSize = SaturatingAdd(totalSize, childSize);
                    }
                }
                else
                {
                    scanned++;
                    if (scanned % 1000 == 0)
                        progress.Report(new ScanProgress($"Scanning: {path}", scanned, 0, true));

                    heap.Add((entry.SizeBytes, entry.FullPath, entry.LastWriteTime));
                    if (heap.Count > _topN)
                        heap.Remove(heap.Min);

                    directFileCount++;
                    directFileSize = SaturatingAdd(directFileSize, entry.SizeBytes);
                    totalSize = SaturatingAdd(totalSize, entry.SizeBytes);
                }
            }
        }
        catch (UnauthorizedAccessException) { return totalSize; }
        catch (IOException) { return totalSize; }

        if (directFileSize > 0)
        {
            node.Children.Insert(0, new FolderSizeNode($"{directFileCount:N0} files", node.FullPath, true)
            {
                SizeBytes = directFileSize
            });
        }

        node.SizeBytes = totalSize;
        return totalSize;
    }

    private static long SaturatingAdd(long left, long right)
        => right > long.MaxValue - left ? long.MaxValue : left + right;
}
