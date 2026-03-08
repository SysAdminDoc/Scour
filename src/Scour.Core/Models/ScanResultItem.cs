using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Scour.Core;

public class ScanResultItem : INotifyPropertyChanged
{
    public required string FullPath { get; init; }
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime Modified { get; init; }
    public string Group { get; init; } = ""; // for duplicate grouping
    public string Detail { get; init; } = ""; // scanner-specific info
    public bool IsDirectory { get; init; }

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public string ParentFolder => System.IO.Path.GetDirectoryName(FullPath) ?? "";

    public string SizeFormatted => FormatSize(SizeBytes);
    public string ModifiedFormatted => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes == 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
