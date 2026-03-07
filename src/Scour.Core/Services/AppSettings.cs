using System.Text.Json;

namespace Scour.Core.Services;

public class AppSettings
{
    public string RootPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public int MaxDepth { get; set; } = -1;
    public bool SkipHidden { get; set; }
    public bool SkipSystem { get; set; } = true;
    public bool Ignore0Kb { get; set; } = true;
    public DeleteMode DeleteMode { get; set; } = DeleteMode.RecycleBin;
    public List<string> ExcludedDirectories { get; set; } =
    [
        "System Volume Information", "RECYCLER", "$RECYCLE.BIN",
        "winsxs", "System32", "GAC_MSIL", "GAC_32",
        "node_modules", ".git", ".svn", ".hg"
    ];
    public List<string> IgnoreFiles { get; set; } =
    [
        "desktop.ini", "Thumbs.db", ".DS_Store", "._*"
    ];
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 860;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Scour", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
