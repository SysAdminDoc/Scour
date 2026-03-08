using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Scour.Core;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Scanners;

/// <summary>
/// Finds broken Windows shortcuts (.lnk files) whose targets no longer exist.
/// Uses COM IShellLink to resolve shortcut targets.
/// </summary>
public sealed class BrokenShortcutScanner : ScannerBase
{
    public override string Name => "Broken Shortcuts";
    public override string Description => "Find .lnk shortcuts with missing targets";
    public override string IconGlyph => "\uE71B"; // Link icon

    public override IReadOnlyList<ColumnDefinition> ResultColumns =>
    [
        new("Name", nameof(ScanResultItem.Name), 220),
        new("Shortcut Path", nameof(ScanResultItem.FullPath), 380),
        new("Missing Target", nameof(ScanResultItem.Detail), 380),
        new("Modified", nameof(ScanResultItem.ModifiedFormatted), 130),
    ];

    public override async Task ScanAsync(ScanConfig config, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        _results.Clear();
        var scanned = 0;

        await Task.Run(() => ScanDir(config.RootPath, 0, config, ref scanned, progress, ct), ct);

        progress.Report(new ScanProgress($"Found {_results.Count} broken shortcuts", _results.Count, _results.Count));
    }

    private void ScanDir(string path, int depth, ScanConfig config,
        ref int scanned, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (config.MaxDepth >= 0 && depth > config.MaxDepth) return;

        try
        {
            foreach (var entry in Win32FileSystem.EnumerateEntries(path))
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    if (config.SkipHidden && entry.IsHidden) continue;
                    if (config.SkipSystem && entry.IsSystem) continue;
                    if (config.ExcludedDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    ScanDir(entry.FullPath, depth + 1, config, ref scanned, progress, ct);
                    continue;
                }

                if (entry.IsDirectory) continue;
                if (!entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;

                scanned++;
                if (scanned % 200 == 0)
                    progress.Report(new ScanProgress($"Checking shortcuts: {path}", scanned, 0, true));

                var target = ResolveShortcutTarget(entry.FullPath);
                if (target == null) continue; // Couldn't read the shortcut

                // Check if target exists
                var targetExists = false;
                try
                {
                    if (Directory.Exists(target) || File.Exists(target))
                        targetExists = true;
                }
                catch { }

                if (!targetExists)
                {
                    AddResult(new ScanResultItem
                    {
                        FullPath = entry.FullPath,
                        Name = entry.Name,
                        SizeBytes = entry.SizeBytes,
                        Modified = entry.LastWriteTime,
                        Detail = target,
                    });
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            var link = (IShellLink)shellLink;
            var file = (IPersistFile)link;
            file.Load(lnkPath, 0); // STGM_READ

            // Don't resolve - just read the stored path (faster, no UI prompts)
            var sb = new char[1024];
            link.GetPath(sb, sb.Length, out _, 0x04); // SLGP_RAWPATH

            var target = new string(sb).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(target)) return null;

            // Expand environment variables
            target = Environment.ExpandEnvironmentVariables(target);
            return target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shellLink != null)
                Marshal.ReleaseComObject(shellLink);
        }
    }

    // COM interop for reading .lnk files
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszFile,
            int cch, out WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public ComTypes.FILETIME ftCreationTime;
        public ComTypes.FILETIME ftLastAccessTime;
        public ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    private static class ComTypes
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }
    }
}
