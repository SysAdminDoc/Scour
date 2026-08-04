using System.Buffers.Binary;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scour.App.Services;
using Scour.Cli;
using Scour.Core;
using Scour.Core.Native;
using Scour.Core.Services;
using Scour.Scanners;
using ScourThemeMode = Scour.Core.ThemeMode;

var failures = new List<string>();

Run("dHash identical samples match", () =>
{
    var samples = Enumerable.Repeat((byte)100, 72).ToArray();
    Assert(MediaFingerprint.ComputeDHash(samples) == MediaFingerprint.ComputeDHash(samples));
});

Run("dHash similar samples stay close", () =>
{
    var first = Enumerable.Range(0, 72).Select(index => (byte)(index % 16)).ToArray();
    var second = first.ToArray();
    second[0] = 255;
    var distance = MediaFingerprint.HammingDistance(
        MediaFingerprint.ComputeDHash(first),
        MediaFingerprint.ComputeDHash(second));
    Assert(distance <= 2, $"distance was {distance}");
});

Run("dHash rejects invalid sample dimensions", () =>
{
    try
    {
        MediaFingerprint.ComputeDHash(new byte[8]);
        throw new InvalidOperationException("Expected ArgumentException");
    }
    catch (ArgumentException)
    {
    }
});

Run("PNG dimensions and fingerprint are readable", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"scour-test-{Guid.NewGuid():N}.png");
    try
    {
        var pixels = new byte[32 * 24 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 40;
            pixels[index + 1] = 120;
            pixels[index + 2] = 220;
            pixels[index + 3] = 255;
        }

        var bitmap = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(path))
            encoder.Save(output);

        var fingerprint = MediaFingerprint.Compute(path);
        Assert(fingerprint != null);
        Assert(fingerprint!.Width == 32, $"width was {fingerprint.Width}");
        Assert(fingerprint.Height == 24, $"height was {fingerprint.Height}");
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
});

Run("media scanner groups similar PNGs and keeps one", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"scour-media-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        WritePng(Path.Combine(directory, "original.png"), 40);
        WritePng(Path.Combine(directory, "near-copy.png"), 42);
        WritePng(Path.Combine(directory, "different.png"), 200);

        var scanner = new MediaDuplicateScanner();
        scanner.ScanAsync(
                new Scour.Core.ScanConfig { RootPath = directory, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Count == 2, $"result count was {scanner.Results.Count}");
        Assert(scanner.Results.Count(item => item.IsSelected) == 1, "expected one safe-to-remove result");
        Assert(scanner.Results.Select(item => item.Group).Distinct().Count() == 1, "expected one duplicate group");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
});

