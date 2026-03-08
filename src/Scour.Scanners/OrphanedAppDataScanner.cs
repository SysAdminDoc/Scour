using Microsoft.Win32;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

/// <summary>
/// Finds leftover application data directories in AppData, ProgramData,
/// and Program Files that don't belong to any currently installed program.
/// </summary>
public sealed class OrphanedAppDataScanner : ScannerBase
{
    public override string Name => "Orphaned App Data";
    public override string Description => "Find leftover data from uninstalled programs";
    public override string IconGlyph => "\uE74D"; // Delete icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Folder", nameof(ScanResultItem.Name), 220),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Location", nameof(ScanResultItem.Group), 120),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 130),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();

        await Task.Run(() =>
        {
            // Phase 1: Build set of known installed application names
            progress.Report(new ScanProgress("Building installed app inventory...", 0, 0, true));
            var installedApps = GetInstalledAppNames();

            // Phase 2: Scan app data directories
            var locations = GetScanLocations();
            var done = 0;

            foreach (var (dirPath, label) in locations)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(dirPath)) continue;

                progress.Report(new ScanProgress($"Scanning {label}...", done, locations.Count, true));

                try
                {
                    foreach (var entry in Win32FileSystem.EnumerateEntries(dirPath))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!entry.IsDirectory || entry.IsReparsePoint) continue;
                        if (entry.IsSystem || entry.IsHidden) continue;

                        // Skip known system/framework directories
                        if (IsKnownSystemDir(entry.Name)) continue;

                        // Check if this directory matches any installed application
                        if (!IsOrphaned(entry.Name, installedApps))
                            continue;

                        // Calculate directory size
                        var size = CalculateDirSize(entry.FullPath, ct);

                        // Skip tiny directories (< 1KB) - likely just empty remnants
                        if (size < 1024) continue;

                        AddResult(new ScanResultItem
                        {
                            FullPath = entry.FullPath,
                            Name = entry.Name,
                            SizeBytes = size,
                            IsDirectory = true,
                            Modified = entry.LastWriteTime,
                            Group = label,
                            IsSelected = false, // Don't auto-select - user should review
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }

                done++;
            }
        }, ct);

        // Sort by size descending
        _results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

        progress.Report(new ScanProgress($"Found {_results.Count} orphaned app data folders", _results.Count, _results.Count));
    }

    private static List<(string Path, string Label)> GetScanLocations()
    {
        var locations = new List<(string, string)>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        if (!string.IsNullOrEmpty(appData))
            locations.Add((appData, "AppData/Roaming"));
        if (!string.IsNullOrEmpty(localAppData))
            locations.Add((localAppData, "AppData/Local"));
        if (!string.IsNullOrEmpty(programData))
            locations.Add((programData, "ProgramData"));

        // Program Files leftovers
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf))
            locations.Add((pf, "Program Files"));
        if (!string.IsNullOrEmpty(pf86) && !pf86.Equals(pf, StringComparison.OrdinalIgnoreCase))
            locations.Add((pf86, "Program Files (x86)"));

        return locations;
    }

    private static HashSet<string> GetInstalledAppNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Read from registry uninstall keys
        string[] registryPaths =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (var regPath in registryPaths)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(regPath);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var displayName = subKey.GetValue("DisplayName") as string;
                        var installLocation = subKey.GetValue("InstallLocation") as string;
                        var publisher = subKey.GetValue("Publisher") as string;

                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            names.Add(displayName);
                            // Also add simplified versions
                            names.Add(displayName.Replace(" ", ""));
                            // Extract first word (company/app name)
                            var firstWord = displayName.Split(' ', '.', '-')[0];
                            if (firstWord.Length > 3) names.Add(firstWord);
                        }

                        if (!string.IsNullOrWhiteSpace(installLocation))
                        {
                            var dirName = Path.GetFileName(installLocation.TrimEnd('\\', '/'));
                            if (!string.IsNullOrEmpty(dirName)) names.Add(dirName);
                        }

                        if (!string.IsNullOrWhiteSpace(publisher))
                        {
                            names.Add(publisher);
                            names.Add(publisher.Replace(" ", ""));
                        }

                        // Also add the registry key name itself (often matches the folder name)
                        names.Add(subKeyName);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Also check current user
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPaths[0]);
            if (key != null)
            {
                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        var displayName = subKey?.GetValue("DisplayName") as string;
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            names.Add(displayName);
                            names.Add(displayName.Replace(" ", ""));
                        }
                        names.Add(subKeyName);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // Add running processes as "still in use" indicators
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var procName = proc.ProcessName;
                    if (procName.Length > 3) names.Add(procName);
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }

        return names;
    }

    private static bool IsOrphaned(string dirName, HashSet<string> installedApps)
    {
        // Direct match
        if (installedApps.Contains(dirName)) return false;

        // Check if any installed app name contains the directory name (or vice versa)
        foreach (var app in installedApps)
        {
            if (app.Length < 4 || dirName.Length < 4) continue;

            if (dirName.Contains(app, StringComparison.OrdinalIgnoreCase) ||
                app.Contains(dirName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static readonly HashSet<string> KnownSystemDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows / system directories
        "Microsoft", "Windows", "Packages", "Microsoft.Windows",
        "ConnectedDevicesPlatform", "CrashDumps", "D3DSCache",
        "Diagnostics", "GameDVR", "Publishers", "Temp", "History",
        "Temporary Internet Files", "INetCache", "INetCookies",
        "Programs", "Microsoft_Corporation",

        // Common framework / runtime directories
        ".NET", ".dotnet", "NuGet", "npm", "pip", "cargo", "rustup",
        "Python", "Java", "Go", "Ruby",

        // Package managers
        "Package Cache", "PackageManagement", "WindowsApps",
        "WinGet", "Chocolatey", "scoop",

        // System utilities
        "Windows Defender", "Windows Security", "Windows Update",
        "WindowsPowerShell", "PowerShell",
    };

    private static bool IsKnownSystemDir(string name) => KnownSystemDirs.Contains(name);

    private static long CalculateDirSize(string path, CancellationToken ct)
    {
        var size = 0L;
        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(path))
            {
                if (ct.IsCancellationRequested) return size;

                if (entry.IsDirectory && !entry.IsReparsePoint)
                    size += CalculateDirSize(entry.FullPath, ct);
                else if (!entry.IsDirectory)
                    size += entry.SizeBytes;
            }
        }
        catch { }
        return size;
    }
}
