using System.Collections.Concurrent;
using Scour.Core;
using Scour.Core.Interfaces;

namespace Scour.Scanners;

/// <summary>
/// Finds disposable browser cache data while preserving profile databases,
/// cookies, history, and other user state.
/// </summary>
public sealed class BrowserCacheScanner : ScannerBase
{
    private static readonly BrowserDefinition[] ChromiumBrowsers =
    [
        new("Chrome", ["Google", "Chrome", "User Data"]),
        new("Edge", ["Microsoft", "Edge", "User Data"]),
        new("Brave", ["BraveSoftware", "Brave-Browser", "User Data"]),
    ];

    private static readonly (string Name, string[] RelativePath)[] ChromiumCacheDirectories =
    [
        ("Cache", ["Cache"]),
        ("Network Cache", ["Network", "Cache"]),
        ("Code Cache", ["Code Cache"]),
        ("GPU Cache", ["GPUCache"]),
        ("Service Worker Cache", ["Service Worker", "CacheStorage"]),
    ];

    private static readonly string[][] FirefoxCacheDirectories =
    [
        ["cache2"],
        ["startupCache"],
        ["thumbnails"],
    ];

    private readonly string _localAppData;
    private readonly string _roamingAppData;

    public BrowserCacheScanner(string? localAppData = null, string? roamingAppData = null)
    {
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _roamingAppData = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    public override string Name => "Browser Cache";
    public override string Description => "Clear disposable cache data by browser profile";
    public override string IconGlyph => "\uE774"; // Globe icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Cache", nameof(ScanResultItem.Name), 180),
        new("Path", nameof(ScanResultItem.FullPath), 420),
        new("Size", nameof(ScanResultItem.SizeFormatted), 100, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Profile", nameof(ScanResultItem.Detail), 300),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var locations = DiscoverCacheLocations();
        progress.Report(new ScanProgress($"Found {locations.Count} browser cache locations", 0, locations.Count));

        if (locations.Count == 0)
        {
            progress.Report(new ScanProgress("No supported browser profiles found", 0, 0));
            return;
        }

        var findings = new ConcurrentBag<CacheFinding>();
        var processed = 0;
        await Parallel.ForEachAsync(locations, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct,
        }, (location, token) =>
        {
            token.ThrowIfCancellationRequested();
            var stats = GetDirectoryStats(location.Path);
            if (stats.FileCount > 0)
                findings.Add(new CacheFinding(location, stats));

            var count = Interlocked.Increment(ref processed);
            if (count % 5 == 0)
                progress.Report(new ScanProgress($"Measured {count}/{locations.Count} cache locations", count, locations.Count));

            return ValueTask.CompletedTask;
        });

        foreach (var finding in findings
            .OrderBy(item => item.Location.Browser, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Location.Profile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Location.CacheName, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            AddResult(new ScanResultItem
            {
                FullPath = finding.Location.Path,
                Name = finding.Location.CacheName,
                SizeBytes = finding.Stats.SizeBytes,
                Modified = finding.Stats.LastModified,
                IsDirectory = true,
                Detail = $"{finding.Location.Browser} · {finding.Location.Profile} · {finding.Stats.FileCount:N0} files",
            });
        }

        var totalBytes = _results.Sum(item => item.SizeBytes);
        var formatted = new ScanResultItem { FullPath = "", SizeBytes = totalBytes }.SizeFormatted;
        progress.Report(new ScanProgress(
            $"Found {_results.Count} browser cache locations ({formatted})",
            _results.Count,
            _results.Count));
    }

    private List<CacheLocation> DiscoverCacheLocations()
    {
        var locations = new List<CacheLocation>();

        foreach (var browser in ChromiumBrowsers)
        {
            var userDataPath = Combine(_localAppData, browser.RelativePath);
            foreach (var profilePath in EnumerateDirectories(userDataPath))
            {
                var profileName = Path.GetFileName(profilePath);
                if (!IsChromiumProfile(profileName)) continue;

                foreach (var cache in ChromiumCacheDirectories)
                {
                    var cachePath = Combine(profilePath, cache.RelativePath);
                    if (Directory.Exists(cachePath))
                        locations.Add(new CacheLocation(browser.Name, profileName, cache.Name, cachePath));
                }
            }
        }

        var firefoxRoots = new[]
        {
            Combine(_roamingAppData, ["Mozilla", "Firefox", "Profiles"]),
            Combine(_localAppData, ["Mozilla", "Firefox", "Profiles"]),
        };

        foreach (var profilesRoot in firefoxRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var profilePath in EnumerateDirectories(profilesRoot))
            {
                var profileName = Path.GetFileName(profilePath);
                foreach (var cache in FirefoxCacheDirectories)
                {
                    var cachePath = Combine(profilePath, cache);
                    if (Directory.Exists(cachePath))
                        locations.Add(new CacheLocation("Firefox", profileName, cache[0], cachePath));
                }
            }
        }

        return locations
            .GroupBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsChromiumProfile(string name)
        => name.Equals("Default", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase);

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

    private static string Combine(string root, IEnumerable<string> segments)
        => segments.Aggregate(root, Path.Combine);

    private static FolderStats GetDirectoryStats(string path)
    {
        long size = 0;
        var files = 0;
        var lastModified = Directory.GetLastWriteTime(path);

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                try
                {
                    var info = new FileInfo(file);
                    size = size > long.MaxValue - info.Length ? long.MaxValue : size + info.Length;
                    files++;
                    if (info.LastWriteTime > lastModified)
                        lastModified = info.LastWriteTime;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new FolderStats(size, files, lastModified);
    }

    private sealed record BrowserDefinition(string Name, string[] RelativePath);

    private sealed record CacheLocation(string Browser, string Profile, string CacheName, string Path);

    private sealed record CacheFinding(CacheLocation Location, FolderStats Stats);

    private sealed record FolderStats(long SizeBytes, int FileCount, DateTime LastModified);
}
