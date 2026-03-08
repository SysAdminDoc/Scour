namespace Scour.Core.Interfaces;

/// <summary>
/// Every scanner module implements this interface.
/// The UI creates one tab per registered IScannerModule.
/// </summary>
public interface IScannerModule
{
    string Name { get; }
    string Description { get; }
    string IconGlyph { get; } // Segoe Fluent Icons glyph

    /// <summary>
    /// Column definitions for the results DataGrid.
    /// Each scanner can define its own columns.
    /// </summary>
    IReadOnlyList<ColumnDefinition> ResultColumns { get; }

    /// <summary>
    /// Callback fired when a result is found during scanning. Used for real-time streaming to UI.
    /// </summary>
    Action<ScanResultItem>? OnItemFound { get; set; }

    /// <summary>
    /// Run the scan. Push results into the channel as they are found.
    /// </summary>
    Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct);

    /// <summary>
    /// Results populated during ScanAsync.
    /// </summary>
    IReadOnlyList<ScanResultItem> Results { get; }

    /// <summary>
    /// Delete/act on the selected items.
    /// </summary>
    Task DeleteSelectedAsync(IEnumerable<ScanResultItem> items, DeleteMode mode, IProgress<ScanProgress> progress, CancellationToken ct);

    /// <summary>
    /// Reset state for a fresh scan.
    /// </summary>
    void Reset();
}

public record ColumnDefinition(string Header, string BindingPath, double Width = 0, bool RightAlign = false);

public record ScanProgress(string Status, int Current, int Total, bool IsIndeterminate = false);
