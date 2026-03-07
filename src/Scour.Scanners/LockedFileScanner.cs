using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

/// <summary>
/// Finds files that cannot be opened for reading (locked by another process,
/// permission denied, or otherwise inaccessible).
/// </summary>
public sealed class LockedFileScanner : ScannerBase
{
    public override string Name => "Locked Files";
    public override string Description => "Find files that are locked or inaccessible";
    public override string IconGlyph => "\uE72E"; // Lock icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 220),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Reason", nameof(ScanResultItem.Detail), 250),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 130),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var scanned = 0;

        await Task.Run(() => ScanDir(config.RootPath, 0, config, ref scanned, progress, ct), ct);

        progress.Report(new ScanProgress($"Found {_results.Count} locked/inaccessible files", _results.Count, _results.Count));
    }

    private void ScanDir(string path, int depth, ScanConfig config,
        ref int scanned, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (config.MaxDepth >= 0 && depth > config.MaxDepth) return;

        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(path))
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    if (config.SkipHidden && entry.IsHidden) continue;
                    if (config.SkipSystem && entry.IsSystem) continue;
                    if (config.ExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    ScanDir(entry.FullPath, depth + 1, config, ref scanned, progress, ct);
                    continue;
                }

                if (entry.IsDirectory) continue;

                scanned++;
                if (scanned % 2000 == 0)
                    progress.Report(new ScanProgress($"Testing access: {path}", scanned, 0, true));

                var reason = TestAccess(entry.FullPath);
                if (reason != null)
                {
                    _results.Add(new ScanResultItem
                    {
                        FullPath = entry.FullPath,
                        Name = entry.Name,
                        SizeBytes = entry.SizeBytes,
                        Modified = entry.LastWriteTime,
                        Detail = reason,
                        IsSelected = false, // Don't select locked files by default
                    });
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static string? TestAccess(string filePath)
    {
        try
        {
            using var fs = new FileStream(
                Win32FileSystem.EnsureLongPath(filePath),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return null; // File is accessible
        }
        catch (UnauthorizedAccessException)
        {
            return "Access denied";
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        {
            return "Locked by another process";
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070021)) // ERROR_LOCK_VIOLATION
        {
            return "Lock violation";
        }
        catch (IOException ex)
        {
            return $"I/O error (0x{ex.HResult:X8})";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }
}
