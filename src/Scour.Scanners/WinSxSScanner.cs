using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

/// <summary>
/// Audits Windows servicing leftovers without offering a delete action.
/// The component store contains live assemblies, so this scanner deliberately
/// reports candidates as informational results only.
/// </summary>
public sealed class WinSxSScanner : ScannerBase
{
    private static readonly HashSet<string> ServicingDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Backup",
        "InstallTemp",
        "Pending",
        "Temp",
    };

    private static readonly HashSet<string> ServicingFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending.xml",
        "poqexec.log",
        "reboot.xml",
    };

    private static readonly Regex SizePattern = new(
        @"(?<value>[0-9]+(?:[\.,][0-9]+)?)\s*(?<unit>KB|MB|GB|TB)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _winSxSPath;
    private readonly bool _runDismAnalysis;

    public WinSxSScanner(string? winSxSPath = null)
    {
        _runDismAnalysis = winSxSPath == null;
        _winSxSPath = winSxSPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "WinSxS");
    }

    public override string Name => "WinSxS Analysis";
    public override string Description => "Audit stale servicing leftovers without touching live components";
    public override string IconGlyph => "\uE950"; // Shield icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 250),
        new("Path", nameof(ScanResultItem.FullPath), 450),
        new("Size", nameof(ScanResultItem.SizeFormatted), 100, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Finding", nameof(ScanResultItem.Detail), 360),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        progress.Report(new ScanProgress("Inspecting the Windows component store...", 0, 0, true));

        if (!Directory.Exists(_winSxSPath))
        {
            progress.Report(new ScanProgress("WinSxS directory was not found", 0, 0));
            return;
        }

        var cutoff = DateTime.Now.AddDays(-7);
        await Task.Run(() => FindKnownLeftovers(cutoff, progress, ct), ct);

        var analysis = _runDismAnalysis
            ? await AnalyzeComponentStoreAsync(ct)
            : null;
        if (analysis != null && (analysis.ReclaimablePackages > 0 || analysis.ReclaimableBytes > 0))
        {
            AddResult(new ScanResultItem
            {
                FullPath = _winSxSPath,
                Name = "DISM reclaimable component data",
                SizeBytes = analysis.ReclaimableBytes,
                IsDirectory = true,
                IsSelected = false,
                Detail = $"{analysis.ReclaimablePackages} package(s) reported by DISM; cleanup recommended: {analysis.CleanupRecommended}",
            });
        }

        progress.Report(new ScanProgress(
            _results.Count == 0
                ? "No stale servicing leftovers reported"
                : $"Found {_results.Count} audit finding(s); WinSxS results are protected",
            _results.Count,
            _results.Count));
    }

    public override Task DeleteSelectedAsync(
        IEnumerable<ScanResultItem> items,
        DeleteMode mode,
        IProgress<ScanProgress> progress,
        CancellationToken ct)
    {
        var count = items.Count();
        progress.Report(new ScanProgress(
            count == 0
                ? "No WinSxS items selected; component-store results are audit-only"
                : "WinSxS analysis is audit-only; no component-store files were changed",
            count,
            count));
        return Task.CompletedTask;
    }

    private void FindKnownLeftovers(DateTime cutoff, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var scanned = 0;
        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(_winSxSPath))
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                if (entry.IsDirectory &&
                    ServicingDirectories.Contains(entry.Name) &&
                    entry.LastWriteTime < cutoff)
                {
                    AddResult(new ScanResultItem
                    {
                        FullPath = entry.FullPath,
                        Name = entry.Name,
                        SizeBytes = GetDirectorySize(entry.FullPath),
                        Modified = entry.LastWriteTime,
                        IsDirectory = true,
                        IsSelected = false,
                        Detail = "Stale servicing workspace older than 7 days; audit only",
                    });
                }
                else if (!entry.IsDirectory &&
                    ServicingFiles.Contains(entry.Name) &&
                    entry.LastWriteTime < cutoff)
                {
                    AddResult(new ScanResultItem
                    {
                        FullPath = entry.FullPath,
                        Name = entry.Name,
                        SizeBytes = entry.SizeBytes,
                        Modified = entry.LastWriteTime,
                        IsSelected = false,
                        Detail = "Stale servicing marker older than 7 days; audit only",
                    });
                }

                if (scanned % 10 == 0)
                    progress.Report(new ScanProgress($"Checked {scanned} component-store entries...", scanned, 0, true));
            }
        }
        catch (UnauthorizedAccessException)
        {
            progress.Report(new ScanProgress("WinSxS access is restricted; showing accessible findings", scanned, 0, true));
        }
        catch (IOException)
        {
            progress.Report(new ScanProgress("WinSxS changed during analysis; showing accessible findings", scanned, 0, true));
        }
    }

    private static long GetDirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
            }))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return total;
    }

    private static async Task<ComponentStoreAnalysis?> AnalyzeComponentStoreAsync(CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dism.exe",
                    Arguments = "/Online /Cleanup-Image /AnalyzeComponentStore /English",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.Unicode,
                },
            };

            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = await outputTask;
            _ = await errorTask;

            if (process.ExitCode != 0) return null;
            return ParseAnalysis(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return null;
        }
    }

    internal static ComponentStoreAnalysis ParseAnalysis(string output)
    {
        var reclaimablePackages = 0;
        var cleanupRecommended = "Unknown";
        var reclaimableBytes = 0L;

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Number of Reclaimable Packages", StringComparison.OrdinalIgnoreCase))
            {
                var separator = trimmed.IndexOf(':');
                if (separator >= 0)
                    int.TryParse(trimmed[(separator + 1)..].Trim(), out reclaimablePackages);
            }
            else if (trimmed.StartsWith("Component Store Cleanup Recommended", StringComparison.OrdinalIgnoreCase))
            {
                var separator = trimmed.IndexOf(':');
                if (separator >= 0)
                    cleanupRecommended = trimmed[(separator + 1)..].Trim();
            }
            else if (trimmed.StartsWith("Backups and Disabled Features", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Cache and Temporary Data", StringComparison.OrdinalIgnoreCase))
            {
                var match = SizePattern.Match(trimmed);
                if (match.Success)
                    reclaimableBytes += ParseSize(match.Groups["value"].Value, match.Groups["unit"].Value);
            }
        }

        return new ComponentStoreAnalysis(reclaimablePackages, reclaimableBytes, cleanupRecommended);
    }

    private static long ParseSize(string value, string unit)
    {
        if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return 0;

        var multiplier = unit.ToUpperInvariant() switch
        {
            "TB" => 1024d * 1024 * 1024 * 1024,
            "GB" => 1024d * 1024 * 1024,
            "MB" => 1024d * 1024,
            _ => 1024d,
        };

        return (long)Math.Clamp(number * multiplier, 0, long.MaxValue);
    }
}

internal sealed record ComponentStoreAnalysis(
    int ReclaimablePackages,
    long ReclaimableBytes,
    string CleanupRecommended);
