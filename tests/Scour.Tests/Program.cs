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
