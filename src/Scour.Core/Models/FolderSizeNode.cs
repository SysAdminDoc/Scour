namespace Scour.Core;

public sealed class FolderSizeNode
{
    public FolderSizeNode(string name, string fullPath, bool isFileBucket = false)
    {
        Name = name;
        FullPath = fullPath;
        IsFileBucket = isFileBucket;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsFileBucket { get; }
    public long SizeBytes { get; set; }
    public List<FolderSizeNode> Children { get; } = [];
}
