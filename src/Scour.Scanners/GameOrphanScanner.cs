using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security;
using Microsoft.Win32;
using Scour.Core;
using Scour.Core.Interfaces;

namespace Scour.Scanners;

/// <summary>
/// Finds game directories that no longer have a Steam, Epic, or GOG install
/// record. Results are deliberately unselected because game libraries often
/// contain manually installed or modded content.
/// </summary>
public sealed class GameOrphanScanner : ScannerBase
{
    private static readonly Regex VdfPairPattern = new(
        "\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<string>? _steamLibraryRoots;
    private readonly IReadOnlyList<string>? _epicManifestRoots;
    private readonly IReadOnlyList<string>? _gogInstallRoots;
    private readonly bool _discoverSteam;
    private readonly bool _discoverEpic;
    private readonly bool _discoverGog;

    public GameOrphanScanner(
        IEnumerable<string>? steamLibraryRoots = null,
        IEnumerable<string>? epicManifestRoots = null,
        IEnumerable<string>? gogInstallRoots = null)
    {
        _discoverSteam = steamLibraryRoots == null;
        _discoverEpic = epicManifestRoots == null;
        _discoverGog = gogInstallRoots == null;
        _steamLibraryRoots = steamLibraryRoots?.ToList();
        _epicManifestRoots = epicManifestRoots?.ToList();
        _gogInstallRoots = gogInstallRoots?.ToList();
    }

    public override string Name => "Game Orphans";
    public override string Description => "Find unreferenced Steam, Epic, and GOG game directories";
    public override string IconGlyph => "\uE7FC"; // Game icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Game folder", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 440),
        new("Size", nameof(ScanResultItem.SizeFormatted), 100, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Platform", nameof(ScanResultItem.Detail), 280),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var findings = new List<OrphanFinding>();
        progress.Report(new ScanProgress("Checking game install records...", 0, 0, true));

        if (_discoverSteam || _steamLibraryRoots?.Count > 0)
            findings.AddRange(FindSteamOrphans(ct));
        if (_discoverEpic || _epicManifestRoots?.Count > 0)
            findings.AddRange(FindEpicOrphans(ct));
        if (_discoverGog || _gogInstallRoots?.Count > 0)
            findings.AddRange(FindGogOrphans(ct));

        var processed = 0;
        await Parallel.ForEachAsync(findings, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct,
        }, (finding, token) =>
        {
            token.ThrowIfCancellationRequested();
            var info = GetDirectoryInfo(finding.Path);
            if (info != null)
            {
                lock (_results)
                {
                    AddResult(new ScanResultItem
                    {
                        FullPath = finding.Path,
                        Name = Path.GetFileName(finding.Path),
                        SizeBytes = info.SizeBytes,
                        Modified = info.LastModified,
                        IsDirectory = true,
                        IsSelected = false,
                        Detail = $"{finding.Platform} · no install record found; review before removing",
                    });
                }
            }

            var count = Interlocked.Increment(ref processed);
            if (count % 10 == 0)
                progress.Report(new ScanProgress($"Measured {count}/{findings.Count} candidate folders", count, findings.Count));

            return ValueTask.CompletedTask;
        });

        var totalBytes = _results.Sum(item => item.SizeBytes);
        var formatted = new ScanResultItem { FullPath = "", SizeBytes = totalBytes }.SizeFormatted;
        progress.Report(new ScanProgress(
            _results.Count == 0
                ? "No unreferenced game directories found"
                : $"Found {_results.Count} unreferenced game directories ({formatted}); review before removing",
            _results.Count,
            _results.Count));
    }

    private IEnumerable<OrphanFinding> FindSteamOrphans(CancellationToken ct)
    {
        var libraries = _steamLibraryRoots ?? DiscoverSteamLibraries();
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(library, "steamapps");
            var common = Path.Combine(steamApps, "common");
            if (!Directory.Exists(common)) continue;

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifest in EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                var data = ReadVdfPairs(manifest);
                if (data.TryGetValue("installdir", out var installDirectory))
                    referenced.Add(installDirectory);
            }

