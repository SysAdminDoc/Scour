using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Scour.Core;
using Scour.Core.Interfaces;

namespace Scour.App.ViewModels;

public class ScannerViewModel : ViewModelBase
{
    private readonly IScannerModule _scanner;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _stopwatch = new();
    private readonly object _resultsLock = new();

    public IScannerModule Scanner => _scanner;
    public string Name => _scanner.Name;
    public string Description => _scanner.Description;
    public string IconGlyph => _scanner.IconGlyph;
    public IReadOnlyList<ColumnDefinition> Columns => _scanner.ResultColumns;

    private string _statusText = "Ready";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private double _progress;
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; set => SetProperty(ref _isIndeterminate, value); }

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set { SetProperty(ref _isScanning, value); OnPropertyChanged(nameof(CanScan)); } }

    private bool _hasResults;
    public bool HasResults { get => _hasResults; set => SetProperty(ref _hasResults, value); }

    public bool CanScan => !IsScanning;

    public ObservableCollection<ScanResultItem> Results { get; } = [];

    private int _selectedCount;
    public int SelectedCount { get => _selectedCount; set => SetProperty(ref _selectedCount, value); }

    private long _selectedSize;
    public long SelectedSize { get => _selectedSize; set { SetProperty(ref _selectedSize, value); OnPropertyChanged(nameof(SelectedSizeFormatted)); } }
    public string SelectedSizeFormatted => new ScanResultItem { FullPath = "", SizeBytes = _selectedSize }.SizeFormatted;

    private string _elapsedTime = "";
    public string ElapsedTime { get => _elapsedTime; set => SetProperty(ref _elapsedTime, value); }

    private int _errorCount;
    public int ErrorCount { get => _errorCount; set => SetProperty(ref _errorCount, value); }

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                ApplyFilter();
        }
    }

    private ListCollectionView? _filteredResults;
    public ListCollectionView? FilteredResults { get => _filteredResults; set => SetProperty(ref _filteredResults, value); }

    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand InvertSelectionCommand { get; }
    public ICommand OpenFileLocationCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand RemoveFromListCommand { get; }

    public ScannerViewModel(IScannerModule scanner)
    {
        _scanner = scanner;
        ScanCommand = new AsyncRelayCommand(DoScan, _ => CanScan);
        CancelCommand = new RelayCommand(_ => DoCancel(), _ => IsScanning);
        DeleteCommand = new AsyncRelayCommand(DoDelete, _ => !IsScanning && SelectedCount > 0);
        SelectAllCommand = new RelayCommand(_ => DoSelectAll());
        SelectNoneCommand = new RelayCommand(_ => DoSelectNone());
        InvertSelectionCommand = new RelayCommand(_ => DoInvertSelection());
        OpenFileLocationCommand = new RelayCommand(DoOpenFileLocation);
        CopyPathCommand = new RelayCommand(DoCopyPath);
        RemoveFromListCommand = new RelayCommand(DoRemoveFromList);

        Results.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ScanResultItem item in e.NewItems)
                    item.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ScanResultItem.IsSelected)) UpdateSelectedCount(); };
        };

        // Set up collection view for filtering
        FilteredResults = new ListCollectionView(Results);
    }

    public ScanConfig? BuildConfig { get; set; }

    public async Task RunScanAsync()
    {
        if (BuildConfig == null || IsScanning) return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        Results.Clear();
        _scanner.Reset();
        HasResults = false;
        Progress = 0;
        ErrorCount = 0;
        StatusText = "Scanning...";
        _stopwatch.Restart();

        var progressHandler = new Progress<ScanProgress>(p =>
        {
            StatusText = p.Status;
            IsIndeterminate = p.IsIndeterminate;
            if (p.Total > 0)
                Progress = (double)p.Current / p.Total * 100;
            if (p.Status.StartsWith("Error:"))
                ErrorCount++;
            ElapsedTime = _stopwatch.Elapsed.ToString(@"mm\:ss");
        });

        try
        {
            await _scanner.ScanAsync(BuildConfig, progressHandler, _cts.Token);

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in _scanner.Results)
                    Results.Add(item);
                HasResults = Results.Count > 0;
                UpdateSelectedCount();
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _stopwatch.Stop();
            IsScanning = false;
            IsIndeterminate = false;
            Progress = 100;
            ElapsedTime = _stopwatch.Elapsed.ToString(@"mm\:ss\.f");
        }
    }

    public async Task RunDeleteAsync(DeleteMode deleteMode)
    {
        var selected = Results.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0 || IsScanning) return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        Progress = 0;
        StatusText = "Deleting...";

        var progressHandler = new Progress<ScanProgress>(p =>
        {
            StatusText = p.Status;
            if (p.Total > 0)
                Progress = (double)p.Current / p.Total * 100;
        });

        try
        {
            await _scanner.DeleteSelectedAsync(selected, deleteMode, progressHandler, _cts.Token);

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in selected)
                    Results.Remove(item);
                HasResults = Results.Count > 0;
                UpdateSelectedCount();
            });

            StatusText = $"Deleted {selected.Count} items";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Deletion cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            Progress = 100;
        }
    }

    private async Task DoScan(object? _) => await RunScanAsync();

    private void DoCancel()
    {
        _cts?.Cancel();
    }

    private async Task DoDelete(object? param)
    {
        var deleteMode = param is DeleteMode dm ? dm : DeleteMode.RecycleBin;
        await RunDeleteAsync(deleteMode);
    }

    private void DoSelectAll()
    {
        foreach (var r in Results) r.IsSelected = true;
        UpdateSelectedCount();
    }

    private void DoSelectNone()
    {
        foreach (var r in Results) r.IsSelected = false;
        UpdateSelectedCount();
    }

    private void DoInvertSelection()
    {
        foreach (var r in Results) r.IsSelected = !r.IsSelected;
        UpdateSelectedCount();
    }

    private void DoOpenFileLocation(object? param)
    {
        if (param is ScanResultItem item)
        {
            try
            {
                var dir = item.IsDirectory ? item.FullPath : System.IO.Path.GetDirectoryName(item.FullPath);
                if (dir != null && System.IO.Directory.Exists(dir))
                    Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
            }
            catch { }
        }
    }

    private void DoCopyPath(object? param)
    {
        if (param is ScanResultItem item)
            Clipboard.SetText(item.FullPath);
    }

    private void DoRemoveFromList(object? param)
    {
        if (param is ScanResultItem item)
        {
            Results.Remove(item);
            HasResults = Results.Count > 0;
            UpdateSelectedCount();
        }
    }

    public void UpdateSelectedCount()
    {
        SelectedCount = Results.Count(r => r.IsSelected);
        SelectedSize = Results.Where(r => r.IsSelected).Sum(r => r.SizeBytes);
    }

    private void ApplyFilter()
    {
        if (FilteredResults == null) return;

        if (string.IsNullOrWhiteSpace(_filterText))
        {
            FilteredResults.Filter = null;
        }
        else
        {
            var filter = _filterText.ToLowerInvariant();
            FilteredResults.Filter = obj =>
            {
                if (obj is ScanResultItem item)
                {
                    return item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || item.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || item.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            };
        }
    }
}