Run("WinSxS scanner reports stale servicing data as protected", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"scour-winsxs-test-{Guid.NewGuid():N}");
    var installTemp = Path.Combine(directory, "InstallTemp");
    Directory.CreateDirectory(installTemp);
    try
    {
        var marker = Path.Combine(installTemp, "stale.tmp");
        File.WriteAllText(marker, "servicing residue");
        Directory.SetLastWriteTime(installTemp, DateTime.Now.AddDays(-14));
        File.SetLastWriteTime(marker, DateTime.Now.AddDays(-14));

        var scanner = new WinSxSScanner(directory);
        scanner.ScanAsync(
                new Scour.Core.ScanConfig { RootPath = directory, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var finding = scanner.Results.Single(item => item.Name == "InstallTemp");
        Assert(!finding.IsSelected, "WinSxS findings must never be selected by default");
        Assert(finding.Detail.Contains("audit only", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
});

Run("browser scanner reports cache data by profile", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"scour-browser-test-{Guid.NewGuid():N}");
    var chromeCache = Path.Combine(root, "Google", "Chrome", "User Data", "Default", "Cache");
    var firefoxCache = Path.Combine(root, "Mozilla", "Firefox", "Profiles", "demo.default-release", "cache2");
    Directory.CreateDirectory(chromeCache);
    Directory.CreateDirectory(firefoxCache);
    try
    {
        File.WriteAllBytes(Path.Combine(chromeCache, "data_0"), new byte[128]);
        File.WriteAllBytes(Path.Combine(firefoxCache, "entries"), new byte[256]);

        var scanner = new BrowserCacheScanner(root, root);
        scanner.ScanAsync(
                new Scour.Core.ScanConfig { RootPath = root, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Count == 2, $"result count was {scanner.Results.Count}");
        Assert(scanner.Results.Any(item => item.Detail.Contains("Chrome", StringComparison.OrdinalIgnoreCase)));
        Assert(scanner.Results.Any(item => item.Detail.Contains("Firefox", StringComparison.OrdinalIgnoreCase)));
        Assert(scanner.Results.All(item => item.IsSelected), "browser cache entries should be removable by default");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("system scanner surfaces protected storage actions", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"scour-system-test-{Guid.NewGuid():N}");
    var minidump = Path.Combine(root, "Minidump");
    Directory.CreateDirectory(minidump);
    try
    {
        File.WriteAllBytes(Path.Combine(root, "hiberfil.sys"), new byte[1024]);
        File.WriteAllBytes(Path.Combine(root, "pagefile.sys"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(root, "MEMORY.DMP"), new byte[512]);
        File.WriteAllBytes(Path.Combine(minidump, "mini.dmp"), new byte[256]);

        var scanner = new SystemSpaceScanner(root, root);
        scanner.ScanAsync(
                new Scour.Core.ScanConfig { RootPath = root, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Any(item => item.Name == "Hibernation file" && !item.IsSelected));
        Assert(scanner.Results.Any(item => item.Name == "Pagefile" && !item.IsSelected));
        Assert(scanner.Results.Count(item => item.Name == "Mini dump") == 1);
        Assert(scanner.Results.Single(item => item.Name == "Full memory dump").IsSelected);

        var actions = scanner.Results
            .Where(item => item.Name is "Hibernation file" or "Pagefile")
            .ToList();
        scanner.DeleteSelectedAsync(
                actions,
                Scour.Core.DeleteMode.Simulate,
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert(File.Exists(Path.Combine(root, "hiberfil.sys")), "simulate must not remove hiberfil.sys");
        Assert(File.Exists(Path.Combine(root, "pagefile.sys")), "simulate must not remove pagefile.sys");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("game scanner finds a Steam directory without a manifest", () =>
{
    var library = Path.Combine(Path.GetTempPath(), $"scour-steam-test-{Guid.NewGuid():N}");
    var steamApps = Path.Combine(library, "steamapps");
    var common = Path.Combine(steamApps, "common");
    Directory.CreateDirectory(common);
    try
    {
        var installed = Path.Combine(common, "Tracked Game");
        var orphan = Path.Combine(common, "Old Game");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(installed, "tracked.exe"), "tracked");
        File.WriteAllText(Path.Combine(orphan, "old.exe"), "old");
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_100.acf"),
            "\"AppState\" { \"appid\" \"100\" \"name\" \"Tracked Game\" \"installdir\" \"Tracked Game\" }");

        var scanner = new GameOrphanScanner(
            steamLibraryRoots: [library],
            epicManifestRoots: [],
            gogInstallRoots: []);
        scanner.ScanAsync(
                new Scour.Core.ScanConfig { RootPath = library, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Count == 1, $"result count was {scanner.Results.Count}");
        Assert(scanner.Results[0].Name == "Old Game");
        Assert(!scanner.Results[0].IsSelected, "orphaned game directories require review before deletion");
    }
    finally
    {
        if (Directory.Exists(library)) Directory.Delete(library, recursive: true);
    }
});

Run("VHDX scanner inventories large virtual disks without mutating them", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"scour-vhdx-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var vhdx = Path.Combine(root, "docker_data.vhdx");
    try
    {
        File.WriteAllBytes(vhdx, new byte[4096]);
        var scanner = new VhdxBloatScanner([root], minimumSizeBytes: 1024);
        scanner.ScanAsync(
                new Scour.Core.ScanConfig { RootPath = root, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Count == 1, $"result count was {scanner.Results.Count}");
        Assert(scanner.Results[0].Name == "docker_data.vhdx");
        Assert(scanner.Results[0].Detail.Contains("Docker", StringComparison.OrdinalIgnoreCase));
        Assert(!scanner.Results[0].IsSelected, "VHDX compaction requires explicit selection");

        scanner.DeleteSelectedAsync(
                scanner.Results,
                Scour.Core.DeleteMode.Simulate,
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert(File.Exists(vhdx), "simulate compaction must not remove the VHDX");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("Recycle Bin scanner parses original path and deletion metadata", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"scour-recycle-test-{Guid.NewGuid():N}");
    var sidRoot = Path.Combine(root, "$Recycle.Bin", "S-1-5-18");
    Directory.CreateDirectory(sidRoot);
    var infoPath = Path.Combine(sidRoot, "$I1234567890ABCDEF");
    var dataPath = Path.Combine(sidRoot, "$R1234567890ABCDEF");
    var originalPath = @"C:\Users\Test\Documents\old.txt";
    try
    {
        var payload = new byte[28 + originalPath.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), 2);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8, 8), 4096);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16, 8), DateTime.Now.AddDays(-2).ToFileTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24, 4), originalPath.Length);
        Encoding.Unicode.GetBytes(originalPath).CopyTo(payload.AsSpan(28));
        File.WriteAllBytes(infoPath, payload);
        File.WriteAllBytes(dataPath, new byte[64]);

        var scanner = new RecycleBinScanner([root]);
        scanner.ScanAsync(
                new Scour.Core.ScanConfig { RootPath = root, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Count == 1, $"result count was {scanner.Results.Count}");
        Assert(scanner.Results[0].Name == "old.txt");
        Assert(scanner.Results[0].SizeBytes == 4096);
        Assert(scanner.Results[0].Detail.Contains(originalPath, StringComparison.Ordinal));
        Assert(scanner.Results[0].Detail.Contains("S-1-5-18", StringComparison.Ordinal));
        Assert(!scanner.Results[0].IsSelected, "Recycle Bin inspection must remain read-only");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("MFT cache persists snapshots and applies USN changes", () =>
{
    var cachePath = Path.Combine(Path.GetTempPath(), $"scour-mft-test-{Guid.NewGuid():N}.bin");
    try
    {
        var entries = new Dictionary<long, MftReader.MftEntry>
        {
            [5] = new MftReader.MftEntry
            {
                FileReferenceNumber = 5,
                ParentFileReferenceNumber = 5,
                FileName = "",
                IsDirectory = true,
                FileAttributes = 0x10,
            },
            [10] = new MftReader.MftEntry
            {
                FileReferenceNumber = 10,
                ParentFileReferenceNumber = 5,
                FileName = "before.txt",
                IsDirectory = false,
                FileAttributes = 0,
            },
        };
        var snapshot = new MftReader.MftSnapshot('C', 123, 500, entries);
        var store = new MftCacheStore(cachePath);
        store.SaveSnapshot(snapshot);

        var loaded = store.LoadSnapshot();
        Assert(loaded != null);
        Assert(loaded!.DriveLetter == 'C' && loaded.JournalId == 123 && loaded.NextUsn == 500);
        Assert(loaded.Entries[10].FileName == "before.txt");

        var changes = new[]
        {
            new MftReader.UsnChange(10, 5, "after.txt", false, 0, 0x00002000, 501),
            new MftReader.UsnChange(11, 5, "deleted.tmp", false, 0, 0x00000200, 502),
        };
        loaded.Entries[11] = new MftReader.MftEntry
        {
            FileReferenceNumber = 11,
            ParentFileReferenceNumber = 5,
            FileName = "deleted.tmp",
        };
        MftReader.ApplyUsnChanges(loaded.Entries, changes);
        Assert(loaded.Entries[10].FileName == "after.txt");
        Assert(!loaded.Entries.ContainsKey(11), "USN delete must remove cached entries");
    }
    finally
    {
        if (File.Exists(cachePath)) File.Delete(cachePath);
    }
});

Run("BLAKE3 matches official empty and one-byte vectors", () =>
{
    var empty = Convert.ToHexStringLower(Blake3Hasher.ComputeHash(ReadOnlySpan<byte>.Empty));
    var zero = Convert.ToHexStringLower(Blake3Hasher.ComputeHash(new byte[] { 0 }));
    var boundaryInput = Enumerable.Range(0, 1025).Select(index => (byte)(index % 251)).ToArray();
    var oneChunk = Convert.ToHexStringLower(Blake3Hasher.ComputeHash(boundaryInput.AsSpan(0, 1024)));
    var twoChunks = Convert.ToHexStringLower(Blake3Hasher.ComputeHash(boundaryInput));
    Assert(empty == "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262", empty);
    Assert(zero == "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213", zero);
    Assert(oneChunk == "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7", oneChunk);
    Assert(twoChunks == "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444", twoChunks);
});

Run("scan presets expose bounded scanner bundles", () =>
{
    Assert(ScanPresetCatalog.Includes(ScanPreset.Quick, "Big Files"), "quick big files");
    Assert(!ScanPresetCatalog.Includes(ScanPreset.Quick, "Duplicate Files"), "quick excludes duplicate hashing");
    Assert(ScanPresetCatalog.Includes(ScanPreset.Deep, "Duplicate Files"), "deep duplicate files");
    Assert(!ScanPresetCatalog.Includes(ScanPreset.Deep, "WinSxS Analysis"), "deep excludes system audit");
    Assert(ScanPresetCatalog.GetDefinition(ScanPreset.Forensic).ScannerNames.Count == 19, "forensic scanner count");
    Assert(ScanPresetCatalog.Includes(ScanPreset.Forensic, "Third-Party Scanner"), "forensic includes plugins");
});

Run("big-file scan builds a proportional folder tree", () =>
{
    var rootPath = Path.Combine(Path.GetTempPath(), $"scour-treemap-test-{Guid.NewGuid():N}");
    var childPath = Path.Combine(rootPath, "Projects");
    Directory.CreateDirectory(childPath);
    try
    {
        File.WriteAllBytes(Path.Combine(rootPath, "root.bin"), new byte[64]);
        File.WriteAllBytes(Path.Combine(childPath, "project.bin"), new byte[192]);

        var scanner = new BigFileScanner();
        scanner.ScanAsync(
                new ScanConfig { RootPath = rootPath, SkipSystem = false },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.FolderSizeRoot != null);
        Assert(scanner.FolderSizeRoot!.SizeBytes == 256, $"root size was {scanner.FolderSizeRoot.SizeBytes}");
        Assert(scanner.FolderSizeRoot.Children.Any(child => child.Name == "Projects" && child.SizeBytes == 192));

        var rectangles = TreemapLayout.Layout(scanner.FolderSizeRoot, 1000, 500);
        Assert(rectangles.Count >= 2, "expected folder and direct-file rectangles");
        Assert(rectangles.All(rectangle => rectangle.Width >= 0 && rectangle.Height >= 0));
    }
    finally
    {
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
});

Run("CLI emits JSON and supports dry-run and quarantine", () =>
{
    var rootPath = Path.Combine(Path.GetTempPath(), $"scour-cli-test-{Guid.NewGuid():N}");
    var quarantinePath = Path.Combine(Path.GetTempPath(), $"scour-cli-quarantine-{Guid.NewGuid():N}");
    var csvPath = Path.Combine(Path.GetTempPath(), $"scour-cli-report-{Guid.NewGuid():N}.csv");
    Directory.CreateDirectory(rootPath);
    var tempFile = Path.Combine(rootPath, "remove-me.tmp");
    File.WriteAllText(tempFile, "temporary");
    try
    {
        var dryRunOptions = CliParser.Parse(
        [
            "--path", rootPath,
            "--scanner", "Temp Files",
            "--json",
            "--dry-run",
            "--quarantine-to", quarantinePath,
        ]);
        var dryRun = CliRunner.ExecuteAsync(dryRunOptions).GetAwaiter().GetResult();
        Assert(dryRun.ExitCode == 1, "dry-run should report findings");
        Assert(dryRun.Actions.Single().Status == "would-quarantine");
        Assert(File.Exists(tempFile), "dry-run must preserve the source");
        Assert(!Directory.Exists(quarantinePath), "dry-run must not create quarantine storage");
        Assert(CliRunner.ToJson(dryRun).Contains("Temp Files", StringComparison.Ordinal));

        var actualOptions = CliParser.Parse(
        [
            "--path", rootPath,
            "--scanner", "Temp Files",
            "--quarantine-to", quarantinePath,
            "--export-csv", csvPath,
        ]);
        var actual = CliRunner.ExecuteAsync(actualOptions).GetAwaiter().GetResult();
        Assert(actual.Actions.Single().Status == "quarantined");
        Assert(!File.Exists(tempFile), "quarantine should move the source");
        Assert(File.Exists(Path.Combine(quarantinePath, "remove-me.tmp")));
        Assert(File.ReadAllText(csvPath).Contains("remove-me.tmp", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        if (Directory.Exists(quarantinePath)) Directory.Delete(quarantinePath, recursive: true);
        if (File.Exists(csvPath)) File.Delete(csvPath);
    }
});

Run("CLI writes a weekly scheduled-task template", () =>
{
    var rootPath = Path.Combine(Path.GetTempPath(), $"scour-task-root-{Guid.NewGuid():N}");
    var taskPath = Path.Combine(Path.GetTempPath(), $"scour-task-{Guid.NewGuid():N}.xml");
    var reportPath = Path.Combine(Path.GetTempPath(), $"scour-task-reports-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootPath);
    try
    {
        var options = CliParser.Parse(
        [
            "--scheduled-task", taskPath,
            "--path", rootPath,
            "--preset", "Deep",
            "--report-dir", reportPath,
        ]);
        ScheduledTaskTemplate.Write(taskPath, options.RootPath, options.Preset, options.ReportDirectory!);
        var xml = File.ReadAllText(taskPath);
        Assert(xml.Contains("CalendarTrigger", StringComparison.Ordinal));
        Assert(xml.Contains("<Sunday", StringComparison.Ordinal));
        Assert(xml.Contains("--preset Deep", StringComparison.Ordinal));
        Assert(xml.Contains("scour-weekly.json", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
        if (File.Exists(taskPath)) File.Delete(taskPath);
        if (Directory.Exists(reportPath)) Directory.Delete(reportPath, recursive: true);
    }
});

Run("theme palettes render offscreen", () =>
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeManager.Apply(ScourThemeMode.Mocha);
            var window = new Window
            {
                Width = 400,
                Height = 300,
                WindowState = WindowState.Normal,
                Background = (SolidColorBrush)app.Resources["BaseBrush"],
                Content = new Border
                {
                    Background = (SolidColorBrush)app.Resources["MantleBrush"],
                    Child = new TextBlock
                    {
                        Text = "Scour theme preview",
                        Foreground = (SolidColorBrush)app.Resources["TextBrush"],
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            window.Measure(new Size(400, 300));
            window.Arrange(new Rect(0, 0, 400, 300));
            window.UpdateLayout();

            Assert(((SolidColorBrush)app.Resources["BaseBrush"]).Color == Color.FromRgb(0x1E, 0x1E, 0x2E));
            ThemeManager.Apply(ScourThemeMode.Latte);
            Assert(((SolidColorBrush)app.Resources["BaseBrush"]).Color == Color.FromRgb(0xEF, 0xF1, 0xF5));
            ThemeManager.Apply(ScourThemeMode.OLED);
            Assert(((SolidColorBrush)app.Resources["BaseBrush"]).Color == Colors.Black);

            var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            Assert(bitmap.PixelWidth == 400 && bitmap.PixelHeight == 300, "offscreen render dimensions");
            window.Close();
            app.Shutdown();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure != null)
        throw failure;
});

Run("scan progress carries telemetry fields", () =>
{
    var progress = new Scour.Core.Interfaces.ScanProgress("Scanning", 8, 20, false, 4096, 7);
    Assert(progress.BytesProcessed == 4096);
    Assert(progress.FilesProcessed == 7);
});

Run("pinned result store persists pin state", () =>
{
    var pinPath = Path.Combine(Path.GetTempPath(), $"scour-pins-test-{Guid.NewGuid():N}.json");
    try
    {
        var first = new PinnedResultStore(pinPath);
        first.SetPinned("Temp Files", Path.Combine(Path.GetTempPath(), "keep.tmp"), true);
        var second = new PinnedResultStore(pinPath);
        Assert(second.IsPinned("Temp Files", Path.Combine(Path.GetTempPath(), "keep.tmp")));
        second.SetPinned("Temp Files", Path.Combine(Path.GetTempPath(), "keep.tmp"), false);
        Assert(!new PinnedResultStore(pinPath).IsPinned("Temp Files", Path.Combine(Path.GetTempPath(), "keep.tmp")));
    }
    finally
    {
        if (File.Exists(pinPath)) File.Delete(pinPath);
        if (File.Exists(pinPath + ".tmp")) File.Delete(pinPath + ".tmp");
    }
});

Run("finding explanations include scanner rules and safety guidance", () =>
{
    var item = new ScanResultItem
    {
        FullPath = @"C:\Temp\candidate.tmp",
        Name = "candidate.tmp",
        Detail = "extension: .tmp",
        SizeBytes = 12,
        IsSelected = true,
    };
    var explanation = FindingExplanationService.Explain("Temp Files", item);
    Assert(explanation.Rule.Contains("temporary", StringComparison.OrdinalIgnoreCase));
    Assert(explanation.Reason.Contains("extension: .tmp", StringComparison.Ordinal));
    Assert(explanation.Safety.Contains("Selected by default", StringComparison.Ordinal));
});

Run("directory exclusions match exact paths without hiding same-named folders", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"scour-exclusion-test-{Guid.NewGuid():N}");
    var excluded = Path.Combine(root, "skip-me");
    var included = Path.Combine(root, "keep-me");
    Directory.CreateDirectory(excluded);
    Directory.CreateDirectory(included);
    try
    {
        File.WriteAllText(Path.Combine(excluded, "excluded.tmp"), "excluded");
        File.WriteAllText(Path.Combine(included, "included.tmp"), "included");

        var scanner = new TempFileScanner();
        scanner.ScanAsync(
                new Scour.Core.ScanConfig
                {
                    RootPath = root,
                    SkipSystem = false,
                    ExcludedDirectories = [excluded]
                },
                new Progress<Scour.Core.Interfaces.ScanProgress>(),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Count == 1, $"result count was {scanner.Results.Count}");
        Assert(scanner.Results[0].FullPath.EndsWith(Path.Combine("keep-me", "included.tmp"), StringComparison.OrdinalIgnoreCase));
        Assert(DirectoryExclusionMatcher.IsExcluded(["skip-me"], excluded, "skip-me"));
        Assert(!DirectoryExclusionMatcher.IsExcluded([excluded], included, "skip-me"));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("USN delta scope limits recursive scanners to changed paths", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"scour-delta-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var changed = Path.Combine(root, "changed.tmp");
    var unchanged = Path.Combine(root, "unchanged.tmp");
    try
    {
        File.WriteAllText(changed, "changed");
        File.WriteAllText(unchanged, "unchanged");

        var scanner = new TempFileScanner();
        var config = new ScanConfig
        {
            RootPath = root,
            SkipSystem = false,
            ChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { changed }
        };
        scanner.SetScanScope(config);
        scanner.ScanAsync(config, new Progress<Scour.Core.Interfaces.ScanProgress>(), CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert(scanner.Results.Count == 1, $"result count was {scanner.Results.Count}");
        Assert(scanner.Results[0].FullPath.Equals(changed, StringComparison.OrdinalIgnoreCase));
        Assert(CliParser.Parse(["--since-last-run"]).SinceLastRun);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("portable mode detects a marker without touching the registry", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"scour-portable-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        Assert(!AppRuntime.IsPortableInstallation(directory));
        File.WriteAllText(Path.Combine(directory, AppRuntime.PortableMarkerFileName), "");
        Assert(AppRuntime.IsPortableInstallation(directory));
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
});

Run("TUI executes a scan through the CLI engine without keyboard injection", () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"scour-tui-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "tui.tmp"), "tui");
        var options = CliParser.Parse(["--tui", "--path", root, "--scanner", "Temp Files"]);
        using var input = new StringReader("scan\nquit\n");
        using var output = new StringWriter();
        var exitCode = TuiRunner.RunAsync(options, input, output, TextWriter.Null)
            .GetAwaiter()
            .GetResult();

        var text = output.ToString();
        Assert(exitCode == 1, $"TUI exit code was {exitCode}");
        Assert(text.Contains("Scour TUI", StringComparison.Ordinal));
        Assert(text.Contains("tui.tmp", StringComparison.Ordinal));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("plugin manifests validate paths and report malformed entries", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"scour-plugin-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(
            Path.Combine(directory, PluginCatalog.ManifestFileName),
            "{\"manifestVersion\":1,\"id\":\"bad.plugin\",\"name\":\"Bad Plugin\",\"version\":\"1.0.0\",\"assembly\":\"..\\\\outside.dll\"}");
        var discovery = PluginCatalog.Discover(directory);
        Assert(discovery.Modules.Count == 0, $"module count was {discovery.Modules.Count}");
        Assert(discovery.Errors.Count == 1, $"error count was {discovery.Errors.Count}");
        Assert(discovery.Errors[0].Contains("inside the plugin directory", StringComparison.Ordinal), discovery.Errors[0]);
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
});

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine($"FAIL: {failure}");
    return 1;
}

Console.WriteLine("Scour lightweight tests passed.");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
    }
}

void Assert(bool condition, string? message = null)
{
    if (!condition)
        throw new InvalidOperationException(message ?? "Assertion failed");
}

void WritePng(string path, byte adjustment)
{
    var pixels = new byte[32 * 24 * 4];
    for (var index = 0; index < pixels.Length; index += 4)
    {
        var pixel = index / 4;
        var x = pixel % 32;
        var y = pixel / 32;
        var gradient = adjustment == 200
            ? 255 - (x * 7)
            : Math.Min(255, adjustment + (x * 4) + (y * 2));
        pixels[index] = (byte)gradient;
        pixels[index + 1] = (byte)Math.Min(255, gradient + 20);
        pixels[index + 2] = (byte)Math.Min(255, gradient + 40);
        pixels[index + 3] = 255;
    }

    var bitmap = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
    bitmap.Freeze();
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var output = File.Create(path);
    encoder.Save(output);
}
