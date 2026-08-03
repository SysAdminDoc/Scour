using System.Diagnostics;
using System.Text.Json;
using Scour.Core;
using Scour.Core.Interfaces;

namespace Scour.Scanners;

/// <summary>
/// Audits dynamic WSL and Docker VHDX files and exposes an explicit compact
/// action. Compacting is never treated as ordinary file deletion.
/// </summary>
public sealed class VhdxBloatScanner : ScannerBase
{
    private readonly IReadOnlyList<string>? _candidateRoots;
    private readonly bool _queryVhdMetadata;
    private readonly long _minimumSizeBytes;
    private readonly Dictionary<string, VhdxAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public VhdxBloatScanner(
        IEnumerable<string>? candidateRoots = null,
        long minimumSizeBytes = 1024L * 1024 * 1024)
    {
        _candidateRoots = candidateRoots?.ToList();
        _queryVhdMetadata = candidateRoots == null;
        _minimumSizeBytes = minimumSizeBytes;
    }

    public override string Name => "VHDX Bloat";
    public override string Description => "Inspect Docker and WSL virtual disks before compacting them";
    public override string IconGlyph => "\uE7F8"; // Virtual machine icon
    public override string DeleteActionLabel => "\uE7F8  Compact Selected";

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Virtual disk", nameof(ScanResultItem.Name), 240),
        new("Path", nameof(ScanResultItem.FullPath), 450),
        new("Host size", nameof(ScanResultItem.SizeFormatted), 110, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Details", nameof(ScanResultItem.Detail), 360),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        _actions.Clear();
        var paths = DiscoverVhdxFiles();
        progress.Report(new ScanProgress($"Found {paths.Count} VHDX files", 0, paths.Count));

        var findings = new List<VhdxFinding>();
        var processed = 0;
        await Parallel.ForEachAsync(paths, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = ct,
        }, async (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            var info = await ReadVhdxInfoAsync(path, token);
            if (info.FileSize >= _minimumSizeBytes)
            {
                lock (findings)
                    findings.Add(new VhdxFinding(path, info));
            }

            var count = Interlocked.Increment(ref processed);
            if (count % 5 == 0)
                progress.Report(new ScanProgress($"Measured {count}/{paths.Count} VHDX files", count, paths.Count));
        });

