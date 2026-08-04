using System.Globalization;
using System.Text;
using System.Text.Json;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Services;
using Scour.Scanners;

namespace Scour.Cli;

public static class CliRunner
{
    public static IReadOnlyList<IScannerModule> CreateScanners() =>
    [
        new EmptyDirectoryScanner(),
        new DuplicateFileScanner(),
        new MediaDuplicateScanner(),
        new WinSxSScanner(),
        new BrowserCacheScanner(),
        new SystemSpaceScanner(),
        new GameOrphanScanner(),
        new VhdxBloatScanner(),
        new RecycleBinScanner(),
        new BigFileScanner(),
        new TempFileScanner(),
        new ZeroLengthFileScanner(),
        new OldFileScanner(),
        new BrokenSymlinkScanner(),
        new BrokenShortcutScanner(),
        new LongPathScanner(),
        new LockedFileScanner(),
        new DuplicateArchiveScanner(),
        new OrphanedAppDataScanner(),
    ];

    public static async Task<CliExecutionResult> ExecuteAsync(
        CliOptions options,
        CancellationToken ct = default,
        TextWriter? progressWriter = null)
    {
        var rootPath = Path.GetFullPath(options.RootPath);
        if (!Directory.Exists(rootPath))
            throw new CliArgumentException($"Scan path does not exist: {rootPath}");

        var modules = CreateScanners();
        var selectedModules = SelectScanners(modules, options);
        var errors = new List<string>();
        var scannerReports = new List<CliScannerReport>();
        var items = new List<CliItemReport>();
        IReadOnlySet<string>? changedPaths = null;
        var scanStrategy = "full scan";

        if (options.SinceLastRun)
        {
            var driveLetter = TryGetDriveLetter(rootPath);
            if (driveLetter == null)
            {
                progressWriter?.WriteLine("[Incremental] USN journal unavailable for this path; using a full scan.");
            }
            else
            {
                var indexProgress = new Progress<ScanProgress>(value =>
                {
                    if (progressWriter != null && (value.IsIndeterminate || value.Total > 0))
                        progressWriter.WriteLine($"[Incremental] {value.Status}");
                });
                var refresh = await new MftCacheStore().RefreshAsync(driveLetter.Value, indexProgress, ct);
                if (refresh.UsedDelta)
                {
                    changedPaths = refresh.ChangedPaths?.ToHashSet(StringComparer.OrdinalIgnoreCase) ??
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    scanStrategy = "USN journal delta";
                    progressWriter?.WriteLine($"[Incremental] {changedPaths.Count:N0} changed paths selected.");
                }
                else
                {
                    scanStrategy = refresh.Error == null ? "full scan (MFT baseline)" : "full scan (USN unavailable)";
                    progressWriter?.WriteLine($"[Incremental] {scanStrategy}; continuing safely.");
                }
            }
        }

        var config = new ScanConfig
        {
            RootPath = rootPath,
            SkipSystem = true,
            SkipHidden = false,
            Ignore0KbFiles = true,
            ChangedPaths = changedPaths,
        };

        foreach (var module in selectedModules)
        {
            ct.ThrowIfCancellationRequested();
            if (module is ScannerBase scannerBase)
                scannerBase.SetScanScope(config);
            var progress = new Progress<ScanProgress>(value =>
            {
                if (progressWriter != null && (value.IsIndeterminate || value.Total > 0))
                    progressWriter.WriteLine($"[{module.Name}] {value.Status}");
            });

            try
            {
                await module.ScanAsync(config, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{module.Name}: {ex.Message}");
                continue;
            }

            var report = new CliScannerReport
            {
                Name = module.Name,
                ResultCount = module.Results.Count,
                SelectedCount = module.Results.Count(item => item.IsSelected),
                SelectedSizeBytes = module.Results.Where(item => item.IsSelected).Sum(item => item.SizeBytes),
            };
            scannerReports.Add(report);
            items.AddRange(module.Results.Select(item => new CliItemReport(module.Name, item)));
        }

        var actions = new List<CliActionReport>();
        if (!string.IsNullOrWhiteSpace(options.QuarantinePath))
            QuarantineSelected(items, rootPath, options, actions, errors);

        if (!string.IsNullOrWhiteSpace(options.ExportCsvPath))
        {
            try
            {
                ExportCsv(options.ExportCsvPath!, items, actions);
            }
            catch (Exception ex)
            {
                errors.Add($"CSV export: {ex.Message}");
            }
        }

        var selectedCount = items.Count(item => item.Selected);
        var selectedSize = items.Where(item => item.Selected).Sum(item => item.SizeBytes);
        var exitCode = errors.Count > 0 ? 2 : selectedCount > 0 ? 1 : 0;
        return new CliExecutionResult
        {
            RootPath = rootPath,
            Preset = options.Preset,
            ScannerReports = scannerReports,
            Items = items,
            Actions = actions,
            Errors = errors,
            SinceLastRun = options.SinceLastRun,
            ScanStrategy = scanStrategy,
            SelectedCount = selectedCount,
            SelectedSizeBytes = selectedSize,
            ExitCode = exitCode,
        };
    }

    public static string ToJson(CliExecutionResult result)
        => JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

    public static void WriteHumanSummary(CliExecutionResult result, TextWriter output)
    {
        output.WriteLine($"Scour scan: {result.RootPath}");
        output.WriteLine($"Preset: {result.Preset}; scanners: {result.ScannerReports.Count}");
        output.WriteLine($"Mode: {result.ScanStrategy}");
        output.WriteLine($"Findings: {result.Items.Count}; selected: {result.SelectedCount} ({FormatSize(result.SelectedSizeBytes)})");
        foreach (var action in result.Actions)
            output.WriteLine($"{action.Status}: {action.SourcePath} -> {action.TargetPath}");
        foreach (var error in result.Errors)
            output.WriteLine($"ERROR: {error}");
        output.WriteLine($"Exit code: {result.ExitCode}");
    }

    private static IReadOnlyList<IScannerModule> SelectScanners(
        IReadOnlyList<IScannerModule> modules,
        CliOptions options)
    {
        if (options.ScannerNames.Count == 0)
            return modules.Where(module => ScanPresetCatalog.Includes(options.Preset, module.Name)).ToList();

        var selected = new List<IScannerModule>();
        var unknown = new List<string>();
        foreach (var name in options.ScannerNames)
        {
            var module = modules.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (module == null)
                unknown.Add(name);
            else if (!selected.Contains(module))
                selected.Add(module);
        }

        if (unknown.Count > 0)
            throw new CliArgumentException($"Unknown scanner(s): {string.Join(", ", unknown)}. Use --help to list names.");
        return selected;
    }

    private static char? TryGetDriveLetter(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return root is { Length: >= 2 } && char.IsLetter(root[0]) && root[1] == ':'
                ? char.ToUpperInvariant(root[0])
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void QuarantineSelected(
        IReadOnlyList<CliItemReport> items,
        string rootPath,
        CliOptions options,
        List<CliActionReport> actions,
        List<string> errors)
    {
        var quarantinePath = Path.GetFullPath(options.QuarantinePath!);
        if (IsSameOrChildPath(quarantinePath, rootPath))
        {
            errors.Add("Quarantine path must be outside the scan path.");
            return;
        }

        if (!options.DryRun)
            Directory.CreateDirectory(quarantinePath);

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(item => item.Selected))
        {
            if (!handled.Add(item.FullPath))
                continue;

            var target = BuildQuarantineTarget(rootPath, quarantinePath, item.FullPath);
            if (options.DryRun)
            {
                actions.Add(new CliActionReport("would-quarantine", item.FullPath, target));
                continue;
            }

            try
            {
                if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
                    throw new FileNotFoundException("Finding no longer exists.", item.FullPath);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                target = EnsureUniqueTarget(target, item.IsDirectory);
                if (item.IsDirectory)
                    Directory.Move(item.FullPath, target);
                else
                    File.Move(item.FullPath, target);
                actions.Add(new CliActionReport("quarantined", item.FullPath, target));
            }
            catch (Exception ex)
            {
                actions.Add(new CliActionReport("quarantine-failed", item.FullPath, target, ex.Message));
                errors.Add($"Quarantine {item.FullPath}: {ex.Message}");
            }
        }
    }

    private static string BuildQuarantineTarget(string rootPath, string quarantinePath, string sourcePath)
    {
        var relative = Path.GetRelativePath(rootPath, sourcePath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            relative = Path.GetFileName(sourcePath);
        return Path.Combine(quarantinePath, relative);
    }

    private static string EnsureUniqueTarget(string target, bool isDirectory)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
            return target;

        var directory = Path.GetDirectoryName(target)!;
        var name = Path.GetFileNameWithoutExtension(target);
        var extension = isDirectory ? "" : Path.GetExtension(target);
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static bool IsSameOrChildPath(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExportCsv(string path, IReadOnlyList<CliItemReport> items, IReadOnlyList<CliActionReport> actions)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var actionByPath = actions
            .GroupBy(action => action.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Status, StringComparer.OrdinalIgnoreCase);

        using var writer = new StreamWriter(fullPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("Selected,Scanner,Name,Path,SizeBytes,Modified,Detail,Group,IsDirectory,Action");
        foreach (var item in items)
        {
            actionByPath.TryGetValue(item.FullPath, out var action);
            writer.WriteLine(string.Join(",",
                item.Selected,
                Csv(item.Scanner),
                Csv(item.Name),
                Csv(item.FullPath),
                item.SizeBytes.ToString(CultureInfo.InvariantCulture),
                Csv(item.Modified),
                Csv(item.Detail),
                Csv(item.Group),
                item.IsDirectory,
                Csv(action ?? "")));
        }
    }

    private static string Csv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string FormatSize(long bytes)
        => new ScanResultItem { FullPath = "", SizeBytes = bytes }.SizeFormatted;
}

public sealed class CliExecutionResult
{
    public string RootPath { get; init; } = "";
    public ScanPreset Preset { get; init; }
    public IReadOnlyList<CliScannerReport> ScannerReports { get; init; } = [];
    public IReadOnlyList<CliItemReport> Items { get; init; } = [];
    public IReadOnlyList<CliActionReport> Actions { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool SinceLastRun { get; init; }
    public string ScanStrategy { get; init; } = "full scan";
    public int SelectedCount { get; init; }
    public long SelectedSizeBytes { get; init; }
    public int ExitCode { get; init; }
}

public sealed class CliScannerReport
{
    public string Name { get; init; } = "";
    public int ResultCount { get; init; }
    public int SelectedCount { get; init; }
    public long SelectedSizeBytes { get; init; }
}

public sealed class CliItemReport
{
    public CliItemReport(string scanner, ScanResultItem item)
    {
        Scanner = scanner;
        Name = item.Name;
        FullPath = item.FullPath;
        SizeBytes = item.SizeBytes;
        Modified = item.Modified == default ? "" : item.Modified.ToString("O", CultureInfo.InvariantCulture);
        Detail = item.Detail;
        Group = item.Group;
        IsDirectory = item.IsDirectory;
        Selected = item.IsSelected;
    }

    public string Scanner { get; }
    public string Name { get; }
    public string FullPath { get; }
    public long SizeBytes { get; }
    public string Modified { get; }
    public string Detail { get; }
    public string Group { get; }
    public bool IsDirectory { get; }
    public bool Selected { get; }
}

public sealed record CliActionReport(string Status, string SourcePath, string TargetPath, string Error = "");
