using System.Collections.Concurrent;
using System.IO.Compression;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

/// <summary>
/// Finds archive files (.zip, .7z, .rar, etc.) that sit alongside directories
/// with matching names, suggesting the archive has been extracted but not deleted.
/// Also detects .zip files whose contents are already present on disk.
/// </summary>
public sealed class DuplicateArchiveScanner : ScannerBase
{
    public override string Name => "Duplicate Archives";
    public override string Description => "Find archives that appear already extracted";
    public override string IconGlyph => "\uE8B7"; // Package icon

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz",
        ".tar.gz", ".tar.bz2", ".tar.xz", ".cab", ".iso"
    };

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Archive", nameof(ScanResultItem.Name), 220),
        new("Path", nameof(ScanResultItem.FullPath), 400),
        new("Size", nameof(ScanResultItem.SizeFormatted), 80, true),
        new("Match", nameof(ScanResultItem.Detail), 250),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 130),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();

        var archives = new ConcurrentBag<(FileEntry File, string DirPath)>();
        var scanned = 0;

        // Phase 1: Find all archives that have a matching directory sibling
        await Task.Run(() => ScanDir(config.RootPath, 0, config, archives, ref scanned, progress, ct), ct);

        progress.Report(new ScanProgress($"Found {archives.Count} candidate archives, verifying...", archives.Count, 0, true));

        // Phase 2: For .zip files, optionally verify contents exist on disk
        var done = 0;
        var total = archives.Count;
        foreach (var (archive, matchDir) in archives)
        {
            ct.ThrowIfCancellationRequested();
            done++;

            var detail = $"Matches folder: {Path.GetFileName(matchDir)}";
            var confidence = "Name match";

            // For .zip files, try to verify contents overlap
            if (archive.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var overlap = CheckZipOverlap(archive.FullPath, matchDir);
                if (overlap >= 0)
                {
                    confidence = overlap >= 80 ? $"{overlap}% content match" : $"{overlap}% overlap";
                    detail = $"{confidence} with {Path.GetFileName(matchDir)}/";
                }
            }

            progress.Report(new ScanProgress($"Checking: {archive.Name}", done, total));

            _results.Add(new ScanResultItem
            {
                FullPath = archive.FullPath,
                Name = archive.Name,
                SizeBytes = archive.SizeBytes,
                Modified = archive.LastWriteTime,
                Detail = detail,
                Group = confidence.Contains("content") || confidence.Contains("100%") ? "High confidence" : "Name match",
            });
        }

        progress.Report(new ScanProgress($"Found {_results.Count} duplicate archives", _results.Count, _results.Count));
    }

    private void ScanDir(string path, int depth, ScanConfig config,
        ConcurrentBag<(FileEntry, string)> archives,
        ref int scanned, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (config.MaxDepth >= 0 && depth > config.MaxDepth) return;

        var files = new List<FileEntry>();
        var dirs = new List<FileEntry>();
        var dirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(path))
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    if (config.SkipHidden && entry.IsHidden) continue;
                    if (config.SkipSystem && entry.IsSystem) continue;
                    if (config.ExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    dirs.Add(entry);
                    dirNames.Add(entry.Name);
                }
                else if (!entry.IsDirectory)
                {
                    files.Add(entry);
                }
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        scanned++;
        if (scanned % 500 == 0)
            progress.Report(new ScanProgress($"Scanning: {path}", scanned, 0, true));

        // Check each archive file for a matching directory
        foreach (var file in files)
        {
            if (!IsArchive(file.Name)) continue;

            // Strip archive extension to get base name
            var baseName = GetArchiveBaseName(file.Name);
            if (string.IsNullOrEmpty(baseName)) continue;

            if (dirNames.Contains(baseName))
            {
                var matchDir = Path.Combine(path, baseName);
                archives.Add((file, matchDir));
            }
        }

        // Recurse into subdirectories
        foreach (var dir in dirs)
        {
            ScanDir(dir.FullPath, depth + 1, config, archives, ref scanned, progress, ct);
        }
    }

    private static bool IsArchive(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ArchiveExtensions.Contains(ext)) return true;

        // Handle double extensions like .tar.gz
        if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bz2", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xz", StringComparison.OrdinalIgnoreCase))
        {
            var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));
            if (inner.Equals(".tar", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string GetArchiveBaseName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);

        // Handle .tar.gz, .tar.bz2, .tar.xz
        var ext = Path.GetExtension(fileName);
        if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bz2", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".xz", StringComparison.OrdinalIgnoreCase))
        {
            var innerExt = Path.GetExtension(name);
            if (innerExt.Equals(".tar", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
        }

        return name;
    }

    /// <summary>
    /// Check what percentage of files in a .zip archive already exist in the target directory.
    /// Returns -1 if the zip can't be read.
    /// </summary>
    private static int CheckZipOverlap(string zipPath, string dirPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(Win32FileSystem.EnsureLongPath(zipPath));
            if (archive.Entries.Count == 0) return -1;

            var totalFiles = 0;
            var matchedFiles = 0;

            foreach (var entry in archive.Entries)
            {
                // Skip directory entries
                if (string.IsNullOrEmpty(entry.Name)) continue;
                totalFiles++;

                var expectedPath = Path.Combine(dirPath, entry.FullName.Replace('/', '\\'));
                if (File.Exists(expectedPath))
                    matchedFiles++;
            }

            if (totalFiles == 0) return -1;
            return (int)((matchedFiles * 100.0) / totalFiles);
        }
        catch
        {
            return -1;
        }
    }
}
