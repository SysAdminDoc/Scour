namespace Scour.Core.Services;

public static class DirectoryExclusionMatcher
{
    public static bool IsExcluded(IEnumerable<string> exclusions, string fullPath, string name)
    {
        foreach (var exclusion in exclusions)
        {
            var candidate = exclusion.Trim();
            if (candidate.Length == 0)
                continue;

            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return true;

            if (LooksLikePath(candidate) && PathsEqual(candidate, fullPath))
                return true;
        }

        return false;
    }

    private static bool LooksLikePath(string value)
        => Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }

        return Path.TrimEndingDirectorySeparator(normalized);
    }
}
