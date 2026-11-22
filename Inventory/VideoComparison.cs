using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

internal sealed record VideoDuration(FileEntry File, long Ticks);
internal sealed record VideoFrame(long Ticks, byte[] Pixels);
internal sealed record VideoGroup(VideoDuration Reference, VideoDuration[] Videos);
internal sealed record VideoResult(VideoGroup[] Groups, List<ScanIssue> Issues, int Checked);

internal sealed record VideoVersion(string Path, long Bytes, long Created, long Modified, long Duration)
{
    public static VideoVersion From(VideoDuration video) => new(video.File.Path.ToUpperInvariant(), video.File.Bytes,
        video.File.CreatedUtc.ToFileTimeUtc(), video.File.ModifiedUtc.ToFileTimeUtc(), video.Ticks);
}

internal sealed record VideoPair(VideoVersion First, VideoVersion Second)
{
    public static VideoPair For(VideoDuration left, VideoDuration right) =>
        StringComparer.OrdinalIgnoreCase.Compare(left.File.Path, right.File.Path) < 0
            ? new(VideoVersion.From(left), VideoVersion.From(right))
            : new(VideoVersion.From(right), VideoVersion.From(left));
}

[JsonSerializable(typeof(VideoPair[]))]
internal partial class VideoExclusionsJson : JsonSerializerContext;

internal static class VideoComparison
{
    public const int ToleranceSeconds = 1;
    public static bool Supports(FileEntry file) => file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    public static VideoResult Run(FileEntry[] candidates, IProgress<int>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = new WindowsVideoSession();
        FileEntry[] files = candidates.Where(Supports).DistinctBy(file => file.Path, InventoryPaths.Comparer).ToArray();
        var videos = new VideoDuration?[files.Length];
        var issues = new ScanIssue?[files.Length];
        var progressGate = new object();
        int completed = 0;
        progress?.Report(0);
        try
        {
            Parallel.For(0, files.Length, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 2 },
                () => new WindowsVideoSession(),
                (index, _, worker) =>
                {
                    FileEntry file = files[index];
                    try
                    {
                        using FileStream stream = Open(file);
                        videos[index] = new VideoDuration(file, WindowsVideoReader.ReadDuration(file.Path));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        issues[index] = new ScanIssue(file.Path, error.Message);
                    }

                    if (progress is not null)
                    {
                        lock (progressGate)
                        {
                            completed++;
                            if (completed % 4 == 0 || completed == files.Length)
                                progress.Report(completed);
                        }
                    }
                    return worker;
                },
                worker => worker.Dispose());
        }
        catch (AggregateException error) when (error.InnerExceptions.All(cause =>
            cause is InvalidDataException or DllNotFoundException or EntryPointNotFoundException))
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerExceptions[0]);
        }

        VideoDuration[] sorted = videos.OfType<VideoDuration>().OrderBy(video => video.Ticks).ThenBy(video => video.File.Bytes)
            .ThenBy(video => video.File.Path, InventoryPaths.Comparer).ToArray();
        return new VideoResult(Group(sorted, token), issues.OfType<ScanIssue>().ToList(), sorted.Length);
    }

    private static VideoGroup[] Group(VideoDuration[] sorted, CancellationToken token)
    {
        const long tolerance = ToleranceSeconds * TimeSpan.TicksPerSecond;
        var groups = new List<List<VideoDuration>>();
        var activeBySize = new Dictionary<long, List<VideoDuration>>();
        foreach (VideoDuration video in sorted)
        {
            token.ThrowIfCancellationRequested();
            long bytes = video.File.Bytes;
            if (!activeBySize.TryGetValue(bytes, out List<VideoDuration>? group)
                || video.Ticks - group[0].Ticks > tolerance)
            {
                group = [];
                activeBySize[bytes] = group;
                groups.Add(group);
            }
            group.Add(video);
        }
        return groups.Where(group => group.Count >= 2)
            .Select(group => new VideoGroup(group.MinBy(video => video.File.ModifiedUtc)!, group.ToArray())).ToArray();
    }

    public static byte[] ReadThumbnail(VideoDuration video, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = new WindowsVideoSession();
        using FileStream stream = Open(video.File);
        return WindowsVideoReader.ReadThumbnail(video.File.Path, width, height, token);
    }

    public static VideoFrame[] ReadReviewFrames(VideoDuration video, int width, int height, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var session = new WindowsVideoSession();
        using FileStream stream = Open(video.File);
        return WindowsVideoReader.ReadReviewFrames(video.File.Path, video.Ticks, width, height, token);
    }

    private static FileStream Open(FileEntry file)
    {
        if (File.GetAttributes(file.Path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Verknüpfte Datei wurde übersprungen.");
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != file.Bytes || File.GetLastWriteTimeUtc(stream.SafeFileHandle).ToFileTimeUtc() != file.ModifiedUtc.ToFileTimeUtc()
                || File.GetCreationTimeUtc(stream.SafeFileHandle).ToFileTimeUtc() != file.CreatedUtc.ToFileTimeUtc())
                throw new IOException("Die Datei hat sich seit dem Inventarscan geändert.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
