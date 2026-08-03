namespace Scour.Core.Services;

public readonly record struct TreemapRectangle(
    FolderSizeNode Node,
    double X,
    double Y,
    double Width,
    double Height,
    int Depth);

public static class TreemapLayout
{
    public static IReadOnlyList<TreemapRectangle> Layout(
        FolderSizeNode root,
        double width,
        double height,
        int maxDepth = 4,
        int maxRectangles = 2000)
    {
        if (width <= 0 || height <= 0 || root.SizeBytes <= 0 || maxDepth < 0 || maxRectangles <= 0)
            return [];

        var rectangles = new List<TreemapRectangle>(Math.Min(maxRectangles, 256));
        LayoutChildren(root, 0, 0, width, height, 0, maxDepth, maxRectangles, rectangles);
        return rectangles;
    }

    private static void LayoutChildren(
        FolderSizeNode parent,
        double x,
        double y,
        double width,
        double height,
        int depth,
        int maxDepth,
        int maxRectangles,
        List<TreemapRectangle> rectangles)
    {
        if (depth >= maxDepth || rectangles.Count >= maxRectangles)
            return;

        var children = parent.Children
            .Where(child => child.SizeBytes > 0)
            .OrderByDescending(child => child.SizeBytes)
            .ToList();
        var totalSize = children.Sum(child => (double)child.SizeBytes);
        if (totalSize <= 0)
            return;

        var horizontal = width >= height;
        var offset = 0d;
        foreach (var child in children)
        {
            if (rectangles.Count >= maxRectangles)
                break;

            var fraction = child.SizeBytes / totalSize;
            var childWidth = horizontal ? width * fraction : width;
            var childHeight = horizontal ? height : height * fraction;
            var childX = horizontal ? x + offset : x;
            var childY = horizontal ? y : y + offset;
            offset += horizontal ? childWidth : childHeight;

            rectangles.Add(new TreemapRectangle(child, childX, childY, childWidth, childHeight, depth));
            LayoutChildren(child, childX, childY, childWidth, childHeight, depth + 1, maxDepth, maxRectangles, rectangles);
        }
    }
}
