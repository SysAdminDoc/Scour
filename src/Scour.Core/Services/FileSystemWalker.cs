using System.Collections.Concurrent;
using System.Threading.Channels;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Core.Services;

/// <summary>
/// Parallel recursive filesystem walker using Win32 native enumeration.
/// Walks once, streams entries through a Channel for consumers.
/// </summary>
public sealed class FileSystemWalker
{
    private readonly ScanConfig _config;
    private readonly HashSet<string> _excludedDirs;

    public FileSystemWalker(ScanConfig config)
    {
        _config = config;
        _excludedDirs = new HashSet<string>(
            config.ExcludedDirectories,
            StringComparer.OrdinalIgnoreCase
        );
    }

    /// <summary>
    /// Walk the directory tree and write entries into a channel.
    /// Consumers read from the channel as entries are discovered.
    /// </summary>
    public ChannelReader<FileEntry> WalkAsync(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(4096)
        {
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await WalkRecursiveParallel(_config.RootPath, 0, channel.Writer, ct);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        return channel.Reader;
    }

    /// <summary>
    /// Simple recursive walk that returns all directories with their file counts.
    /// Used by empty directory scanner.
    /// </summary>
    public async Task<List<DirectoryInfo>> GetAllDirectoriesAsync(
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var results = new ConcurrentBag<DirectoryInfo>();
        var count = 0;

        await Task.Run(() => WalkDirectories(_config.RootPath, 0, results, progress, ref count, ct), ct);

        return results.ToList();
    }

    private void WalkDirectories(string path, int depth,
        ConcurrentBag<DirectoryInfo> results,
        IProgress<ScanProgress>? progress,
        ref int count, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_config.MaxDepth >= 0 && depth > _config.MaxDepth)
            return;

        List<FileEntry> subdirs = [];
        var fileCount = 0;
        var totalFileSize = 0L;
        var hasNonIgnoredFiles = false;

        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(path))
            {
                if (entry.IsDirectory)
                {
                    if (ShouldSkipDirectory(entry))
                        continue;
                    subdirs.Add(entry);
                }
                else
                {
                    fileCount++;
                    totalFileSize += entry.SizeBytes;
                    if (!IsIgnoredFile(entry))
                        hasNonIgnoredFiles = true;
                }
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        count++;
        if (count % 100 == 0)
            progress?.Report(new ScanProgress(path, count, 0, true));

        results.Add(new DirectoryInfo
        {
            FullPath = path,
            Name = Path.GetFileName(path),
            FileCount = fileCount,
            TotalFileSize = totalFileSize,
            HasNonIgnoredFiles = hasNonIgnoredFiles,
            SubdirectoryCount = subdirs.Count,
            Depth = depth
        });

        foreach (var sub in subdirs)
        {
            WalkDirectories(sub.FullPath, depth + 1, results, progress, ref count, ct);
        }
    }

    private async Task WalkRecursiveParallel(string path, int depth,
        ChannelWriter<FileEntry> writer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_config.MaxDepth >= 0 && depth > _config.MaxDepth)
            return;

        List<FileEntry> subdirs = [];

        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(path))
            {
                if (entry.IsDirectory)
                {
                    if (ShouldSkipDirectory(entry))
                        continue;
                    subdirs.Add(entry);
                }

                await writer.WriteAsync(entry, ct);
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        // Parallel subdirectory walking for performance
        if (subdirs.Count > 1 && depth < 3)
        {
            await Parallel.ForEachAsync(subdirs,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                async (sub, token) =>
                {
                    await WalkRecursiveParallel(sub.FullPath, depth + 1, writer, token);
                });
        }
        else
        {
            foreach (var sub in subdirs)
            {
                await WalkRecursiveParallel(sub.FullPath, depth + 1, writer, ct);
            }
        }
    }

    private bool ShouldSkipDirectory(FileEntry entry)
    {
        if (entry.IsReparsePoint) return true;
        if (_config.SkipHidden && entry.IsHidden) return true;
        if (_config.SkipSystem && entry.IsSystem) return true;
        if (_excludedDirs.Contains(entry.Name)) return true;
        return false;
    }

    private bool IsIgnoredFile(FileEntry entry)
    {
        if (_config.Ignore0KbFiles && entry.SizeBytes == 0)
            return true;

        foreach (var pattern in _config.IgnoreFiles)
        {
            if (string.IsNullOrEmpty(pattern)) continue;

            if (pattern.Contains('*'))
            {
                if (MatchWildcard(entry.Name, pattern))
                    return true;
            }
            else if (string.Equals(entry.Name, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchWildcard(string input, string pattern)
    {
        // Simple wildcard matcher: *.ext, prefix*, *middle*
        var parts = pattern.Split('*');
        var pos = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (string.IsNullOrEmpty(parts[i])) continue;
            var idx = input.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            if (i == 0 && !pattern.StartsWith('*') && idx != 0) return false;
            pos = idx + parts[i].Length;
        }
        if (!pattern.EndsWith('*') && pos != input.Length) return false;
        return true;
    }
}

public struct DirectoryInfo
{
    public string FullPath;
    public string Name;
    public int FileCount;
    public long TotalFileSize;
    public bool HasNonIgnoredFiles;
    public int SubdirectoryCount;
    public int Depth;
}
