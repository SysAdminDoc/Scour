using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Scour.Core;
using Scour.Core.Services;

namespace Scour.App.Controls;

public sealed class FolderTreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root),
        typeof(FolderSizeNode),
        typeof(FolderTreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Color[] Palette =
    [
        Color.FromRgb(137, 180, 250),
        Color.FromRgb(203, 166, 247),
        Color.FromRgb(166, 227, 161),
        Color.FromRgb(249, 226, 175),
        Color.FromRgb(245, 194, 231),
        Color.FromRgb(148, 226, 213),
        Color.FromRgb(250, 179, 135),
    ];

    public FolderSizeNode? Root
    {
        get => (FolderSizeNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 37)), null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        if (Root == null || Root.SizeBytes <= 0)
            return;

        var rectangles = TreemapLayout.Layout(Root, ActualWidth, ActualHeight);
        foreach (var rectangle in rectangles)
        {
            var bounds = new Rect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            if (bounds.Width < 1 || bounds.Height < 1)
                continue;

            var colorIndex = unchecked((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(rectangle.Node.FullPath)) % (uint)Palette.Length;
            var color = Palette[(int)colorIndex];
            var brush = new SolidColorBrush(Color.FromArgb((byte)(rectangle.Depth == 0 ? 210 : 155), color.R, color.G, color.B));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(24, 24, 37)), 1);
            drawingContext.DrawRectangle(brush, pen, bounds);

            if (bounds.Width < 64 || bounds.Height < 24)
                continue;

            var label = rectangle.Node.Name;
            if (bounds.Width < 130)
                label = FitLabel(label, bounds.Width - 10);
            if (string.IsNullOrEmpty(label))
                continue;

            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                rectangle.Depth == 0 ? 11 : 10,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, bounds.Width - 10),
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawingContext.DrawText(text, new Point(bounds.X + 5, bounds.Y + 4));
        }
    }

    private static string FitLabel(string label, double width)
    {
        if (label.Length <= 18 || width >= 100)
            return label;
        return label[..Math.Min(15, label.Length)] + "…";
    }
}
