using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FilesizeChecker.Inventory;

internal enum SimilarityLevel { Strict, Balanced, Broad }

internal sealed record NameClues(string Stem, string Normalized, string TokenKey, string NumberKey,
    string? Version, string? Date, string? Marker);

internal sealed record NameMatch(FileEntry File, NameClues Name, int Score, string Reason);

internal sealed record NameFamily(string Name, IReadOnlyList<NameMatch> Members);

internal static partial class NameSimilarity
{
    [GeneratedRegex(@"(?:^|[\s._-])v(?:ersion)?[\s._-]*(?<number>\d+(?:\.\d+)*(?:[-._]?(?:alpha|beta|rc|preview|dev)(?:[.-]?\d+)?)?)(?=$|[\s._-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitVersion();

    [GeneratedRegex(@"[\s_-]+(?<number>\d{1,8}(?:\.\d{1,8}){1,7}(?:-(?:alpha|beta|rc|preview|dev)[.\d]*)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DottedVersion();

    [GeneratedRegex(@"(?:^|[\s._-])(?<year>(?:19|20|21)\d{2})[._-]?(?<month>\d{2})[._-]?(?<day>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();

    [GeneratedRegex(@"(?:[\s._-]*(?<mark>\(\d+\))|[\s._-]+(?<mark>kopie|copy|alt|backup|final|entwurf|draft)(?:[\s._-]*\(\d+\))?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VariantSuffix();

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex CamelCase();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"\p{Nd}+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    public static NameClues Analyze(string filename)
    {
        string original = Path.GetFileNameWithoutExtension(filename);
        string stem = original;
        var markers = new List<string>();
        string? date = null;
        while (stem.Length > 0)
        {
            Match variant = VariantSuffix().Match(stem);
            if (variant.Success && variant.Index > 0)
            {
                markers.Add(variant.Groups["mark"].Value);
                stem = stem[..variant.Index];
                continue;
            }
            Match suffix = DateSuffix().Match(stem);
            if (date is null && suffix.Success && suffix.Index > 0
                && DateOnly.TryParseExact(suffix.Groups["year"].Value + suffix.Groups["month"].Value + suffix.Groups["day"].Value,
                    "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                date = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                stem = stem[..suffix.Index];
                continue;
            }
            break;
        }

        string? version = null;
        MatchCollection explicitVersions = ExplicitVersion().Matches(stem);
        if (explicitVersions.Count == 1)
        {
            version = explicitVersions[0].Groups["number"].Value;
            string reduced = ExplicitVersion().Replace(stem, " ").Trim(' ', '.', '_', '-');
            if (reduced.Length > 0)
                stem = reduced;
        }
        else if (explicitVersions.Count > 1)
        {
            markers.Add("mehrere Versionsangaben");
        }
        else
        {
            Match numeric = DottedVersion().Match(stem);
            if (numeric.Success)
            {
                version = numeric.Groups["number"].Value;
                stem = stem[..numeric.Index];
            }
        }

        string normalized = Normalize(stem);
        if (normalized.Length == 0)
        {
            stem = original.Length == 0 ? filename : original;
            normalized = Normalize(stem);
        }
        if (normalized.Length == 0)
            normalized = stem.ToLowerInvariant();
        string tokens = string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        string numbers = string.Join('|', Digits().Matches(normalized).Select(match => match.Value).Order(StringComparer.Ordinal));
        return new NameClues(stem, normalized, tokens, numbers, version, date,
            markers.Count == 0 ? null : string.Join(", ", markers));
    }

    public static NameFamily[] Find(IEnumerable<FileEntry> files, SimilarityLevel level, CancellationToken cancellation = default)
    {
        var anchors = files.DistinctBy(file => file.Path, InventoryPaths.Comparer)
            .Select(file => new NameMatch(file, Analyze(file.Name), 100, ""))
            .GroupBy(member => member.Name.TokenKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(member => member.Name.Version is null ? 0 : 1)
                .ThenBy(member => member.Name.Marker is null ? 0 : 1)
                .ThenBy(member => member.File.Name.Length).ThenBy(member => member.File.Path, InventoryPaths.Comparer).ToArray())
            .OrderByDescending(group => group.Length).ThenBy(group => group[0].Name.TokenKey, StringComparer.Ordinal).ToArray();

        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int group = 0; group < anchors.Length; group++)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (string gram in Grams(anchors[group][0].Name))
            {
                if (!index.TryGetValue(gram, out List<int>? matches))
                    index[gram] = matches = [];
                matches.Add(group);
            }
        }

        int threshold = level switch { SimilarityLevel.Strict => 91, SimilarityLevel.Broad => 77, _ => 80 };
        int maxPostings = Math.Max(120, anchors.Length / 12);
        var assigned = new bool[anchors.Length];
        var families = new List<NameFamily>();
        for (int group = 0; group < anchors.Length; group++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (assigned[group])
                continue;
            assigned[group] = true;
            NameMatch anchor = anchors[group][0];
            var members = new List<NameMatch>(anchors[group].Length);
            AddGroup(members, anchors[group], anchor.Name, 100);
            var candidates = new Dictionary<int, int>();
            foreach (string gram in Grams(anchor.Name))
            {
                List<int> posting = index[gram];
                if (posting.Count > maxPostings)
                    continue;
                foreach (int candidate in posting)
                {
                    if (!assigned[candidate])
                        candidates[candidate] = candidates.GetValueOrDefault(candidate) + 1;
                }
            }
            foreach (int candidate in candidates.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Select(pair => pair.Key))
            {
                cancellation.ThrowIfCancellationRequested();
                NameClues other = anchors[candidate][0].Name;
                int score = Score(anchor.Name, other, threshold);
                if (score < threshold)
                    continue;
                assigned[candidate] = true;
                AddGroup(members, anchors[candidate], anchor.Name, score);
            }
            if (members.Count > 1)
                families.Add(new NameFamily(anchor.Name.Stem, members));
        }
        return families.OrderBy(family => family.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static void AddGroup(List<NameMatch> members, NameMatch[] group, NameClues anchor, int score)
    {
        foreach (NameMatch entry in group)
        {
            string reason = members.Count == 0 ? "Bezugsname"
                : score < 100 ? "Ähnliche Schreibweise"
                : entry.Name.Normalized == anchor.Normalized ? "Gemeinsamer Namensstamm"
                : entry.Name.TokenKey == anchor.TokenKey ? "Andere Wortreihenfolge"
                : "Unterschiedliche Worttrennung";
            members.Add(entry with { Score = score, Reason = reason });
        }
    }

    private static string Normalize(string value)
    {
        string decomposed = CamelCase().Replace(value, " ").Normalize(NormalizationForm.FormD).ToLowerInvariant();
        var folded = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            folded.Append(character == 'ß' ? "ss" : character.ToString());
        }
        return Separators().Replace(folded.ToString(), " ").Trim();
    }

    private static HashSet<string> Grams(NameClues name)
    {
        var grams = new HashSet<string>(StringComparer.Ordinal);
        AddGrams(name.TokenKey);
        if (name.Normalized != name.TokenKey)
            AddGrams(name.Normalized);
        return grams;

        void AddGrams(string value)
        {
            string text = value.Replace(" ", "", StringComparison.Ordinal);
            for (int offset = 0; offset + 1 < text.Length; offset++)
            {
                if (text.Length <= 9)
                    grams.Add(text.Substring(offset, 2));
                if (offset + 2 < text.Length)
                    grams.Add(text.Substring(offset, 3));
            }
        }
    }

    private static int Score(NameClues first, NameClues second, int threshold)
    {
        if (first.NumberKey != second.NumberKey)
            return 0;
        string left = first.TokenKey.Replace(" ", "", StringComparison.Ordinal);
        string right = second.TokenKey.Replace(" ", "", StringComparison.Ordinal);
        int length = Math.Max(left.Length, right.Length);
        int allowed = Math.Max(1, length * (100 - threshold) / 100);
        if (Math.Min(left.Length, right.Length) < 5 || Math.Abs(left.Length - right.Length) > allowed)
            return 0;

        // Ein gemeinsames Wort allein reicht bei mehrteiligen Namen nicht für einen Treffer.
        int edits = EditDistance(left, right, allowed);
        if (edits > 0 && (first.Normalized != first.TokenKey || second.Normalized != second.TokenKey))
        {
            edits = Math.Min(edits, EditDistance(first.Normalized.Replace(" ", "", StringComparison.Ordinal),
                second.Normalized.Replace(" ", "", StringComparison.Ordinal), allowed));
        }
        if (edits > allowed)
            return 0;
        return (int)Math.Round(100.0 * (length - edits) / length);
    }

    private static int EditDistance(string first, string second, int allowed)
    {
        Span<int> older = stackalloc int[second.Length + 1];
        Span<int> previous = stackalloc int[second.Length + 1];
        Span<int> current = stackalloc int[second.Length + 1];
        for (int index = 0; index <= second.Length; index++)
            previous[index] = index;
        for (int row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            int minimum = row;
            for (int column = 1; column <= second.Length; column++)
            {
                int replacement = previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1);
                int value = Math.Min(replacement, Math.Min(current[column - 1], previous[column]) + 1);
                if (row > 1 && column > 1 && first[row - 1] == second[column - 2] && first[row - 2] == second[column - 1])
                    value = Math.Min(value, older[column - 2] + 1);
                current[column] = value;
                minimum = Math.Min(minimum, value);
            }
            if (minimum > allowed)
                return allowed + 1;
            Span<int> storage = older;
            older = previous;
            previous = current;
            current = storage;
        }
        return previous[second.Length];
    }
}
