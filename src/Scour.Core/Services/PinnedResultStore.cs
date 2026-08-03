using System.Text.Json;

namespace Scour.Core.Services;

public sealed class PinnedResultStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private readonly HashSet<string> _keys;

    public PinnedResultStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scour", "pins.json");
        _keys = Load();
    }

    public bool IsPinned(string scannerName, string fullPath)
    {
        lock (_sync)
            return _keys.Contains(Key(scannerName, fullPath));
    }

    public void SetPinned(string scannerName, string fullPath, bool pinned)
    {
        lock (_sync)
        {
            var key = Key(scannerName, fullPath);
            if (pinned)
                _keys.Add(key);
            else
                _keys.Remove(key);
            Save();
        }
    }

    private HashSet<string> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var values = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path));
                return new HashSet<string>(values ?? [], StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // A corrupt pin file should not prevent scanning; the next pin action repairs it.
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_keys.Order(StringComparer.OrdinalIgnoreCase)));
        if (File.Exists(_path))
            File.Replace(temporaryPath, _path, null);
        else
            File.Move(temporaryPath, _path);
    }

    private static string Key(string scannerName, string fullPath)
        => $"{scannerName.Trim()}\n{Path.GetFullPath(fullPath)}";
}
