using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Services;

namespace Scour.Scanners;

public abstract class ScannerBase : IScannerModule
{
    protected readonly List<ScanResultItem> _results = [];

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string IconGlyph { get; }
    public abstract IReadOnlyList<ColumnDefinition> ResultColumns { get; }

    public IReadOnlyList<ScanResultItem> Results => _results;

    /// <summary>
    /// Callback fired when a result is found during scanning. Used for real-time streaming to UI.
    /// </summary>
    public Action<ScanResultItem>? OnItemFound { get; set; }

    /// <summary>
    /// Add a result and notify the UI for real-time streaming.
    /// </summary>
    protected void AddResult(ScanResultItem item)
    {
        _results.Add(item);
        OnItemFound?.Invoke(item);
    }

    public abstract Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct);

    public virtual async Task DeleteSelectedAsync(IEnumerable<ScanResultItem> items, DeleteMode mode,
        IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var list = items.OrderByDescending(i => i.FullPath.Length).ToList();
        var done = 0;

        await Task.Run(() =>
        {
            foreach (var item in list)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress.Report(new ScanProgress($"Deleting: {item.FullPath}", done, list.Count));
                try { DeletionService.Delete(item.FullPath, item.IsDirectory, mode); }
                catch (Exception ex) { progress.Report(new ScanProgress($"Error: {item.FullPath} - {ex.Message}", done, list.Count)); }
            }
        }, ct);
    }

    public void Reset() => _results.Clear();
}
