using Scour.Core.Native;

namespace Scour.Core.Services;

public static class DeletionService
{
    public static void Delete(string path, bool isDirectory, DeleteMode mode)
    {
        if (mode == DeleteMode.Simulate)
            return;

        if (mode == DeleteMode.RecycleBin)
        {
            if (!Win32FileSystem.SendToRecycleBin(path))
                throw new IOException($"Failed to send to recycle bin: {path}");
            return;
        }

        // Permanent delete
        if (isDirectory)
            Win32FileSystem.DeleteDirectory(path);
        else
            Win32FileSystem.DeleteFile(path);
    }
}
