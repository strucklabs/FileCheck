using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace FilesizeChecker.Inventory;

internal enum ImageRelation { Different, Similar, PixelEqual, Reference }
internal enum ImageCompression { Unknown, Lossy, Lossless }

internal sealed record ImageTechnicalInfo(ImageCompression Compression, bool HasAlpha, bool OrientationAdjusted);
internal sealed record ImageFeatures(int Width, int Height, string Format, string PixelHash, ulong Pattern, byte[] Colors,
    ImageTechnicalInfo? Technical = null);
internal sealed record ComparedImage(FileEntry File, WindowsFileStamp? Stamp, ImageFeatures Features, string? ContentHash = null);
internal sealed record ImageMatch(ComparedImage Image, ImageRelation Relation);
internal sealed record ImageGroup(ComparedImage Reference, List<ImageMatch> Matches);
internal sealed record ImageSearchResult(IReadOnlyList<ImageGroup> Groups, IReadOnlyList<ScanIssue> Issues, int Examined, int Reused);
internal sealed record ImageProgress(string Phase, int Done, int Total);
internal sealed record CachedImage(string Path, WindowsFileStamp? Stamp, ImageFeatures Features, string? ContentHash = null);

[JsonSerializable(typeof(CachedImage[]))]
internal partial class ImageCacheJson : JsonSerializerContext;

internal sealed class ImageComparison(string cachePath)
{
    private const int SampleSize = 16;
    private const int ColorGridSize = 4;
    private const long MaximumPixels = 48_000_000;
    internal const string FileFilter = "Bilder (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp";

    public static bool Supports(FileEntry file) => file.Extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";

    public static string RelationLabel(ImageRelation relation) => relation switch
    {
        ImageRelation.PixelEqual => "Pixelgleich",
        ImageRelation.Similar => "Visuell ähnlich",
        ImageRelation.Reference => "Referenz",
        _ => "Kein Bildtreffer"
    };

    public static string CompressionLabel(ImageCompression compression) => compression switch
    {
        ImageCompression.Lossless => "Verlustfrei",
        ImageCompression.Lossy => "Verlustbehaftet",
        _ => "Kompression unbekannt"
    };

    public ImageSearchResult Run(FileEntry[] candidates, FileEntry? reference, bool direct, bool wider,
        IProgress<ImageProgress>? progress, CancellationToken token)
    {
        var issues = new List<ScanIssue>();
        var images = new List<ComparedImage>();
        var cache = new Dictionary<string, CachedImage>(InventoryPaths.Comparer);
        int reused = 0;
        bool changed = false;
        try
        {
            foreach (CachedImage item in LocalJsonFile.Load(cachePath, ImageCacheJson.Default.CachedImageArray) ?? [])
                cache[item.Path] = item;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            issues.Add(new ScanIssue(cachePath, $"Bildmerkmale konnten nicht geladen werden. {error.Message}"));
        }

        try
        {
            ComparedImage? first = reference is null ? null : Read(reference);
            if (reference is not null && first is null)
                return new ImageSearchResult([], issues, 0, reused);
            FileEntry[] files = candidates.Where(Supports).DistinctBy(file => file.Path, InventoryPaths.Comparer)
                .Where(file => reference is null || !InventoryPaths.Comparer.Equals(file.Path, reference.Path)).ToArray();
            progress?.Report(new ImageProgress("Bildmerkmale", 0, files.Length));
            for (int index = 0; index < files.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (Read(files[index]) is { } image)
                    images.Add(image);
                if ((index + 1) % 16 == 0 || index + 1 == files.Length)
                    progress?.Report(new ImageProgress("Bildmerkmale", index + 1, files.Length));
            }

            int examined = images.Count + (first is null ? 0 : 1);
            if (direct && first is null)
                first = OrderReferenceCandidates(images).FirstOrDefault();
            IReadOnlyList<ImageGroup> groups = first is not null
                ? [CompareAgainstReference(first, images, wider, includeUnmatched: direct, token)]
                : direct ? [] : GroupImages(images, wider, progress, token);
            return new ImageSearchResult(groups, issues, examined, reused);
        }
        finally
        {
            if (changed)
            {
                try
                {
                    LocalJsonFile.Save(cachePath, cache.Values.ToArray(), ImageCacheJson.Default.CachedImageArray);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new ScanIssue(cachePath, $"Bildmerkmale konnten nicht gespeichert werden. {error.Message}"));
                }
            }
        }

