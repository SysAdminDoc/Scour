using Microsoft.Win32;

namespace Scour.Core.Services;

/// <summary>
/// Manages Windows Explorer context menu integration for "Scan with Scour".
/// Adds/removes registry entries under HKCU\Software\Classes\Directory\shell\Scour.
/// </summary>
public static class ContextMenuService
{
    private const string DirShellKey = @"Directory\shell\Scour";
    private const string DirBgShellKey = @"Directory\Background\shell\Scour";
    private const string DriveShellKey = @"Drive\shell\Scour";

    /// <summary>
    /// Check if the context menu entry is currently registered.
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + DirShellKey);
            return key != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// Register "Scan with Scour" in the Windows Explorer context menu.
    /// Uses HKCU so no admin rights required.
    /// </summary>
    public static void Register()
    {
        var exePath = GetExePath();
        if (string.IsNullOrEmpty(exePath)) return;

        var command = $"\"{exePath}\" --scan \"%V\"";

        RegisterShellEntry(@"Software\Classes\" + DirShellKey, command, exePath);
        RegisterShellEntry(@"Software\Classes\" + DirBgShellKey, command, exePath);
        RegisterShellEntry(@"Software\Classes\" + DriveShellKey, command, exePath);
    }

    /// <summary>
    /// Remove the context menu entries.
    /// </summary>
    public static void Unregister()
    {
        TryDeleteKey(@"Software\Classes\" + DirShellKey);
        TryDeleteKey(@"Software\Classes\" + DirBgShellKey);
        TryDeleteKey(@"Software\Classes\" + DriveShellKey);
    }

    private static void RegisterShellEntry(string keyPath, string command, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            if (key == null) return;

            key.SetValue("", "Scan with Scour");
            key.SetValue("Icon", exePath);

            using var cmdKey = key.CreateSubKey("command");
            cmdKey?.SetValue("", command);
        }
        catch { }
    }

    private static void TryDeleteKey(string keyPath)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        }
        catch { }
    }

    private static string GetExePath()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        if (entry == null) return "";

        var location = entry.Location;
        // For single-file publish, Location is empty - use ProcessPath instead
        if (string.IsNullOrEmpty(location))
            location = Environment.ProcessPath ?? "";

        return location;
    }
}
