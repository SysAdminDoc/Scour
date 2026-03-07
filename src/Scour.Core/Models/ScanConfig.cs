namespace Scour.Core;

public class ScanConfig
{
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
}
