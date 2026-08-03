using System.Collections.Concurrent;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;
using Scour.Core.Services;

namespace Scour.Scanners;

public sealed class MediaDuplicateScanner : ScannerBase
{
    private const int ImageDistanceThreshold = 10;
    private const int VideoDistanceThreshold = 8;
    private const int DimensionBucketSize = 256;

    public override string Name => "Media Duplicates";
    public override string Description => "Find near-duplicate photos and videos by perceptual fingerprint";
    public override string IconGlyph => "\uE91B"; // Photo icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Dimensions", nameof(ScanResultItem.Detail), 150),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Group", nameof(ScanResultItem.Group), 100),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        progress.Report(new ScanProgress("Scanning media files...", 0, 0, true));

        var mediaFiles = new List<FileEntry>();
        var scanned = 0;
        var reader = new FileSystemWalker(config).WalkAsync(ct);

        await foreach (var entry in reader.ReadAllAsync(ct))
        {
            if (entry.IsDirectory || !MediaFingerprint.IsSupported(entry.FullPath))
                continue;

            scanned++;
            if (scanned % 250 == 0)
                progress.Report(new ScanProgress($"Found {scanned} media files...", scanned, 0, true));

            mediaFiles.Add(entry);
        }

        var candidates = new ConcurrentBag<MediaCandidate>();
        var processed = 0;
        await Parallel.ForEachAsync(mediaFiles, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct,
        }, (entry, token) =>
        {
            token.ThrowIfCancellationRequested();
            var fingerprint = MediaFingerprint.Compute(entry.FullPath);
            if (fingerprint != null)
                candidates.Add(new MediaCandidate(entry, fingerprint));

            var count = Interlocked.Increment(ref processed);
            if (count % 100 == 0)
                progress.Report(new ScanProgress($"Fingerprinting media... {count}/{mediaFiles.Count}", count, mediaFiles.Count));

            return ValueTask.CompletedTask;
        });

        var groups = FindNearDuplicateGroups(candidates, ct);
        var groupNumber = 0;
        foreach (var group in groups.OrderByDescending(g => g.Sum(item => item.Entry.SizeBytes)))
        {
            ct.ThrowIfCancellationRequested();
            groupNumber++;

            var ordered = group
                .OrderByDescending(item => item.Entry.SizeBytes)
                .ThenBy(item => item.Entry.LastWriteTime)
                .ThenBy(item => item.Entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keep = true;
            foreach (var item in ordered)
            {
                AddResult(new ScanResultItem
                {
                    FullPath = item.Entry.FullPath,
                    Name = item.Entry.Name,
                    SizeBytes = item.Entry.SizeBytes,
                    Modified = item.Entry.LastWriteTime,
                    Group = $"Group {groupNumber}",
                    Detail = $"{item.Fingerprint.Kind} · {item.Fingerprint.Dimensions}",
                    IsSelected = !keep,
                });
                keep = false;
            }
        }

        var selectedBytes = _results.Where(item => item.IsSelected).Sum(item => item.SizeBytes);
        var saved = new ScanResultItem { FullPath = "", SizeBytes = selectedBytes }.SizeFormatted;
        progress.Report(new ScanProgress(
            $"Found {_results.Count(item => item.IsSelected)} near-duplicates ({saved} recoverable) in {groupNumber} groups",
            _results.Count,
            _results.Count));
    }

    private static IReadOnlyList<IReadOnlyList<MediaCandidate>> FindNearDuplicateGroups(
        IEnumerable<MediaCandidate> candidates,
        CancellationToken ct)
    {
        var buckets = candidates
            .GroupBy(GetDimensionBucket)
            .Where(group => group.Count() > 1)
            .ToList();
        var groups = new List<IReadOnlyList<MediaCandidate>>();

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();
            var entries = bucket.ToList();
            var unionFind = new UnionFind(entries.Count);
            var hashBands = new Dictionary<(int Band, ushort Value), List<int>>();

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                for (var band = 0; band < 4; band++)
                {
                    var value = (ushort)((entry.Fingerprint.PerceptualBucket >> (band * 16)) & ushort.MaxValue);
                    var key = (band, value);
                    if (!hashBands.TryGetValue(key, out var prior))
                    {
                        prior = [];
                        hashBands[key] = prior;
                    }

                    foreach (var candidateIndex in prior)
                    {
                        var candidate = entries[candidateIndex];
                        var threshold = entry.Fingerprint.Kind == "Image"
                            ? ImageDistanceThreshold
                            : VideoDistanceThreshold;
                        if (MediaFingerprint.HammingDistance(
                                entry.Fingerprint.PerceptualBucket,
                                candidate.Fingerprint.PerceptualBucket) <= threshold)
                        {
                            unionFind.Union(index, candidateIndex);
                        }
                    }

                    prior.Add(index);
                }
            }

            groups.AddRange(entries
                .Select((entry, index) => (entry, index))
                .GroupBy(pair => unionFind.Find(pair.index))
                .Select(group => (IReadOnlyList<MediaCandidate>)group.Select(pair => pair.entry).ToList())
                .Where(group => group.Count > 1));
        }

        return groups;
    }

    private static (string Kind, int Width, int Height) GetDimensionBucket(MediaCandidate candidate)
    {
        var fingerprint = candidate.Fingerprint;
        return (
            fingerprint.Kind,
            fingerprint.Width > 0 ? fingerprint.Width / DimensionBucketSize : 0,
            fingerprint.Height > 0 ? fingerprint.Height / DimensionBucketSize : 0);
    }

    private sealed record MediaCandidate(FileEntry Entry, MediaFingerprintInfo Fingerprint);

    private sealed class UnionFind(int count)
    {
        private readonly int[] _parents = Enumerable.Range(0, count).ToArray();
        private readonly byte[] _ranks = new byte[count];

        public int Find(int value)
        {
            while (_parents[value] != value)
            {
                _parents[value] = _parents[_parents[value]];
                value = _parents[value];
            }

            return value;
        }

        public void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left == right) return;

            if (_ranks[left] < _ranks[right])
                (left, right) = (right, left);

            _parents[right] = left;
            if (_ranks[left] == _ranks[right])
                _ranks[left]++;
        }
    }
}
