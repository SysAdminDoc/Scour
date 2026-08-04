using System.Text;
using Scour.Core.Interfaces;
using Scour.Core.Native;

namespace Scour.Core.Services;

/// <summary>
/// Persists an MFT index and refreshes it from the USN journal when the
/// journal checkpoint is still valid. The cache is intentionally one file so
/// it can be invalidated atomically after a journal rollover or corruption.
/// </summary>
public sealed class MftCacheStore
{
    private const int CacheFormatVersion = 1;
    private const int MaxEntryCount = 5_000_000;
    private const string CacheMagic = "SCOUR-MFT";

    public MftCacheStore(string? cachePath = null)
    {
        CachePath = cachePath ?? GetDefaultCachePath();
    }

    public string CachePath { get; }

    public void SaveSnapshot(MftReader.MftSnapshot snapshot)
        => Save(snapshot);

    public MftReader.MftSnapshot? LoadSnapshot()
        => TryLoad();

    public static string GetDefaultCachePath()
        => Path.Combine(AppRuntime.DataDirectory, "mft-cache.bin");

    public Task<MftCacheRefreshResult> RefreshAsync(
        char driveLetter,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
        => Task.Run(() => Refresh(driveLetter, progress, ct), ct);

    public static byte[] Serialize(MftReader.MftSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(CacheMagic);
        writer.Write(CacheFormatVersion);
        writer.Write(snapshot.DriveLetter);
        writer.Write(snapshot.JournalId);
        writer.Write(snapshot.NextUsn);
        writer.Write(snapshot.Entries.Count);

        foreach (var pair in snapshot.Entries.OrderBy(pair => pair.Key))
        {
            var entry = pair.Value;
            writer.Write(pair.Key);
            writer.Write(entry.ParentFileReferenceNumber);
            writer.Write(entry.IsDirectory);
            writer.Write(entry.FileAttributes);
            writer.Write(entry.FileName ?? string.Empty);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static MftReader.MftSnapshot? Deserialize(ReadOnlySpan<byte> data)
    {
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!string.Equals(reader.ReadString(), CacheMagic, StringComparison.Ordinal))
                return null;
            if (reader.ReadInt32() != CacheFormatVersion)
                return null;

            var driveLetter = reader.ReadChar();
            var journalId = reader.ReadInt64();
            var nextUsn = reader.ReadInt64();
            var count = reader.ReadInt32();
            if (count < 0 || count > MaxEntryCount)
                return null;

            var entries = new Dictionary<long, MftReader.MftEntry>(count);
            for (var index = 0; index < count; index++)
            {
                var fileReference = reader.ReadInt64();
                var entry = new MftReader.MftEntry
                {
                    FileReferenceNumber = fileReference,
                    ParentFileReferenceNumber = reader.ReadInt64(),
                    IsDirectory = reader.ReadBoolean(),
                    FileAttributes = reader.ReadInt32(),
                    FileName = reader.ReadString(),
                };
                entries[fileReference] = entry;
            }

            return new MftReader.MftSnapshot(driveLetter, journalId, nextUsn, entries);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private MftCacheRefreshResult Refresh(
        char driveLetter,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            var journal = MftReader.QueryJournal(driveLetter, progress);
            if (journal == null)
                return Failure("USN journal unavailable");

            var cached = TryLoad();
            MftReader.MftSnapshot? snapshot = null;
            var strategy = "full MFT enumeration";
            var usedDelta = false;
            IReadOnlyList<string>? changedPaths = null;

            if (cached != null &&
                cached.DriveLetter == char.ToUpperInvariant(driveLetter) &&
                cached.JournalId == journal.Value.JournalId &&
                cached.NextUsn >= journal.Value.FirstUsn &&
                cached.NextUsn <= journal.Value.NextUsn)
            {
                var changes = MftReader.ReadUsnDelta(
                    driveLetter,
                    cached.JournalId,
                    cached.NextUsn,
                    journal.Value.NextUsn,
                    progress,
                    ct);
                if (changes != null)
                {
                    var entries = new Dictionary<long, MftReader.MftEntry>(cached.Entries);
                    MftReader.ApplyUsnChanges(entries, changes);
                    changedPaths = ResolveChangedPaths(changes, cached.Entries, entries, driveLetter);
                    snapshot = new MftReader.MftSnapshot(
                        char.ToUpperInvariant(driveLetter),
                        journal.Value.JournalId,
                        journal.Value.NextUsn,
                        entries);
                    strategy = "USN journal delta";
                    usedDelta = true;
                }
            }

            if (snapshot == null)
            {
                snapshot = MftReader.EnumerateMftSnapshot(driveLetter, progress, ct);
                if (snapshot == null)
                    return Failure("MFT enumeration unavailable");
            }

            Save(snapshot);
            return new MftCacheRefreshResult(
                usedDelta,
                snapshot.Entries.Count,
                strategy,
                CachePath,
                null,
                changedPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    private MftReader.MftSnapshot? TryLoad()
    {
        try
        {
            return File.Exists(CachePath)
                ? Deserialize(File.ReadAllBytes(CachePath))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(MftReader.MftSnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(CachePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("MFT cache path has no parent directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{CachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, Serialize(snapshot));
            if (File.Exists(CachePath))
                File.Replace(temporaryPath, CachePath, null);
            else
                File.Move(temporaryPath, CachePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private MftCacheRefreshResult Failure(string message)
        => new(false, 0, "unavailable", CachePath, message);

    private static IReadOnlyList<string> ResolveChangedPaths(
        IReadOnlyList<MftReader.UsnChange> changes,
        Dictionary<long, MftReader.MftEntry> previousEntries,
        Dictionary<long, MftReader.MftEntry> currentEntries,
        char driveLetter)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            AddPathAndParent(paths, MftReader.ResolvePath(change.FileReferenceNumber, previousEntries, driveLetter));
            AddPathAndParent(paths, MftReader.ResolvePath(change.FileReferenceNumber, currentEntries, driveLetter));
        }

        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddPathAndParent(HashSet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        paths.Add(path);
        var parent = Path.GetDirectoryName(path);
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(parent) &&
            !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            paths.Add(parent);
    }
}

public sealed record MftCacheRefreshResult(
    bool UsedDelta,
    int EntryCount,
    string Strategy,
    string CachePath,
    string? Error,
    IReadOnlyList<string>? ChangedPaths = null);
