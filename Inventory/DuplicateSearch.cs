using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

internal sealed record DuplicateMember(FileEntry File, WindowsFileStamp? Stamp);
internal sealed record DuplicateGroup(IReadOnlyList<DuplicateMember> Files, long ReclaimableBytes);
internal sealed record DuplicateResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<ScanIssue> Issues,
    int Candidates, int Reused, bool Paused, int ReusedComparisons, long BytesRead);
internal sealed record DuplicateProgress(string Phase, string Path, int Processed, int Total,
    long BytesRead, double BytesPerSecond, long Position = 0, long Length = 0);
internal sealed record CachedHash(string Path, WindowsFileStamp Stamp, string? Hash,
    string? Sample = null, WindowsFileStamp? VerifiedAgainst = null);

[JsonSerializable(typeof(CachedHash[]))]
internal partial class DuplicateJson : JsonSerializerContext;

internal sealed class DuplicateSearch(string cachePath)
{
    private const int BlockSize = 1024 * 1024;
    private const int SampleBlockSize = 64 * 1024;
    private const int SamplingThreshold = 16 * 1024 * 1024;

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
        var elapsed = Stopwatch.StartNew();
        var interval = Stopwatch.StartNew();
        var checkpoint = Stopwatch.StartNew();
        var buffer = new byte[BlockSize];
        int reused = 0;
        int reusedComparisons = 0;
        long bytesRead = 0;
        bool paused = false;
        bool dirty = false;
        if (force)
        {
            foreach (FileEntry file in candidates)
                dirty |= cache.Remove(file.Path);
        }
        try
        {
            List<DuplicateMember> hashCandidates = SelectHashCandidates();
            for (int index = 0; index < hashCandidates.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                DuplicateMember candidate = hashCandidates[index];
                FileEntry file = candidate.File;
                Report("SHA-256", file.Path, index, hashCandidates.Count);
                try
                {
                    using FileStream stream = Open(file, out WindowsFileStamp? stamp);
                    if (candidate.Stamp is not null && candidate.Stamp != stamp)
                        throw new IOException("Datei hat sich während der Prüfung geändert. Inventar erneut scannen.");
                    CachedHash? cached = GetCached(file.Path, stamp);
                    string hash;
                    if (cached?.Hash is string savedHash)
                    {
                        hash = savedHash;
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
                            bytesRead += count;
                            algorithm.AppendData(buffer.AsSpan(0, count));
                            if (interval.ElapsedMilliseconds >= 180)
                            {
                                Report("SHA-256", file.Path, index, hashCandidates.Count, stream.Position, file.Bytes);
                            }
                        }
                        hash = Convert.ToHexString(algorithm.GetHashAndReset());
                        if (stamp?.CanCache == true && stamp == WindowsFileStamp.Read(stream))
                        {
                            cache[file.Path] = new CachedHash(file.Path, stamp, hash, cached?.Sample);
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
                SaveCheckpoint();
            }

            var matchedHashes = hashes.Values.Where(bucket => bucket.Count > 1).ToArray();
            var secondBuffer = new byte[BlockSize];
            for (int index = 0; index < matchedHashes.Length; index++)
            {
                var confirmed = new List<List<DuplicateMember>>();
                foreach (DuplicateMember member in matchedHashes[index].OrderBy(member => member.File.Path, InventoryPaths.Comparer))
                {
                    cancellation.ThrowIfCancellationRequested();
                    Report("Bytevergleich", member.File.Path, index, matchedHashes.Length);
                    try
                    {
                        using FileStream current = OpenChecked(member);
                        bool added = false;
                        foreach (List<DuplicateMember> group in confirmed)
                        {
                            DuplicateMember firstMember = group[0];
                            using FileStream reference = OpenChecked(firstMember);
                            CachedHash? cached = GetCached(member.File.Path, member.Stamp);
                            if (cached?.VerifiedAgainst is WindowsFileStamp verifiedStamp && verifiedStamp == firstMember.Stamp)
                            {
                                reusedComparisons++;
                                group.Add(member);
                                added = true;
                                break;
                            }
                            current.Position = 0;
                            if (EqualContents(reference, current, buffer, secondBuffer, cancellation, count =>
                            {
                                bytesRead += count;
                                if (interval.ElapsedMilliseconds >= 180)
                                    Report("Bytevergleich", member.File.Path, index, matchedHashes.Length, current.Position, member.File.Bytes);
                            }))
                            {
                                if (cached is not null && firstMember.Stamp?.CanCache == true
                                    && member.Stamp == WindowsFileStamp.Read(current)
                                    && firstMember.Stamp == WindowsFileStamp.Read(reference))
                                {
                                    cache[member.File.Path] = cached with { VerifiedAgainst = firstMember.Stamp };
                                    dirty = true;
                                }
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
                    SaveCheckpoint();
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
            .ThenByDescending(group => group.Files[0].File.Bytes).ToArray(), issues, candidates.Length, reused,
            paused || cancellation.IsCancellationRequested, reusedComparisons, bytesRead);

        List<DuplicateMember> SelectHashCandidates()
        {
            var selected = new List<DuplicateMember>();
            int sampleTotal = candidates.Count(file => file.Bytes >= SamplingThreshold);
            int sampled = 0;
            foreach (var sizeGroup in candidates.GroupBy(file => file.Bytes))
            {
                cancellation.ThrowIfCancellationRequested();
                if (sizeGroup.Key < SamplingThreshold)
                {
                    selected.AddRange(sizeGroup.Select(file => new DuplicateMember(file, null)));
                    continue;
                }

                var samples = new List<(DuplicateMember Member, string? Sample)>();
                foreach (FileEntry file in sizeGroup)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Report("Stichproben", file.Path, sampled++, sampleTotal);
                    try
                    {
                        using FileStream stream = Open(file, out WindowsFileStamp? stamp, FileOptions.RandomAccess);
                        string? sample = null;
                        if (stamp?.CanCache == true)
                        {
                            CachedHash? cached = GetCached(file.Path, stamp);
                            sample = cached?.Sample;
                            if (sample is null)
                            {
                                using IncrementalHash algorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                                foreach (long offset in new long[] { 0, (file.Bytes - SampleBlockSize) / 2, file.Bytes - SampleBlockSize })
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    stream.Position = offset;
                                    int count = stream.ReadAtLeast(buffer.AsSpan(0, SampleBlockSize), SampleBlockSize, throwOnEndOfStream: false);
                                    bytesRead += count;
                                    if (count != SampleBlockSize)
                                        throw new IOException("Datei konnte während der Stichprobe nicht vollständig gelesen werden.");
                                    algorithm.AppendData(buffer.AsSpan(0, count));
                                }
                                sample = Convert.ToHexString(algorithm.GetHashAndReset());
                                if (stamp != WindowsFileStamp.Read(stream))
                                    throw new IOException("Datei hat sich während der Prüfung geändert. Inventar erneut scannen.");
                                cache[file.Path] = new CachedHash(file.Path, stamp, cached?.Hash, sample, cached?.VerifiedAgainst);
                                dirty = true;
                            }
                        }
                        samples.Add((new DuplicateMember(file, stamp), sample));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        issues.Add(new ScanIssue(file.Path, error.Message));
                    }
                    SaveCheckpoint();
                }

                // An unverifiable stamp prevents filtering the entire size group by samples.
                var remaining = samples.Any(entry => entry.Sample is null)
                    ? samples
                    : samples.GroupBy(entry => entry.Sample).Where(group => group.Skip(1).Any()).SelectMany(group => group);
                selected.AddRange(remaining.Select(entry => entry.Member));
            }
            return selected;
        }

        CachedHash? GetCached(string path, WindowsFileStamp? stamp) =>
            stamp?.CanCache == true && cache.TryGetValue(path, out CachedHash? entry)
            && (entry.Stamp with { Attributes = stamp.Attributes }) == stamp ? entry : null;

        void SaveCheckpoint()
        {
            if (dirty && checkpoint.Elapsed.TotalSeconds >= 4)
            {
                dirty = !SaveCache(cache, issues);
                checkpoint.Restart();
            }
        }

        void Report(string phase, string path, int processed, int total, long position = 0, long length = 0)
        {
            progress?.Report(new DuplicateProgress(phase, path, processed, total, bytesRead,
                elapsed.Elapsed.TotalSeconds > 0 ? bytesRead / elapsed.Elapsed.TotalSeconds : 0, position, length));
            interval.Restart();
        }
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

    private static FileStream Open(FileEntry file, out WindowsFileStamp? stamp, FileOptions options = FileOptions.SequentialScan)
    {
        if (File.GetAttributes(file.Path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Verknüpfte Datei wurde übersprungen.");
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, options);
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
        CancellationToken cancellation, Action<int> recordRead)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = left.ReadAtLeast(first, first.Length, throwOnEndOfStream: false);
            recordRead(count);
            cancellation.ThrowIfCancellationRequested();
            int other = right.ReadAtLeast(second, second.Length, throwOnEndOfStream: false);
            recordRead(other);
            if (count != other || !first.AsSpan(0, count).SequenceEqual(second.AsSpan(0, other)))
                return false;
            if (count < first.Length)
                return true;
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
