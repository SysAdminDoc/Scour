using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scour.Scanners;

/// <summary>
/// A bounded fingerprint for visually or structurally similar media.
/// </summary>
public sealed record MediaFingerprintInfo(
    string Kind,
    int Width,
    int Height,
    ulong PerceptualBucket)
{
    public string Dimensions => Width > 0 && Height > 0
        ? $"{Width} x {Height}"
        : "Unknown dimensions";
}

/// <summary>
/// Reads media metadata and computes a compact dHash-style similarity bucket
/// without loading a complete video or image into memory.
/// </summary>
public static class MediaFingerprint
{
    private const int ImageHashWidth = 9;
    private const int ImageHashHeight = 8;
    private const int VideoSampleWidth = 64;
    private const int VideoSampleBytesPerPoint = 64;
    private const int MetadataSampleBytes = 16 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".heif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".webm", ".wmv"
    };

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return ImageExtensions.Contains(extension) || VideoExtensions.Contains(extension);
    }

    public static MediaFingerprintInfo? Compute(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension))
            return ComputeImage(path);

        if (VideoExtensions.Contains(extension))
            return ComputeVideo(path, extension);

        return null;
    }

    public static int HammingDistance(ulong left, ulong right)
        => System.Numerics.BitOperations.PopCount(left ^ right);

    /// <summary>
    /// Builds the 64-bit dHash from 8 rows of 9 grayscale samples.
    /// Exposed for the lightweight test harness and deterministic callers.
    /// </summary>
    public static ulong ComputeDHash(ReadOnlySpan<byte> grayscaleSamples)
    {
        if (grayscaleSamples.Length != ImageHashWidth * ImageHashHeight)
            throw new ArgumentException("A dHash requires exactly 72 grayscale samples.", nameof(grayscaleSamples));

        ulong hash = 0;
        for (var row = 0; row < ImageHashHeight; row++)
        {
            var offset = row * ImageHashWidth;
            for (var column = 0; column < ImageHashWidth - 1; column++)
            {
                hash <<= 1;
                if (grayscaleSamples[offset + column] > grayscaleSamples[offset + column + 1])
                    hash |= 1;
            }
        }

        return hash;
    }

    private static MediaFingerprintInfo? ComputeImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var frame = decoder.Frames.FirstOrDefault();
            if (frame == null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
                return null;

            var hash = ComputeImageDHash(frame);
            return new MediaFingerprintInfo("Image", frame.PixelWidth, frame.PixelHeight, hash);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is NotSupportedException ||
            ex is InvalidOperationException ||
            ex is ArgumentException)
        {
            // Some Windows installations do not have a WIC codec for every
            // extension (notably HEIC/AVIF). Keep those files useful by using
            // a bounded byte bucket and any dimensions available from headers.
            var dimensions = TryReadImageDimensions(path);
            return new MediaFingerprintInfo(
                "Image",
                dimensions.Width,
                dimensions.Height,
                ComputeSampleBucket(path));
        }
    }

    private static MediaFingerprintInfo ComputeVideo(string path, string extension)
    {
        var dimensions = TryReadVideoDimensions(path, extension);
        return new MediaFingerprintInfo(
            "Video",
            dimensions.Width,
            dimensions.Height,
            ComputeSampleBucket(path));
    }

    private static ulong ComputeImageDHash(BitmapSource source)
    {
        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        gray.Freeze();

        var scale = new TransformedBitmap(
            gray,
            new ScaleTransform(
                ImageHashWidth / (double)gray.PixelWidth,
                ImageHashHeight / (double)gray.PixelHeight));
        scale.Freeze();

        var pixels = new byte[ImageHashWidth * ImageHashHeight];
        scale.CopyPixels(pixels, ImageHashWidth, 0);
        return ComputeDHash(pixels);
    }

    private static ulong ComputeSampleBucket(string path)
    {
        var samples = new byte[VideoSampleWidth];
        var total = 0L;
        var count = 0;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);

            var length = stream.Length;
            if (length <= 0) return 0;

            var buffer = new byte[VideoSampleBytesPerPoint];
            for (var index = 0; index < VideoSampleWidth; index++)
            {
                var offset = length <= buffer.Length
                    ? 0
                    : (length - buffer.Length) * index / (VideoSampleWidth - 1);

                stream.Seek(offset, SeekOrigin.Begin);
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) continue;

                var sum = 0L;
                for (var byteIndex = 0; byteIndex < read; byteIndex++)
                    sum += buffer[byteIndex];

                samples[index] = (byte)(sum / read);
                total += samples[index];
                count++;
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        if (count == 0) return 0;

        var average = total / (double)count;
        ulong bucket = 0;
        foreach (var sample in samples)
        {
            bucket <<= 1;
            if (sample >= average)
                bucket |= 1;
        }

        return bucket;
    }

    private static (int Width, int Height) TryReadImageDimensions(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[32];
            var read = stream.Read(header);
            if (read >= 24 && header[..8].SequenceEqual("\x89PNG\r\n\x1A\n"u8))
                return ((int)BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
                    (int)BinaryPrimitives.ReadInt32BigEndian(header[20..24]));

            if (read >= 10 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
                return (BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]),
                    BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]));

            if (read >= 26 && header[..2].SequenceEqual("BM"u8))
                return ((int)BinaryPrimitives.ReadUInt32LittleEndian(header[18..22]),
                    Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header[22..26])));

            return (0, 0);
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static (int Width, int Height) TryReadVideoDimensions(string path, string extension)
    {
        try
        {
            return extension.ToLowerInvariant() switch
            {
                ".avi" => ReadAviDimensions(path),
                ".m4v" or ".mov" or ".mp4" => ReadMp4Dimensions(path),
                ".mkv" or ".webm" => ReadMatroskaDimensions(path),
                _ => (0, 0),
            };
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static (int Width, int Height) ReadAviDimensions(string path)
    {
        var buffer = ReadSample(path, MetadataSampleBytes, fromEnd: false);
        var marker = FindMarker(buffer, "avih"u8);
        if (marker < 0 || marker + 48 > buffer.Length)
            return (0, 0);

        return (BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(marker + 40, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(marker + 44, 4)));
    }

    private static (int Width, int Height) ReadMp4Dimensions(string path)
    {
        var buffer = ReadSample(path, MetadataSampleBytes, fromEnd: false);
        var dimensions = FindMp4TkhdDimensions(buffer);
        if (dimensions.Width > 0 && dimensions.Height > 0)
            return dimensions;

        buffer = ReadSample(path, MetadataSampleBytes, fromEnd: true);
        return FindMp4TkhdDimensions(buffer);
    }

    private static (int Width, int Height) FindMp4TkhdDimensions(byte[] buffer)
    {
        var marker = FindMarker(buffer, "tkhd"u8);
        while (marker >= 0)
        {
            var atomStart = marker - 4;
            if (atomStart >= 0 && atomStart + 84 <= buffer.Length)
            {
                var version = buffer[marker + 4];
                var widthOffset = version == 1 ? atomStart + 84 : atomStart + 72;
                var heightOffset = widthOffset + 4;
                if (heightOffset + 4 <= buffer.Length)
                {
                    var width = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(widthOffset, 4)) >> 16;
                    var height = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(heightOffset, 4)) >> 16;
                    if (width > 0 && height > 0)
                        return (width, height);
                }
            }

            marker = FindMarker(buffer, "tkhd"u8, marker + 4);
        }

        return (0, 0);
    }

    private static (int Width, int Height) ReadMatroskaDimensions(string path)
    {
        var buffer = ReadSample(path, MetadataSampleBytes, fromEnd: false);
        var width = ReadEbmlIntegerAfter(buffer, 0xB0);
        var height = ReadEbmlIntegerAfter(buffer, 0xBA);
        return (width, height);
    }

    private static int ReadEbmlIntegerAfter(byte[] buffer, byte elementId)
    {
        for (var index = 0; index + 2 < buffer.Length; index++)
        {
            if (buffer[index] != elementId) continue;

            var sizeByte = buffer[index + 1];
            var sizeLength = LeadingZeroCount(sizeByte) + 1;
            if (sizeLength > 4 || index + sizeLength >= buffer.Length) continue;

            var size = sizeByte & ((1 << (8 - sizeLength)) - 1);
            for (var sizeIndex = 1; sizeIndex < sizeLength; sizeIndex++)
                size = (size << 8) | buffer[index + 1 + sizeIndex];

            if (size is <= 0 or > 4 || index + sizeLength + size > buffer.Length) continue;

            var value = 0;
            for (var valueIndex = 0; valueIndex < size; valueIndex++)
                value = (value << 8) | buffer[index + sizeLength + valueIndex];

            if (value > 0) return value;
        }

        return 0;
    }

    private static int LeadingZeroCount(byte value)
    {
        var count = 0;
        for (var bit = 0x80; bit > 0 && (value & bit) == 0; bit >>= 1)
            count++;
        return count;
    }

    private static byte[] ReadSample(string path, int maxBytes, bool fromEnd)
    {
        using var stream = File.OpenRead(path);
        var count = (int)Math.Min(stream.Length, maxBytes);
        if (fromEnd && stream.Length > count)
            stream.Seek(-count, SeekOrigin.End);

        var buffer = new byte[count];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0) break;
            offset += read;
        }

        return offset == buffer.Length ? buffer : buffer[..offset];
    }

    private static int FindMarker(byte[] buffer, ReadOnlySpan<byte> marker, int start = 0)
    {
        for (var index = Math.Max(0, start); index <= buffer.Length - marker.Length; index++)
        {
            if (buffer.AsSpan(index, marker.Length).SequenceEqual(marker))
                return index;
        }

        return -1;
    }

}
