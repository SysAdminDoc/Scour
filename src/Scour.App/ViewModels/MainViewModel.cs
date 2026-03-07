using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Services;
using Scour.Scanners;

namespace Scour.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly AppSettings _settings;

    public ObservableCollection<ScannerViewModel> Scanners { get; } = [];

    private ScannerViewModel? _activeScannerVm;
    public ScannerViewModel? ActiveScanner
    {
        get => _activeScannerVm;
        set => SetProperty(ref _activeScannerVm, value);
    }

    private string _rootPath = "";
    public string RootPath
    {
        get => _rootPath;
        set { if (SetProperty(ref _rootPath, value)) _settings.RootPath = value; }
    }

    private int _maxDepth = -1;
    public int MaxDepth
    {
        get => _maxDepth;
        set { if (SetProperty(ref _maxDepth, value)) _settings.MaxDepth = value; }
    }

    private bool _skipHidden;
    public bool SkipHidden
    {
        get => _skipHidden;
        set { if (SetProperty(ref _skipHidden, value)) _settings.SkipHidden = value; }
    }

    private bool _skipSystem = true;
    public bool SkipSystem
    {
        get => _skipSystem;
        set { if (SetProperty(ref _skipSystem, value)) _settings.SkipSystem = value; }
    }

    private bool _ignore0Kb = true;
    public bool Ignore0Kb
    {
        get => _ignore0Kb;
        set { if (SetProperty(ref _ignore0Kb, value)) _settings.Ignore0Kb = value; }
    }

    private DeleteMode _deleteMode = DeleteMode.RecycleBin;
    public DeleteMode DeleteMode
    {
        get => _deleteMode;
        set { if (SetProperty(ref _deleteMode, value)) _settings.DeleteMode = value; }
    }

    public ObservableCollection<string> ExcludedDirectories { get; } = [];

    private string _newExcludeDir = "";
    public string NewExcludeDir { get => _newExcludeDir; set => SetProperty(ref _newExcludeDir, value); }

    public ICommand BrowseCommand { get; }
    public ICommand ScanActiveCommand { get; }
    public ICommand ScanAllCommand { get; }
    public ICommand DeleteActiveCommand { get; }
    public ICommand CancelActiveCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand AddExcludeDirCommand { get; }
    public ICommand RemoveExcludeDirCommand { get; }

    public MainViewModel()
    {
        _settings = AppSettings.Load();

        // Apply loaded settings
        _rootPath = _settings.RootPath;
        _maxDepth = _settings.MaxDepth;
        _skipHidden = _settings.SkipHidden;
        _skipSystem = _settings.SkipSystem;
        _ignore0Kb = _settings.Ignore0Kb;
        _deleteMode = _settings.DeleteMode;

        foreach (var dir in _settings.ExcludedDirectories)
            ExcludedDirectories.Add(dir);

        // Register all scanner modules
        IScannerModule[] modules =
        [
            new EmptyDirectoryScanner(),
            new DuplicateFileScanner(),
            new BigFileScanner(),
            new TempFileScanner(),
            new ZeroLengthFileScanner(),
            new OldFileScanner(),
            new BrokenSymlinkScanner(),
        ];

        foreach (var m in modules)
            Scanners.Add(new ScannerViewModel(m));

        ActiveScanner = Scanners.FirstOrDefault();

        BrowseCommand = new RelayCommand(_ => DoBrowse());
        ScanActiveCommand = new AsyncRelayCommand(DoScanActive, _ => ActiveScanner?.CanScan ?? false);
        ScanAllCommand = new AsyncRelayCommand(DoScanAll, _ => Scanners.All(s => s.CanScan));
        DeleteActiveCommand = new AsyncRelayCommand(DoDeleteActive, _ => ActiveScanner != null && !ActiveScanner.IsScanning && ActiveScanner.SelectedCount > 0);
        CancelActiveCommand = new RelayCommand(_ => DoCancelActive(), _ => ActiveScanner?.IsScanning ?? false);
        ExportCsvCommand = new RelayCommand(_ => DoExportCsv(), _ => ActiveScanner?.HasResults ?? false);
        ExportJsonCommand = new RelayCommand(_ => DoExportJson(), _ => ActiveScanner?.HasResults ?? false);
        AddExcludeDirCommand = new RelayCommand(_ => DoAddExcludeDir());
        RemoveExcludeDirCommand = new RelayCommand(DoRemoveExcludeDir);
    }

    public void SaveSettings()
    {
        _settings.ExcludedDirectories = [.. ExcludedDirectories];
        _settings.Save();
    }

    public void ApplyWindowSettings(System.Windows.Window window)
    {
        if (!double.IsNaN(_settings.WindowWidth)) window.Width = _settings.WindowWidth;
        if (!double.IsNaN(_settings.WindowHeight)) window.Height = _settings.WindowHeight;
        if (!double.IsNaN(_settings.WindowLeft)) window.Left = _settings.WindowLeft;
        if (!double.IsNaN(_settings.WindowTop)) window.Top = _settings.WindowTop;
    }

    public void SaveWindowSettings(System.Windows.Window window)
    {
        if (window.WindowState == System.Windows.WindowState.Normal)
        {
            _settings.WindowWidth = window.Width;
            _settings.WindowHeight = window.Height;
            _settings.WindowLeft = window.Left;
            _settings.WindowTop = window.Top;
        }
        _settings.Save();
    }

    private ScanConfig BuildConfig() => new()
    {
        RootPath = RootPath,
        MaxDepth = MaxDepth,
        SkipHidden = SkipHidden,
        SkipSystem = SkipSystem,
        Ignore0KbFiles = Ignore0Kb,
        ExcludedDirectories = [.. ExcludedDirectories],
        IgnoreFiles = [.. _settings.IgnoreFiles],
    };

    private void DoBrowse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select folder to scan",
            InitialDirectory = Directory.Exists(RootPath) ? RootPath : null,
        };

        if (dialog.ShowDialog() == true)
            RootPath = dialog.FolderName;
    }

    public void SetRootPath(string path)
    {
        if (Directory.Exists(path))
            RootPath = path;
    }

    private async Task DoScanActive(object? _)
    {
        if (ActiveScanner == null) return;
        ActiveScanner.BuildConfig = BuildConfig();
        await ActiveScanner.RunScanAsync();
    }

    private async Task DoScanAll(object? _)
    {
        var config = BuildConfig();
        var tasks = new List<Task>();
        foreach (var scanner in Scanners)
        {
            scanner.BuildConfig = config;
            tasks.Add(scanner.RunScanAsync());
        }
        await Task.WhenAll(tasks);
    }

    private async Task DoDeleteActive(object? _)
    {
        if (ActiveScanner == null) return;
        await ActiveScanner.RunDeleteAsync(DeleteMode);
    }

    private void DoCancelActive()
    {
        ActiveScanner?.CancelCommand.Execute(null);
    }

    private void DoExportCsv()
    {
        if (ActiveScanner == null || !ActiveScanner.HasResults) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Results as CSV",
            Filter = "CSV Files (*.csv)|*.csv",
            FileName = $"Scour_{ActiveScanner.Name.Replace(" ", "")}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Selected,Name,Path,Size,Modified,Detail,Group");
        foreach (var r in ActiveScanner.Results)
        {
            sb.AppendLine($"{r.IsSelected},\"{Escape(r.Name)}\",\"{Escape(r.FullPath)}\",{r.SizeBytes},\"{r.ModifiedFormatted}\",\"{Escape(r.Detail)}\",\"{Escape(r.Group)}\"");
        }
        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
        ActiveScanner.StatusText = $"Exported {ActiveScanner.Results.Count} results to CSV";
    }

    private void DoExportJson()
    {
        if (ActiveScanner == null || !ActiveScanner.HasResults) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export Results as JSON",
            Filter = "JSON Files (*.json)|*.json",
            FileName = $"Scour_{ActiveScanner.Name.Replace(" ", "")}_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (dialog.ShowDialog() != true) return;

        var data = ActiveScanner.Results.Select(r => new
        {
            r.Name,
            r.FullPath,
            r.SizeBytes,
            Size = r.SizeFormatted,
            Modified = r.ModifiedFormatted,
            r.Detail,
            r.Group,
            r.IsSelected,
            r.IsDirectory
        }).ToList();

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(dialog.FileName, json, Encoding.UTF8);
        ActiveScanner.StatusText = $"Exported {ActiveScanner.Results.Count} results to JSON";
    }

    private static string Escape(string s) => s.Replace("\"", "\"\"");

    private void DoAddExcludeDir()
    {
        var dir = NewExcludeDir.Trim();
        if (!string.IsNullOrEmpty(dir) && !ExcludedDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase))
        {
            ExcludedDirectories.Add(dir);
            NewExcludeDir = "";
            SyncExcludedDirs();
        }
    }

    private void DoRemoveExcludeDir(object? param)
    {
        if (param is string dir)
        {
            ExcludedDirectories.Remove(dir);
            SyncExcludedDirs();
        }
    }

    private void SyncExcludedDirs()
    {
        _settings.ExcludedDirectories = [.. ExcludedDirectories];
        _settings.Save();
    }
}
