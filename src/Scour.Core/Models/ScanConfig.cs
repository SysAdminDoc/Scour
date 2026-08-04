namespace Scour.Core;

public class ScanConfig
{
    private readonly object _changedPathLock = new();
    private IReadOnlySet<string>? _normalizedChangedPaths;

    public required string RootPath { get; init; }
    public int MaxDepth { get; init; } = -1; // -1 = unlimited
    public bool SkipHidden { get; init; }
    public bool SkipSystem { get; init; } = true;
    public List<string> ExcludedDirectories { get; init; } = [];
    public List<string> IgnoreFiles { get; init; } = [];
    public bool Ignore0KbFiles { get; init; } = true;
    public long MinFileSizeBytes { get; init; }
    public long MaxFileSizeBytes { get; init; } // 0 = no limit
    public int MinFolderAgeHours { get; init; }
    public IReadOnlySet<string>? ChangedPaths { get; init; }

    public bool IsExcludedDirectory(string fullPath, string name)
        => Services.DirectoryExclusionMatcher.IsExcluded(ExcludedDirectories, fullPath, name);

    public bool MayContainChangedPath(string path)
    {
        if (ChangedPaths == null)
            return true;

        var normalized = NormalizePath(path);
        return GetNormalizedChangedPaths().Any(changed =>
            IsSameOrDescendant(normalized, changed) || IsSameOrDescendant(changed, normalized));
    }

    public bool IsChangedPath(string path)
    {
        if (ChangedPaths == null)
            return true;

        var normalized = NormalizePath(path);
        return GetNormalizedChangedPaths().Any(changed => IsSameOrDescendant(normalized, changed));
    }

    private IReadOnlySet<string> GetNormalizedChangedPaths()
    {
        if (_normalizedChangedPaths != null)
            return _normalizedChangedPaths;

        lock (_changedPathLock)
        {
            return _normalizedChangedPaths ??= ChangedPaths!
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsSameOrDescendant(string path, string parent)
    {
        if (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path);
            if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[4..];
            }

            return Path.TrimEndingDirectorySeparator(normalized);
        }
        catch (ArgumentException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