            foreach (var directory in EnumerateDirectories(common))
            {
                if (!referenced.Contains(Path.GetFileName(directory)))
                    yield return new OrphanFinding("Steam", directory);
            }
        }
    }

    private IEnumerable<OrphanFinding> FindEpicOrphans(CancellationToken ct)
    {
        var manifestRoots = _epicManifestRoots ?? DiscoverEpicManifestRoots();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in manifestRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var manifest in EnumerateFiles(root, "*.item"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                    var installLocation = ReadString(document.RootElement, "InstallLocation");
                    if (string.IsNullOrWhiteSpace(installLocation)) continue;

                    var fullPath = Path.GetFullPath(installLocation);
                    referenced.Add(fullPath);
                    var parent = Directory.GetParent(fullPath)?.FullName;
                    if (parent != null) installRoots.Add(parent);
                }
                catch (JsonException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        foreach (var root in installRoots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var directory in EnumerateDirectories(root))
            {
                if (!referenced.Contains(Path.GetFullPath(directory)))
                    yield return new OrphanFinding("Epic", directory);
            }
        }
    }

    private IEnumerable<OrphanFinding> FindGogOrphans(CancellationToken ct)
    {
        var referenced = _discoverGog ? DiscoverGogInstallPaths() : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = _gogInstallRoots?.ToList() ?? DiscoverGogInstallRoots(referenced);

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var directory in EnumerateDirectories(root))
            {
                if (!referenced.Contains(Path.GetFullPath(directory)))
                    yield return new OrphanFinding("GOG", directory);
            }
        }
    }

    private List<string> DiscoverSteamLibraries()
    {
        var candidates = new List<string>();
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFilesX86, "Steam"));
        candidates.Add(Path.Combine(programFiles, "Steam"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam"));

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(Path.Combine(candidate, "steamapps"))) continue;
            libraries.Add(candidate);

            var libraryFile = Path.Combine(candidate, "steamapps", "libraryfolders.vdf");
            foreach (var pair in ReadVdfValues(libraryFile))
            {
                if (!pair.Key.Equals("path", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(Path.Combine(pair.Value, "steamapps")))
                    libraries.Add(pair.Value);
            }
        }

        return libraries.ToList();
    }

    private static List<string> DiscoverEpicManifestRoots()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return
        [
            Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests"),
            Path.Combine(localAppData, "EpicGamesLauncher", "Saved", "Manifests"),
        ];
    }

    private static List<string> DiscoverGogInstallRoots(ISet<string> referenced)
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(programFilesX86, "GOG Galaxy", "Games"),
            Path.Combine(programFiles, "GOG Galaxy", "Games"),
        };

        foreach (var path in referenced)
        {
            var parent = Directory.GetParent(path)?.FullName;
            if (parent != null) roots.Add(parent);
        }

        return roots.ToList();
    }

    private static HashSet<string> DiscoverGogInstallPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var games = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                    if (games == null) continue;

                    foreach (var subKeyName in games.GetSubKeyNames())
                    {
                        using var game = games.OpenSubKey(subKeyName);
                        var path = game?.GetValue("path") as string
                            ?? game?.GetValue("installDirectory") as string
                            ?? game?.GetValue("installPath") as string;
                        if (!string.IsNullOrWhiteSpace(path))
                            paths.Add(Path.GetFullPath(path));
                    }
                }
                catch (SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return paths;
    }

    private static Dictionary<string, string> ReadVdfPairs(string path)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ReadVdfValues(path))
            pairs[pair.Key] = pair.Value;
        return pairs;
    }

    private static List<KeyValuePair<string, string>> ReadVdfValues(string path)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (!File.Exists(path)) return pairs;

        try
        {
            foreach (Match match in VdfPairPattern.Matches(File.ReadAllText(path)))
            {
                var key = match.Groups["key"].Value;
                var value = match.Groups["value"].Value
                    .Replace("\\\\", "\\", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal);
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return pairs;
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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

    private static IEnumerable<string> EnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).ToList();
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

    private static DirectoryStats? GetDirectoryInfo(string path)
    {
        if (!Directory.Exists(path)) return null;

        long size = 0;
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
                    if (info.LastWriteTime > lastModified)
                        lastModified = info.LastWriteTime;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return new DirectoryStats(size, lastModified);
    }

    private sealed record OrphanFinding(string Platform, string Path);

    private sealed record DirectoryStats(long SizeBytes, DateTime LastModified);
}