        foreach (var finding in findings.OrderByDescending(item => item.Info.FileSize))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(finding.Path);
            var kind = DetectKind(finding.Path);
            var detail = BuildDetail(kind, finding.Info);
            var item = new ScanResultItem
            {
                FullPath = finding.Path,
                Name = Path.GetFileName(finding.Path),
                SizeBytes = finding.Info.FileSize,
                Modified = info.LastWriteTime,
                Detail = detail,
                IsSelected = false,
            };
            AddResult(item);
            _actions[item.FullPath] = VhdxAction.Compact;
        }

        var totalBytes = _results.Sum(item => item.SizeBytes);
        var formatted = new ScanResultItem { FullPath = "", SizeBytes = totalBytes }.SizeFormatted;
        progress.Report(new ScanProgress(
            _results.Count == 0
                ? "No VHDX files exceeded the reporting threshold"
                : $"Found {_results.Count} compactable VHDX files ({formatted}); close Docker/WSL before compacting",
            _results.Count,
            _results.Count));
    }

    public override async Task DeleteSelectedAsync(
        IEnumerable<ScanResultItem> items,
        DeleteMode mode,
        IProgress<ScanProgress> progress,
        CancellationToken ct)
    {
        var selected = items.ToList();
        var failures = new List<string>();
        var completed = 0;

        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!_actions.TryGetValue(item.FullPath, out var action) || action != VhdxAction.Compact)
                    throw new InvalidOperationException("This VHDX result has no supported action.");

                progress.Report(new ScanProgress($"Compacting: {item.Name}", completed, selected.Count));
                if (mode != DeleteMode.Simulate)
                    await CompactVhdxAsync(item.FullPath, ct);

                completed++;
                progress.Report(new ScanProgress($"Compacted: {item.Name}; restart may be required", completed, selected.Count));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{item.Name}: {ex.Message}");
                progress.Report(new ScanProgress($"Error: {item.Name} - {ex.Message}", completed, selected.Count));
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join("; ", failures));
    }

    private List<string> DiscoverVhdxFiles()
    {
        var roots = _candidateRoots ?? DiscoverDefaultRoots();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (File.Exists(root) && root.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(Path.GetFullPath(root));
                continue;
            }

            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.vhdx", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }))
                {
                    files.Add(Path.GetFullPath(path));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return files.ToList();
    }

    private List<string> DiscoverDefaultRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return
        [
            Path.Combine(localAppData, "Docker", "wsl"),
            Path.Combine(localAppData, "Packages"),
            Path.Combine(userProfile, ".docker", "desktop-data"),
            Path.Combine(programData, "DockerDesktop"),
        ];
    }

    private async Task<VhdxInfo> ReadVhdxInfoAsync(string path, CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        if (!_queryVhdMetadata)
            return new VhdxInfo(fileInfo.Length, 0, 0, "Unknown");

        try
        {
            var escaped = path.Replace("'", "''", StringComparison.Ordinal);
            var script = "$ErrorActionPreference='Stop'; " +
                $"Get-VHD -Path '{escaped}' | Select-Object FileSize,Size,MinimumSize,VhdType | ConvertTo-Json -Compress";
            var output = await RunPowerShellAsync(script, ct);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return new VhdxInfo(
                ReadInt64(root, "FileSize") ?? fileInfo.Length,
                ReadInt64(root, "Size") ?? 0,
                ReadInt64(root, "MinimumSize") ?? 0,
                root.TryGetProperty("VhdType", out var vhdType) ? vhdType.ToString() : "Unknown");
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return new VhdxInfo(fileInfo.Length, 0, 0, "Metadata unavailable");
        }
    }

    private static long? ReadInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)) return value;
        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value)) return value;
        return null;
    }

    private static async Task CompactVhdxAsync(string path, CancellationToken ct)
    {
        var escaped = path.Replace("'", "''", StringComparison.Ordinal);
        var script = "$ErrorActionPreference='Stop'; " +
            $"Optimize-VHD -Path '{escaped}' -Mode Full -ErrorAction Stop";
        _ = await RunPowerShellAsync(script, ct);
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);

        if (!process.Start())
            throw new InvalidOperationException("Could not start Windows PowerShell");

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"PowerShell exited with code {process.ExitCode}"
                : error.Trim());
        return output;
    }

    private static string DetectKind(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.Contains("\\Docker\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\.docker\\", StringComparison.OrdinalIgnoreCase))
            return "Docker";
        if (normalized.Contains("\\Packages\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\wsl\\", StringComparison.OrdinalIgnoreCase))
            return "WSL";
        return "VHDX";
    }

    private static string BuildDetail(string kind, VhdxInfo info)
    {
        var virtualSize = info.VirtualSize > 0
            ? $"virtual {FormatBytes(info.VirtualSize)}"
            : "virtual size unavailable";
        var minimumSize = info.MinimumSize > 0
            ? $", minimum {FormatBytes(info.MinimumSize)}"
            : "";
        return $"{kind} · {info.VhdType}; {virtualSize}{minimumSize}; close Docker/WSL before compacting";
    }

    private static string FormatBytes(long bytes)
        => new ScanResultItem { FullPath = "", SizeBytes = bytes }.SizeFormatted;

    private sealed record VhdxFinding(string Path, VhdxInfo Info);

    private sealed record VhdxInfo(long FileSize, long VirtualSize, long MinimumSize, string VhdType);

    private enum VhdxAction
    {
        Compact,
    }
}