        ComparedImage? Read(FileEntry file)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using FileStream stream = Open(file, out WindowsFileStamp? stamp);
                // File systems without a reliable change stamp require proof from the complete encoded file.
                string? contentHash = stamp?.CanCache == true ? null : ReadContentHash(stream, token);
                if (cache.TryGetValue(file.Path, out CachedImage? saved)
                    && saved.Features is { Width: > 0, Height: > 0, Colors.Length: ColorGridSize * ColorGridSize * 3 }
                    && (contentHash is null ? saved.ContentHash is null && saved.Stamp == stamp : saved.ContentHash == contentHash))
                {
                    ImageFeatures cachedFeatures = saved.Features;
                    if (cachedFeatures.Technical is null)
                    {
                        ImageCompression compression = ReadCompression(stream, token);
                        using var source = new SKManagedStream(stream, disposeManagedStream: false);
                        using SKCodec codec = SKCodec.Create(source) ?? throw new InvalidDataException("Bild konnte nicht gelesen werden.");
                        cachedFeatures = cachedFeatures with { Technical = ReadTechnicalInfo(codec, compression) };
                        cache[file.Path] = saved with { Features = cachedFeatures };
                        changed = true;
                    }
                    reused++;
                    return new ComparedImage(file, stamp, cachedFeatures, contentHash);
                }

