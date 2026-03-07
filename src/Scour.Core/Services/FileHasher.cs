using System.Security.Cryptography;

namespace Scour.Core.Services;

/// <summary>
/// High-performance file hashing with partial-hash optimization.
/// For duplicate detection: hash first 4KB, only full-hash on matches.
/// </summary>
public static class FileHasher
{
    private const int PartialHashSize = 4096;

    /// <summary>
    /// Compute SHA256 of the first 4KB of a file (fast pre-filter for duplicates).
    /// </summary>
    public static async Task<byte[]> ComputePartialHashAsync(string path, CancellationToken ct = default)
    {
        var buffer = new byte[PartialHashSize];
        int bytesRead;

        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PartialHashSize, true))
        {
            bytesRead = await fs.ReadAsync(buffer.AsMemory(0, PartialHashSize), ct);
        }

        return SHA256.HashData(buffer.AsSpan(0, bytesRead));
    }

    /// <summary>
    /// Compute full SHA256 hash of a file. Uses buffered async reads.
    /// </summary>
    public static async Task<byte[]> ComputeFullHashAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return await SHA256.HashDataAsync(fs, ct);
    }

    public static string HashToHex(byte[] hash) => Convert.ToHexStringLower(hash);
}
