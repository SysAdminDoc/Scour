using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Services;

namespace Scour.Scanners;

/// <summary>
/// Surfaces system-managed storage and provides explicit, protected actions
/// for hibernation, pagefile sizing, and automatic crash-dump policy.
/// </summary>
public sealed class SystemSpaceScanner : ScannerBase
{
    private readonly string _systemRoot;
    private readonly string _windowsRoot;
    private readonly bool _isDefaultSystem;
    private readonly Dictionary<string, SystemAction> _actions = new(StringComparer.OrdinalIgnoreCase);

    public SystemSpaceScanner(string? systemRoot = null, string? windowsRoot = null)
    {
        _isDefaultSystem = systemRoot == null && windowsRoot == null;
        _systemRoot = systemRoot ?? (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
        _windowsRoot = windowsRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    public override string Name => "System Space";
    public override string Description => "Manage hibernation, pagefile, and crash-dump storage";
    public override string IconGlyph => "\uE7F4"; // Settings icon
    public override string DeleteActionLabel => "\uE8FB  Apply Selected Actions";

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Item", nameof(ScanResultItem.Name), 220),
        new("Path", nameof(ScanResultItem.FullPath), 430),
        new("Size", nameof(ScanResultItem.SizeFormatted), 100, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 140),
        new("Action", nameof(ScanResultItem.Detail), 380),
    ];

    public override Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        _actions.Clear();
        progress.Report(new ScanProgress("Inspecting system-managed storage...", 0, 0, true));

        AddFileFinding(
            "Hibernation file",
            Path.Combine(_systemRoot, "hiberfil.sys"),
            "Select to disable hibernation with powercfg; Windows removes the file",
            SystemAction.DisableHibernation,
            isSelected: false);

        AddFileFinding(
            "Pagefile",
            Path.Combine(_systemRoot, "pagefile.sys"),
            "Select to shrink the configured pagefile to 75% (minimum 1 GB); restart required",
            SystemAction.ShrinkPagefile,
            isSelected: false);

        AddFileFinding(
            "Swapfile",
            Path.Combine(_systemRoot, "swapfile.sys"),
            "System-managed swap storage; it is informational and cannot be acted on here",
            SystemAction.Informational,
            isSelected: false);

        AddFileFinding(
            "Full memory dump",
            Path.Combine(_windowsRoot, "MEMORY.DMP"),
            "Crash dump; selected files are sent to the Recycle Bin by default",
            SystemAction.DeleteFile,
            isSelected: true);

        var minidumpPath = Path.Combine(_windowsRoot, "Minidump");
        foreach (var dumpPath in EnumerateFiles(minidumpPath, "*.dmp"))
        {
            ct.ThrowIfCancellationRequested();
            AddFileFinding(
                "Mini dump",
                dumpPath,
                "Crash dump; selected files are sent to the Recycle Bin by default",
                SystemAction.DeleteFile,
                isSelected: true);
        }

        if (_isDefaultSystem)
        {
            var crashDumpEnabled = ReadCrashDumpEnabled();
            var registryPath = @"HKLM\SYSTEM\CurrentControlSet\Control\CrashControl";
            var item = new ScanResultItem
            {
                FullPath = registryPath,
                Name = "Automatic crash dumps",
                Detail = crashDumpEnabled
                    ? "Enabled; select to disable new automatic crash dumps"
                    : "Disabled; no action needed",
                IsSelected = false,
            };
            AddResult(item);
            _actions[item.FullPath] = SystemAction.DisableCrashDumps;
        }

        var totalBytes = _results.Sum(item => item.SizeBytes);
        var formatted = new ScanResultItem { FullPath = "", SizeBytes = totalBytes }.SizeFormatted;
        progress.Report(new ScanProgress(
            $"Found {_results.Count} system-storage item(s) ({formatted})",
            _results.Count,
            _results.Count));
        return Task.CompletedTask;
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
                var action = _actions.GetValueOrDefault(item.FullPath, SystemAction.Informational);
                progress.Report(new ScanProgress($"Applying: {item.Name}", completed, selected.Count));

                switch (action)
                {
                    case SystemAction.DeleteFile:
                        DeletionService.Delete(item.FullPath, false, mode);
                        break;
                    case SystemAction.DisableHibernation:
                        await RunProcessAsync("powercfg.exe", "/hibernate off", mode, ct);
                        break;
                    case SystemAction.ShrinkPagefile:
                        await ShrinkPagefileAsync(item.FullPath, item.SizeBytes, mode, ct);
                        break;
                    case SystemAction.DisableCrashDumps:
                        await DisableCrashDumpsAsync(mode, ct);
                        break;
                    case SystemAction.Informational:
                        throw new InvalidOperationException("This system-storage item has no action.");
                }

                completed++;
                progress.Report(new ScanProgress($"Applied: {item.Name}", completed, selected.Count));
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

    private void AddFileFinding(
        string name,
        string path,
        string detail,
        SystemAction action,
        bool isSelected)
    {
        if (!File.Exists(path)) return;

        var info = new FileInfo(path);
        var item = new ScanResultItem
        {
            FullPath = path,
            Name = name,
            SizeBytes = info.Length,
            Modified = info.LastWriteTime,
            Detail = detail,
            IsSelected = isSelected,
        };
        AddResult(item);
        _actions[item.FullPath] = action;
    }

    private static IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToList();
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

    private static bool ReadCrashDumpEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\CrashControl",
                writable: false);
            var value = key?.GetValue("CrashDumpEnabled");
            return value is int intValue && intValue > 0;
        }
        catch (SecurityException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async Task ShrinkPagefileAsync(string path, long fileSizeBytes, DeleteMode mode, CancellationToken ct)
    {
        if (mode == DeleteMode.Simulate) return;

        var currentMegabytes = Math.Max(1024L, fileSizeBytes / (1024 * 1024));
        var targetMegabytes = Math.Max(1024L, (long)Math.Floor(currentMegabytes * 0.75));
        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var script = $"$page = Get-CimInstance Win32_PageFileSetting | Where-Object Name -eq '{escapedPath}'; " +
            "if ($null -eq $page) { throw 'Configured pagefile was not found' }; " +
            $"$page | Set-CimInstance -Property @{{InitialSize={targetMegabytes}; MaximumSize={targetMegabytes}}}";
        await RunPowerShellAsync(script, ct);
    }

    private static async Task DisableCrashDumpsAsync(DeleteMode mode, CancellationToken ct)
    {
        if (mode == DeleteMode.Simulate) return;

        var script = "Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\CrashControl' -Name CrashDumpEnabled -Type DWord -Value 0";
        await RunPowerShellAsync(script, ct);
    }

    private static async Task RunProcessAsync(string fileName, string arguments, DeleteMode mode, CancellationToken ct)
    {
        if (mode == DeleteMode.Simulate) return;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {fileName}");

        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}");
    }

    private static async Task RunPowerShellAsync(string script, CancellationToken ct)
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

        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"PowerShell exited with code {process.ExitCode}"
                : error.Trim());
    }

    private enum SystemAction
    {
        Informational,
        DeleteFile,
        DisableHibernation,
        ShrinkPagefile,
        DisableCrashDumps,
    }
}
