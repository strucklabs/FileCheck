using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

internal sealed record DuplicateMember(FileEntry File, WindowsFileStamp? Stamp);
internal sealed record DuplicateGroup(IReadOnlyList<DuplicateMember> Files, long ReclaimableBytes);
internal sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<ScanIssue> Issues,
    int Candidates, int Reused, bool Paused);
internal sealed record DuplicateProgress(string Phase, string Path, int Processed, int Total,
    long Position = 0, long Length = 0);
internal sealed record CachedHash(string Path, WindowsFileStamp Stamp, string Hash);

[JsonSerializable(typeof(CachedHash[]))]
internal partial class DuplicateJson : JsonSerializerContext;

internal sealed class DuplicateSearch(string cachePath)
{
    private const int BlockSize = 1024 * 1024;

    public static FileEntry[] FindCandidates(IEnumerable<FileEntry> files) => files
        .DistinctBy(file => file.Path, InventoryPaths.Comparer).GroupBy(file => file.Bytes)
        .Where(group => group.Skip(1).Any()).SelectMany(group => group).ToArray();

    public DuplicateResult Run(FileEntry[] candidates, bool force, IProgress<DuplicateProgress>? progress,
        CancellationToken cancellation)
    {
        var issues = new List<ScanIssue>();
        var groups = new List<DuplicateGroup>();
        var cache = LoadCache(issues);
        var hashes = new Dictionary<(long Bytes, string Hash), List<DuplicateMember>>();
        var interval = Stopwatch.StartNew();
        var checkpoint = Stopwatch.StartNew();
        var buffer = new byte[BlockSize];
        int processed = 0;
        int reused = 0;
        bool paused = false;
        bool dirty = false;
        if (force)
        {
            foreach (FileEntry file in candidates)
                dirty |= cache.Remove(file.Path);
        }
        try
        {
            foreach (FileEntry file in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Report(new DuplicateProgress("SHA-256", file.Path, processed, candidates.Length));
                try
                {
                    using FileStream stream = Open(file, out WindowsFileStamp? stamp);
                    string hash;
                    if (!force && stamp?.CanCache == true && cache.TryGetValue(file.Path, out CachedHash? cached)
                        && cached.Stamp is { } cachedStamp && (cachedStamp with { Attributes = stamp.Attributes }) == stamp)
                    {
                        hash = cached.Hash;
                        reused++;
                    }
                    else
                    {
                        using IncrementalHash algorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        while (true)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int count = stream.Read(buffer);
                            if (count == 0)
                                break;
                            algorithm.AppendData(buffer.AsSpan(0, count));
                            if (interval.ElapsedMilliseconds >= 180)
                            {
                                progress?.Report(new DuplicateProgress("SHA-256", file.Path, processed,
                                    candidates.Length, stream.Position, stream.Length));
                                interval.Restart();
                            }
                        }
                        hash = Convert.ToHexString(algorithm.GetHashAndReset());
                        if (stamp?.CanCache == true && stamp == WindowsFileStamp.Read(stream))
                        {
                            cache[file.Path] = new CachedHash(file.Path, stamp, hash);
                            dirty = true;
                        }
                    }
                    if (stamp != WindowsFileStamp.Read(stream))
                        throw new IOException("Datei hat sich während der Prüfung geändert. Inventar erneut scannen.");

                    var key = (file.Bytes, hash);
                    if (!hashes.TryGetValue(key, out List<DuplicateMember>? matches))
                        hashes[key] = matches = [];
                    matches.Add(new DuplicateMember(file, stamp));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(file.Path, error.Message));
                }
                processed++;
                if (dirty && checkpoint.Elapsed.TotalSeconds >= 4)
                {
                    dirty = !SaveCache(cache, issues);
                    checkpoint.Restart();
                }
            }

            var matchedHashes = hashes.Values.Where(bucket => bucket.Count > 1).ToArray();
            var secondBuffer = new byte[BlockSize];
            for (int index = 0; index < matchedHashes.Length; index++)
            {
                var confirmed = new List<List<DuplicateMember>>();
                foreach (DuplicateMember member in matchedHashes[index])
                {
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report(new DuplicateProgress("Bytevergleich", member.File.Path, index, matchedHashes.Length));
                    try
                    {
                        using FileStream current = OpenChecked(member);
                        bool added = false;
                        foreach (List<DuplicateMember> group in confirmed)
                        {
                            using FileStream reference = OpenChecked(group[0]);
                            current.Position = 0;
                            if (EqualContents(reference, current, buffer, secondBuffer, cancellation, position =>
                            {
                                if (interval.ElapsedMilliseconds >= 180)
                                {
                                    progress?.Report(new DuplicateProgress("Bytevergleich", member.File.Path,
                                        index, matchedHashes.Length, position, current.Length));
                                    interval.Restart();
                                }
                            }))
                            {
                                group.Add(member);
                                added = true;
                                break;
                            }
                        }
                        if (!added)
                            confirmed.Add([member]);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new ScanIssue(member.File.Path, error.Message));
                    }
                }

