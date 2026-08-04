namespace Scour.Core.Services;

public static class AppRuntime
{
    public const string PortableMarkerFileName = "portable.flag";
    private static readonly bool Portable = IsPortableInstallation(AppContext.BaseDirectory);

    public static bool IsPortable => Portable;

    public static string DataDirectory => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scour");

    public static bool IsPortableInstallation(string baseDirectory)
    {
        try
        {
            if (File.Exists(Path.Combine(baseDirectory, PortableMarkerFileName)))
                return true;

            var root = Path.GetPathRoot(baseDirectory);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Removable;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
