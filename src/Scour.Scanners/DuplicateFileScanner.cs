using System.Collections.Concurrent;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;
using Scour.Core.Services;

namespace Scour.Scanners;

public sealed class DuplicateFileScanner : ScannerBase
{
    public override string Name => "Duplicate Files";
    public override string Description => "Find duplicate files by content hash";
    public override string IconGlyph => "\uE8C8"; // Copy icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Group", nameof(ScanResultItem.Group), 100),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();

        // Phase 1: Walk filesystem and group by size
        progress.Report(new ScanProgress("Scanning files...", 0, 0, true));

        var sizeGroups = new ConcurrentDictionary<long, ConcurrentBag<string>>();
        var fileCount = 0;

        await Task.Run(() =>
        {
            ScanDirectory(config.RootPath, 0, config, sizeGroups, ref fileCount, progress, ct);
        }, ct);

        // Filter to only sizes with 2+ files
        var candidates = sizeGroups
            .Where(g => g.Value.Count >= 2)
            .ToList();

        var totalCandidates = candidates.Sum(g => g.Value.Count);
        progress.Report(new ScanProgress($"Found {totalCandidates} candidate files in {candidates.Count} size groups", 0, totalCandidates));

        // Phase 2: Partial hash (first 4KB)
        var partialHashGroups = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        var hashed = 0;

        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        }, async (sizeGroup, token) =>
        {
            foreach (var filePath in sizeGroup.Value)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var hash = await FileHasher.ComputePartialHashAsync(filePath, token);
                    var key = $"{sizeGroup.Key}:{FileHasher.HashToHex(hash)}";
                    partialHashGroups.GetOrAdd(key, _ => []).Add(filePath);
                }
                catch { /* skip unreadable files */ }

                var count = Interlocked.Increment(ref hashed);
                if (count % 100 == 0)
                    progress.Report(new ScanProgress($"Partial hashing... {count}/{totalCandidates}", count, totalCandidates));
            }
        });

        // Phase 3: Full hash only on partial-hash collisions
        var fullHashCandidates = partialHashGroups
            .Where(g => g.Value.Count >= 2)
            .ToList();

        var fullTotal = fullHashCandidates.Sum(g => g.Value.Count);
        var fullHashed = 0;
        progress.Report(new ScanProgress($"Full hashing {fullTotal} files...", 0, fullTotal));

        var fullHashGroups = new ConcurrentDictionary<string, ConcurrentBag<(string Path, long Size, DateTime Modified)>>();

        await Parallel.ForEachAsync(fullHashCandidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        }, async (group, token) =>
        {
            foreach (var filePath in group.Value)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var hash = await FileHasher.ComputeFullHashAsync(filePath, token);
                    var key = FileHasher.HashToHex(hash);
                    var info = new FileInfo(filePath);
                    fullHashGroups.GetOrAdd(key, _ => []).Add((filePath, info.Length, info.LastWriteTime));
                }
                catch { }

                var count = Interlocked.Increment(ref fullHashed);
                if (count % 50 == 0)
                    progress.Report(new ScanProgress($"Full hashing... {count}/{fullTotal}", count, fullTotal));
            }
        });

        // Phase 4: Build results - duplicates only
        var groupNum = 0;
        foreach (var group in fullHashGroups.Where(g => g.Value.Count >= 2).OrderByDescending(g => g.Value.First().Size))
        {
            groupNum++;
            var files = group.Value.OrderBy(f => f.Modified).ToList();
            var isFirst = true;

            foreach (var file in files)
            {
                AddResult(new ScanResultItem
                {
                    FullPath = file.Path,
                    Name = Path.GetFileName(file.Path),
                    SizeBytes = file.Size,
                    Modified = file.Modified,
                    Group = $"Group {groupNum}",
                    Detail = group.Key[..16] + "...",
                    IsSelected = !isFirst, // keep oldest, select rest for deletion
                });
                isFirst = false;
            }
        }

        var dupeCount = _results.Count(r => r.IsSelected);
        var savedBytes = _results.Where(r => r.IsSelected).Sum(r => r.SizeBytes);
        var saved = new ScanResultItem { FullPath = "", SizeBytes = savedBytes }.SizeFormatted;
        progress.Report(new ScanProgress(
            $"Found {dupeCount} duplicate files ({saved} recoverable) in {groupNum} groups",
            _results.Count, _results.Count));
    }

    private void ScanDirectory(string path, int depth, ScanConfig config,
        ConcurrentDictionary<long, ConcurrentBag<string>> sizeGroups,
        ref int fileCount, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (config.MaxDepth >= 0 && depth > config.MaxDepth)
            return;

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

                    ScanDirectory(entry.FullPath, depth + 1, config, sizeGroups, ref fileCount, progress, ct);
                }
                else
                {
                    if (entry.SizeBytes == 0) continue;
                    if (config.MinFileSizeBytes > 0 && entry.SizeBytes < config.MinFileSizeBytes) continue;
                    if (config.MaxFileSizeBytes > 0 && entry.SizeBytes > config.MaxFileSizeBytes) continue;

                    sizeGroups.GetOrAdd(entry.SizeBytes, _ => []).Add(entry.FullPath);
                    fileCount++;

                    if (fileCount % 1000 == 0)
                        progress.Report(new ScanProgress($"Scanning: {path}", fileCount, 0, true));
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
