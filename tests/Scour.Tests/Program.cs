using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scour.Scanners;

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
