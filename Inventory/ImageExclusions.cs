using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

internal sealed record ImageExclusion(string ReferencePath, string ReferencePixels, string MatchPath, string MatchPixels)
{
    public static ImageExclusion For(ImageGroup group, ImageMatch match) =>
        new(group.Reference.File.Path, group.Reference.Features.PixelHash, match.Image.File.Path, match.Image.Features.PixelHash);

    public static IEqualityComparer<ImageExclusion> Comparer { get; } = new PairComparer();

    private sealed class PairComparer : IEqualityComparer<ImageExclusion>
    {
        public bool Equals(ImageExclusion? x, ImageExclusion? y) => ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             ((SameImage(x.ReferencePath, x.ReferencePixels, y.ReferencePath, y.ReferencePixels)
               && SameImage(x.MatchPath, x.MatchPixels, y.MatchPath, y.MatchPixels))
              || (SameImage(x.ReferencePath, x.ReferencePixels, y.MatchPath, y.MatchPixels)
                  && SameImage(x.MatchPath, x.MatchPixels, y.ReferencePath, y.ReferencePixels))));

        public int GetHashCode(ImageExclusion pair) =>
            ImageHash(pair.ReferencePath, pair.ReferencePixels) ^ ImageHash(pair.MatchPath, pair.MatchPixels);

        private static bool SameImage(string path, string pixels, string otherPath, string otherPixels) =>
            InventoryPaths.Comparer.Equals(path, otherPath) && pixels == otherPixels;

        private static int ImageHash(string path, string pixels) =>
            HashCode.Combine(InventoryPaths.Comparer.GetHashCode(path), pixels);
    }
}

[JsonSerializable(typeof(ImageExclusion[]))]
internal partial class ImageExclusionsJson : JsonSerializerContext;
