using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Scour.Core.Services;

/// <summary>
/// Portable, dependency-free BLAKE3 hash implementation for the optional
/// duplicate-file full-hash backend. It intentionally favors a small,
/// reviewable scalar implementation over platform-specific SIMD code.
/// </summary>
public static class Blake3Hasher
{
    private const int BlockLength = 64;
    private const int ChunkLength = 1024;
    private const uint ChunkStart = 1;
    private const uint ChunkEnd = 2;
    private const uint Parent = 4;
    private const uint Root = 8;

    private static readonly uint[] Iv =
    [
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    ];

    private static readonly byte[] MessagePermutation =
    [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];

    public static byte[] ComputeHash(ReadOnlySpan<byte> input)
    {
        using var stream = new MemoryStream(input.ToArray(), writable: false);
        return ComputeHashAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task<byte[]> ComputeHashAsync(Stream input, CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkLength);
        var stack = new List<uint[]>();
        Blake3Output? lastOutput = null;
        ulong chunkIndex = 0;

        try
        {
            while (true)
            {
                var bytesRead = 0;
                while (bytesRead < ChunkLength)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(bytesRead, ChunkLength - bytesRead), ct);
                    if (read == 0) break;
                    bytesRead += read;
                }

                if (bytesRead == 0) break;

                if (lastOutput != null)
                    AddChunkChainingValue(stack, lastOutput.ChainingValue(), chunkIndex - 1);

                lastOutput = CompressChunk(buffer.AsSpan(0, bytesRead), chunkIndex);
                chunkIndex++;
            }

            lastOutput ??= CompressChunk(ReadOnlySpan<byte>.Empty, 0);

            var rootOutput = lastOutput;
            while (stack.Count > 0)
            {
                var left = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                rootOutput = ParentOutput(left, rootOutput.ChainingValue());
            }

            return rootOutput.RootBytes(32);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AddChunkChainingValue(List<uint[]> stack, uint[] chainingValue, ulong chunkIndex)
    {
        var totalChunks = chunkIndex + 1;
        var current = chainingValue;
        while ((totalChunks & 1) == 0)
        {
            var left = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            current = ParentOutput(left, current).ChainingValue();
            totalChunks >>= 1;
        }

        stack.Add(current);
    }

    private static Blake3Output CompressChunk(ReadOnlySpan<byte> input, ulong chunkIndex)
    {
        var blockCount = Math.Max(1, (input.Length + BlockLength - 1) / BlockLength);
        var chainingValue = (uint[])Iv.Clone();
        Blake3Output? output = null;

        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
        {
            var offset = blockIndex * BlockLength;
            var blockLength = Math.Min(BlockLength, Math.Max(0, input.Length - offset));
            var flags = (blockIndex == 0 ? ChunkStart : 0u)
                | (blockIndex == blockCount - 1 ? ChunkEnd : 0u);
            output = new Blake3Output(
                chainingValue,
                WordsFromBlock(input.Slice(offset, blockLength)),
                chunkIndex,
                (uint)blockLength,
                flags);

            if (blockIndex < blockCount - 1)
                chainingValue = output.ChainingValue();
        }

        return output!;
    }

    private static Blake3Output ParentOutput(uint[] left, uint[] right)
    {
        var blockWords = new uint[16];
        Array.Copy(left, 0, blockWords, 0, 8);
        Array.Copy(right, 0, blockWords, 8, 8);
        return new Blake3Output((uint[])Iv.Clone(), blockWords, 0, BlockLength, Parent);
    }

    private static uint[] WordsFromBlock(ReadOnlySpan<byte> block)
    {
        Span<byte> padded = stackalloc byte[BlockLength];
        block.CopyTo(padded);
        var words = new uint[16];
        for (var index = 0; index < words.Length; index++)
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(padded[(index * 4)..]);
        return words;
    }

    private static uint[] Compress(uint[] chainingValue, uint[] blockWords, ulong counter, uint blockLength, uint flags)
    {
        var state = new uint[16];
        Array.Copy(chainingValue, state, 8);
        Array.Copy(Iv, 0, state, 8, 4);
        state[12] = (uint)counter;
        state[13] = (uint)(counter >> 32);
        state[14] = blockLength;
        state[15] = flags;

        var message = (uint[])blockWords.Clone();
        for (var round = 0; round < 7; round++)
        {
            Round(state, message);
            Permute(message);
        }

        var output = new uint[16];
        for (var index = 0; index < 8; index++)
        {
            output[index] = state[index] ^ state[index + 8];
            output[index + 8] = state[index + 8] ^ chainingValue[index];
        }

        return output;
    }

    private static void Round(uint[] state, uint[] message)
    {
        G(state, 0, 4, 8, 12, message[0], message[1]);
        G(state, 1, 5, 9, 13, message[2], message[3]);
        G(state, 2, 6, 10, 14, message[4], message[5]);
        G(state, 3, 7, 11, 15, message[6], message[7]);
        G(state, 0, 5, 10, 15, message[8], message[9]);
        G(state, 1, 6, 11, 12, message[10], message[11]);
        G(state, 2, 7, 8, 13, message[12], message[13]);
        G(state, 3, 4, 9, 14, message[14], message[15]);
    }

    private static void Permute(uint[] message)
    {
        var permutation = new uint[16];
        for (var index = 0; index < permutation.Length; index++)
            permutation[index] = message[MessagePermutation[index]];
        Array.Copy(permutation, message, permutation.Length);
    }

    private static void G(uint[] state, int a, int b, int c, int d, uint mx, uint my)
    {
        unchecked
        {
            state[a] = state[a] + state[b] + mx;
            state[d] = BitOperations.RotateRight(state[d] ^ state[a], 16);
            state[c] += state[d];
            state[b] = BitOperations.RotateRight(state[b] ^ state[c], 12);
            state[a] = state[a] + state[b] + my;
            state[d] = BitOperations.RotateRight(state[d] ^ state[a], 8);
            state[c] += state[d];
            state[b] = BitOperations.RotateRight(state[b] ^ state[c], 7);
        }
    }

    private sealed class Blake3Output(
        uint[] chainingValue,
        uint[] blockWords,
        ulong counter,
        uint blockLength,
        uint flags)
    {
        public uint[] ChainingValue()
            => Compress(chainingValue, blockWords, counter, blockLength, flags)[..8];

        public byte[] RootBytes(int length)
        {
            var output = new byte[length];
            var block = new byte[BlockLength];
            var written = 0;
            ulong outputBlockCounter = 0;
            while (written < output.Length)
            {
                var words = Compress(chainingValue, blockWords, outputBlockCounter, blockLength, flags | Root);
                for (var index = 0; index < words.Length; index++)
                    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(index * 4, 4), words[index]);

                var copyLength = Math.Min(block.Length, output.Length - written);
                block.AsSpan(0, copyLength).CopyTo(output.AsSpan(written, copyLength));
                written += copyLength;
                outputBlockCounter++;
            }

            return output;
        }
    }
}