                using SKBitmap bitmap = Decode(stream, token, out string format, out ImageTechnicalInfo technical);
                string hash = Convert.ToHexString(SHA256.HashData(bitmap.GetPixelSpan()));
                token.ThrowIfCancellationRequested();
                const int oversample = 4;
                using SKBitmap sample = Resize(bitmap, SampleSize * oversample, SampleSize * oversample, SKColors.White);
                byte[] colors = AverageColors(sample.GetPixelSpan(), sample.Width, SampleSize, 4);
                byte[] colorBlocks = AverageColors(colors, SampleSize, ColorGridSize, 3);
                ulong pattern = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int left = (2 * y + 1) * SampleSize + x * 15 / 8;
                        int right = (2 * y + 1) * SampleSize + (x + 1) * 15 / 8;
                        pattern = (pattern << 1) | (Light(colors, left) > Light(colors, right) ? 1UL : 0UL);
                    }
                var features = new ImageFeatures(bitmap.Width, bitmap.Height, format, hash, pattern, colorBlocks, technical);
                if (WindowsFileStamp.Read(stream) != stamp)
                    throw new IOException("Datei hat sich während der Bildprüfung geändert.");
                cache[file.Path] = new CachedImage(file.Path, stamp, features, contentHash);
                changed = true;
                return new ComparedImage(file, stamp, features, contentHash);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                issues.Add(new ScanIssue(file.Path, error.Message));
                return null;
            }
        }
    }

    internal static ImageRelation Compare(ImageFeatures left, ImageFeatures right, bool wider)
    {
        if (left.Width == right.Width && left.Height == right.Height && left.PixelHash == right.PixelHash)
            return ImageRelation.PixelEqual;
        double aspectDelta = Math.Abs(Math.Log((double)left.Width * right.Height / ((double)left.Height * right.Width)));
        if (aspectDelta > (wider ? 0.08 : 0.035) || Distance(left.Pattern, right.Pattern) > (wider ? 14 : 8))
            return ImageRelation.Different;
        int colorDelta = 0;
        for (int i = 0; i < left.Colors.Length; i++)
            colorDelta += Math.Abs(left.Colors[i] - right.Colors[i]);
        return colorDelta <= left.Colors.Length * (wider ? 24 : 12) ? ImageRelation.Similar : ImageRelation.Different;
    }

    private static ImageGroup CompareAgainstReference(ComparedImage reference, IEnumerable<ComparedImage> images,
        bool wider, bool includeUnmatched, CancellationToken token)
    {
        var matches = new List<ImageMatch>();
        foreach (ComparedImage image in images)
        {
            token.ThrowIfCancellationRequested();
            if (InventoryPaths.Comparer.Equals(reference.File.Path, image.File.Path))
                continue;
            ImageRelation relation = Compare(reference.Features, image.Features, wider);
            if (includeUnmatched || relation != ImageRelation.Different)
                matches.Add(new ImageMatch(image, relation));
        }
        return new ImageGroup(reference, matches.OrderByDescending(match => match.Relation).ToList());
    }

    internal static ImageGroup ChangeReference(ImageGroup group, ComparedImage reference, bool wider) =>
        CompareAgainstReference(reference, group.Matches.Select(match => match.Image).Prepend(group.Reference),
            wider, includeUnmatched: true, CancellationToken.None);

    private static ImageGroup[] GroupImages(List<ComparedImage> images, bool wider, IProgress<ImageProgress>? progress,
        CancellationToken token)
    {
        var groups = new List<ImageGroup>();
        PatternNode? index = null;
        int done = 0;
        progress?.Report(new ImageProgress("Bildgruppen", 0, images.Count));
        foreach (var identical in OrderReferenceCandidates(images)
                     .GroupBy(image => (image.Features.Width, image.Features.Height, image.Features.PixelHash)))
        {
            token.ThrowIfCancellationRequested();
            ComparedImage first = identical.First();
            ImageGroup? group = index?.Find(first.Features, wider, token);
            if (group is null)
            {
                group = new ImageGroup(first, []);
                groups.Add(group);
                if (index is null)
                    index = new PatternNode(group);
                else
                    index.Add(group);
            }
            else
            {
                group.Matches.Add(new ImageMatch(first, ImageRelation.Similar));
            }

            foreach (ComparedImage copy in identical.Skip(1))
                group.Matches.Add(new ImageMatch(copy, Compare(group.Reference.Features, copy.Features, wider)));
            done += identical.Count();
            if (done % 16 == 0 || done == images.Count)
                progress?.Report(new ImageProgress("Bildgruppen", done, images.Count));
        }
        return groups.Where(group => group.Matches.Count > 0).ToArray();
    }

    private static IEnumerable<ComparedImage> OrderReferenceCandidates(IEnumerable<ComparedImage> images)
    {
        var byPriority = images.OrderByDescending(image => (long)image.Features.Width * image.Features.Height)
            .ThenBy(image => image.File.ModifiedUtc)
            .ThenBy(image => image.File.Path, InventoryPaths.Comparer)
            .GroupBy(image => (image.Features.Width, image.Features.Height));
        foreach (var dimensions in byPriority)
        {
            var ordered = dimensions.OrderByDescending(image => image.Features.Technical?.Compression == ImageCompression.Lossless)
                .ToArray();
            var lossyByFormat = ordered.Where(image => image.Features.Technical?.Compression == ImageCompression.Lossy)
                .GroupBy(image => image.Features.Format)
                .ToDictionary(format => format.Key, format => new Queue<ComparedImage>(format
                    .OrderBy(image => image.File.ModifiedUtc).ThenByDescending(image => image.File.Bytes)));
            // A larger file only advances within the slots for its own lossy format.
            foreach (ComparedImage image in ordered)
                yield return image.Features.Technical?.Compression == ImageCompression.Lossy
                    ? lossyByFormat[image.Features.Format].Dequeue() : image;
        }
    }

    private sealed class PatternNode(ImageGroup group)
    {
        private readonly List<ImageGroup> groups = [group];
        private readonly Dictionary<int, PatternNode> children = [];
        private readonly ulong pattern = group.Reference.Features.Pattern;

        public void Add(ImageGroup value)
        {
            PatternNode node = this;
            while (true)
            {
                int distance = Distance(node.pattern, value.Reference.Features.Pattern);
                if (distance == 0)
                {
                    node.groups.Add(value);
                    return;
                }
                if (!node.children.TryGetValue(distance, out PatternNode? child))
                {
                    node.children.Add(distance, new PatternNode(value));
                    return;
                }
                node = child;
            }
        }

        public ImageGroup? Find(ImageFeatures features, bool wider, CancellationToken token)
        {
            int radius = wider ? 14 : 8;
            int distance = Distance(pattern, features.Pattern);
            token.ThrowIfCancellationRequested();
            if (distance <= radius)
                foreach (ImageGroup candidate in groups)
                {
                    token.ThrowIfCancellationRequested();
                    if (Compare(candidate.Reference.Features, features, wider) != ImageRelation.Different)
                        return candidate;
                }

            foreach (var child in children)
                if (Math.Abs(child.Key - distance) <= radius && child.Value.Find(features, wider, token) is { } found)
                    return found;
            return null;
        }
    }

    public static byte[] ReadPreview(ComparedImage image, CancellationToken token)
    {
        using FileStream stream = Open(image.File, out WindowsFileStamp? stamp);
        if (stamp != image.Stamp)
            throw new IOException("Datei hat sich seit dem Bildvergleich geändert.");
        if (image.ContentHash is string contentHash && ReadContentHash(stream, token) != contentHash)
            throw new IOException("Datei hat sich seit dem Bildvergleich geändert.");
        using SKBitmap bitmap = Decode(stream, token, out _, out _);
        double scale = Math.Min(1, 640d / Math.Max(bitmap.Width, bitmap.Height));
        using SKBitmap thumbnail = Resize(bitmap, Math.Max(1, (int)(bitmap.Width * scale)),
            Math.Max(1, (int)(bitmap.Height * scale)), SKColors.Transparent);
        if (WindowsFileStamp.Read(stream) != stamp)
            throw new IOException("Datei hat sich während der Vorschau geändert.");
        using SKData encoded = thumbnail.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static string ReadContentHash(FileStream stream, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = stream.Read(buffer);
                if (count == 0)
                    break;
                hash.AppendData(buffer.AsSpan(0, count));
            }
            stream.Position = 0;
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static FileStream Open(FileEntry file, out WindowsFileStamp? stamp)
    {
        if (File.GetAttributes(file.Path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Verknüpfte Datei wurde übersprungen.");
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            stamp = WindowsFileStamp.Read(stream);
            if (stream.Length != file.Bytes || (stamp?.Written ?? File.GetLastWriteTimeUtc(stream.SafeFileHandle).ToFileTimeUtc()) != file.ModifiedUtc.ToFileTimeUtc()
                || (stamp?.Created ?? File.GetCreationTimeUtc(stream.SafeFileHandle).ToFileTimeUtc()) != file.CreatedUtc.ToFileTimeUtc())
                throw new IOException("Datei hat sich seit der Erfassung geändert. Inventar erneut scannen.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static ImageCompression ReadCompression(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
            throw new InvalidDataException("Datei enthält kein unterstütztes JPEG-, PNG- oder WebP-Bild.");
        try
        {
            if (header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return ImageCompression.Lossless;
            if (header[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff }))
                return ReadJpegCompression(stream, token);
            if (header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8))
            {
                long end = Math.Min(stream.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]));
                Span<byte> chunk = stackalloc byte[8];
                while (stream.Position <= end - chunk.Length
                       && stream.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) == chunk.Length)
                {
                    token.ThrowIfCancellationRequested();
                    long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                    long next = stream.Position + size + (size & 1);
                    if (next > end)
                        break;
                    if (chunk[..4].SequenceEqual("VP8L"u8))
                        return ImageCompression.Lossless;
                    if (chunk[..4].SequenceEqual("VP8 "u8))
                        return ImageCompression.Lossy;
                    stream.Position = next;
                }
                return ImageCompression.Unknown;
            }
            throw new InvalidDataException("Datei enthält kein unterstütztes JPEG-, PNG- oder WebP-Bild.");
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static ImageCompression ReadJpegCompression(Stream stream, CancellationToken token)
    {
        stream.Position = 2;
        Span<byte> length = stackalloc byte[2];
        while (stream.ReadByte() == 0xff)
        {
            token.ThrowIfCancellationRequested();
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker is 0xc3 or 0xc7 or 0xcb or 0xcf)
                return ImageCompression.Lossless;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc5 or 0xc6 or 0xc9 or 0xca or 0xcd or 0xce)
                return ImageCompression.Lossy;
            if (marker is < 0xc0 or 0xd8 or 0xd9 or 0xda
                || stream.ReadAtLeast(length, length.Length, throwOnEndOfStream: false) != length.Length)
                break;
            int payload = BinaryPrimitives.ReadUInt16BigEndian(length) - length.Length;
            if (payload < 0 || stream.Position + payload > stream.Length)
                break;
            stream.Position += payload;
        }
        return ImageCompression.Unknown;
    }

    private static ImageTechnicalInfo ReadTechnicalInfo(SKCodec codec, ImageCompression compression) =>
        new(compression, codec.Info.AlphaType != SKAlphaType.Opaque, codec.EncodedOrigin != SKEncodedOrigin.TopLeft);

    private static SKBitmap Decode(Stream stream, CancellationToken token, out string format, out ImageTechnicalInfo technical)
    {
        ImageCompression compression = ReadCompression(stream, token);
        using var source = new SKManagedStream(stream, disposeManagedStream: false);
        using SKCodec codec = SKCodec.Create(source) ?? throw new InvalidDataException("Bild konnte nicht gelesen werden.");
        format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "JPEG",
            SKEncodedImageFormat.Png => "PNG",
            SKEncodedImageFormat.Webp => "WebP",
            _ => throw new InvalidDataException("Bildformat wird nicht unterstützt.")
        };
        technical = ReadTechnicalInfo(codec, compression);
        if (codec.FrameCount > 1 || codec.Info.Width <= 0 || codec.Info.Height <= 0
            || (long)codec.Info.Width * codec.Info.Height > MaximumPixels)
            throw new InvalidDataException("Die Bildprüfung unterstützt Einzelbilder bis 48 Megapixel.");
        using SKColorSpace colorSpace = SKColorSpace.CreateSrgb();
        var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace));
        try
        {
            if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new InvalidDataException("Bild konnte nicht vollständig decodiert werden.");
            token.ThrowIfCancellationRequested();
            if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft)
                return bitmap;

            int w = bitmap.Width;
            int h = bitmap.Height;
            bool swap = (int)codec.EncodedOrigin >= (int)SKEncodedOrigin.LeftTop;
            var oriented = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, bitmap.ColorType, bitmap.AlphaType, colorSpace));
            using (var canvas = new SKCanvas(oriented))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(codec.EncodedOrigin switch
                {
                    SKEncodedOrigin.TopRight => Matrix(-1, 0, w, 0, 1, 0),
                    SKEncodedOrigin.BottomRight => Matrix(-1, 0, w, 0, -1, h),
                    SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, h),
                    SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
                    SKEncodedOrigin.RightTop => Matrix(0, -1, h, 1, 0, 0),
                    SKEncodedOrigin.RightBottom => Matrix(0, -1, h, -1, 0, w),
                    SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, w),
                    _ => SKMatrix.Identity
                });
                canvas.DrawBitmap(bitmap, 0, 0, null);
            }
            bitmap.Dispose();
            return oriented;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static SKBitmap Resize(SKBitmap bitmap, int width, int height, SKColor background)
    {
        using SKColorSpace colorSpace = SKColorSpace.CreateSrgb();
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace));
        using var canvas = new SKCanvas(target);
        using SKImage image = SKImage.FromBitmap(bitmap);
        canvas.Clear(background);
        canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Flush();
        return target;
    }

    private static int Light(byte[] colors, int pixel) =>
        3 * colors[3 * pixel] + 6 * colors[3 * pixel + 1] + colors[3 * pixel + 2];

    private static byte[] AverageColors(ReadOnlySpan<byte> source, int width, int gridSize, int channels)
    {
        int block = width / gridSize;
        var result = new byte[gridSize * gridSize * 3];
        for (int y = 0; y < gridSize; y++)
            for (int x = 0; x < gridSize; x++)
                for (int channel = 0; channel < 3; channel++)
                {
                    int sum = 0;
                    for (int row = 0; row < block; row++)
                        for (int col = 0; col < block; col++)
                            sum += source[((y * block + row) * width + x * block + col) * channels + channel];
                    result[(y * gridSize + x) * 3 + channel] = (byte)(sum / (block * block));
                }
        return result;
    }

    private static int Distance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);
    private static SKMatrix Matrix(float a, float b, float c, float d, float e, float f) =>
        new(a, b, c, d, e, f, 0, 0, 1);
}