                foreach (List<DuplicateMember> group in confirmed.Where(group => group.Count > 1))
                {
                    var verified = new List<DuplicateMember>();
                    foreach (DuplicateMember member in group)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        try
                        {
                            using FileStream stream = OpenChecked(member);
                            verified.Add(member);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            issues.Add(new ScanIssue(member.File.Path, error.Message));
                        }
                    }
                    if (verified.Count > 1)
                        groups.Add(new DuplicateGroup(verified, Reclaimable(verified)));
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            paused = true;
        }
        finally
        {
            if (dirty)
                SaveCache(cache, issues);
        }

        return new DuplicateResult(groups.OrderByDescending(group => group.ReclaimableBytes)
            .ThenByDescending(group => group.Files[0].File.Bytes).ToArray(), issues, candidates.Length, reused, paused);
    }

    private Dictionary<string, CachedHash> LoadCache(List<ScanIssue> issues)
    {
        var cache = new Dictionary<string, CachedHash>(InventoryPaths.Comparer);
        try
        {
            foreach (CachedHash entry in LocalJsonFile.Load(cachePath, DuplicateJson.Default.CachedHashArray) ?? [])
                cache[entry.Path] = entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            issues.Add(new ScanIssue(cachePath, $"Prüfwertspeicher konnte nicht geladen werden. {error.Message}"));
        }
        return cache;
    }

    private bool SaveCache(Dictionary<string, CachedHash> cache, List<ScanIssue> issues)
    {
        try
        {
            LocalJsonFile.Save(cachePath, cache.Values.ToArray(), DuplicateJson.Default.CachedHashArray);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            issues.Add(new ScanIssue(cachePath, $"Prüfwertspeicher konnte nicht gespeichert werden. {error.Message}"));
            return false;
        }
    }

    private static FileStream Open(FileEntry file, out WindowsFileStamp? stamp)
    {
        if (File.GetAttributes(file.Path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Verknüpfte Datei wurde übersprungen.");
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.SequentialScan);
        try
        {
            stamp = WindowsFileStamp.Read(stream);
            long written = stamp?.Written ?? File.GetLastWriteTimeUtc(stream.SafeFileHandle).ToFileTimeUtc();
            long created = stamp?.Created ?? File.GetCreationTimeUtc(stream.SafeFileHandle).ToFileTimeUtc();
            if (stream.Length != file.Bytes || written != file.ModifiedUtc.ToFileTimeUtc() || created != file.CreatedUtc.ToFileTimeUtc())
                throw new IOException($"Die Datei {file.Name} hat sich seit dem Inventarscan geändert.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenChecked(DuplicateMember member)
    {
        FileStream stream = Open(member.File, out WindowsFileStamp? stamp);
        if (stamp == member.Stamp)
            return stream;
        stream.Dispose();
        throw new IOException($"Die Datei {member.File.Name} hat sich während der Prüfung geändert.");
    }

    private static bool EqualContents(Stream left, Stream right, byte[] first, byte[] second,
        CancellationToken cancellation, Action<long> progress)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = left.ReadAtLeast(first, first.Length, throwOnEndOfStream: false);
            int other = right.ReadAtLeast(second, second.Length, throwOnEndOfStream: false);
            if (count != other || !first.AsSpan(0, count).SequenceEqual(second.AsSpan(0, other)))
                return false;
            if (count < first.Length)
                return true;
            progress(right.Position);
        }
    }

    private static long Reclaimable(IReadOnlyList<DuplicateMember> members)
    {
        if (members.Any(member => member.Stamp is null || member.Stamp.Links == 0))
            return 0;
        var identities = members.GroupBy(member => member.Stamp!.Identity).ToArray();
        int removable = identities.Count(group => group.Count() >= group.First().Stamp!.Links);
        int toKeep = removable == identities.Length ? 1 : 0;
        return Math.Max(0, removable - toKeep) * members[0].File.Bytes;
    }
}
