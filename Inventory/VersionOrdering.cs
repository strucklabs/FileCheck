using System.Globalization;
using System.Text.RegularExpressions;

namespace FilesizeChecker.Inventory;

internal sealed record VersionOrder(string Summary, IReadOnlyList<string> Evidence, int? Direction);

internal static partial class VersionOrdering
{
    [GeneratedRegex(@"^v?(?<number>[0-9]+(?:\.[0-9]+){0,7})(?:[-\s._]?(?<stage>alpha|beta|rc|preview|pre|dev)[.\s_-]?(?<counter>[0-9]+)?)?(?:\+[a-z0-9.-]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static VersionOrder Compare(VersionReport first, VersionReport second)
    {
        var reasons = new List<string>();
        var directions = new HashSet<int>();
        bool comparable = false;
        foreach (VersionEvidence left in first.Evidence.Where(item => item.Kind <= VersionKind.Model))
        {
            foreach (VersionEvidence right in second.Evidence.Where(item => item.Kind == left.Kind))
            {
                if (!ShareScope(left, right) || (left.Kind is VersionKind.Revision or VersionKind.Document && left.Label != right.Label))
                    continue;
                if (!TryRead(left.Value, out VersionKey? a) || !TryRead(right.Value, out VersionKey? b))
                    continue;
                comparable = true;
                int direction = Math.Sign(a!.CompareTo(b!));
                if (direction != 0)
                    directions.Add(direction);
                string relation = direction < 0 ? "<" : direction > 0 ? ">" : "=";
                reasons.Add($"{left.Label}: {left.Value} {relation} {right.Value}");
            }
        }

        string summary = directions.Count switch
        {
            > 1 => "Widersprüchliche Versionshinweise · Reihenfolge offen",
            1 when directions.Contains(-1) => "Version rechts höher laut vergleichbaren Angaben",
            1 => "Version links höher laut vergleichbaren Angaben",
            _ when comparable => "Vergleichbare Versionsangaben gleich",
            _ => "Reihenfolge offen · keine vergleichbaren Versionsnummern"
        };
        int? order = directions.Count > 1 || !comparable ? null : directions.Count == 0 ? 0 : directions.Single();
        return new VersionOrder(summary, reasons.Distinct().ToArray(), order);
    }

    public static string FilenameDifference(VersionReport? report)
    {
        VersionEvidence? filename = report?.Evidence.FirstOrDefault(item => item.Kind == VersionKind.Filename);
        if (filename is null || !TryRead(filename.Value, out VersionKey? key))
            return "";
        var differences = new List<string>();
        foreach (VersionEvidence item in report!.Evidence.Where(item => item.Kind is VersionKind.File or VersionKind.Product
            or VersionKind.Package or VersionKind.Document or VersionKind.Model))
        {
            if (TryRead(item.Value, out VersionKey? actual) && key!.CompareTo(actual!) != 0)
                differences.Add($"{item.Label} {item.Value}");
        }
        return differences.Count == 0 ? "" : $"Dateiname {filename.Value} weicht ab von {string.Join(", ", differences)}.";
    }

    private static bool ShareScope(VersionEvidence left, VersionEvidence right)
    {
        if (left.Label != right.Label && left.Kind == VersionKind.Package)
            return false;
        if (left.Scope is null || right.Scope is null)
            return true;
        if (InventoryPaths.Comparer.Equals(left.Scope, right.Scope))
            return true;
        return left.Kind == VersionKind.Product
            && NameSimilarity.Analyze(left.Scope + ".placeholder").Normalized == NameSimilarity.Analyze(right.Scope + ".placeholder").Normalized;
    }

    private static bool TryRead(string value, out VersionKey? key)
    {
        key = null;
        string cleaned = value.Trim().Replace(", ", ".", StringComparison.Ordinal).Replace(',', '.');
        Match match = VersionPattern().Match(cleaned);
        if (!match.Success)
            return false;

        string[] pieces = match.Groups["number"].Value.Split('.');
        var numbers = new int[pieces.Length];
        for (int index = 0; index < pieces.Length; index++)
        {
            if (!int.TryParse(pieces[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
                return false;
        }
        string suffix = match.Groups["stage"].Value.ToLowerInvariant();
        int stage = suffix switch { "dev" => 0, "alpha" => 1, "beta" => 2, "preview" or "pre" => 3, "rc" => 4, _ => 5 };
        int revision = 0;
        if (match.Groups["counter"].Success && !int.TryParse(match.Groups["counter"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision))
            return false;
        key = new VersionKey(numbers, stage, revision);
        return true;
    }

    private sealed record VersionKey(int[] Numbers, int Stage, int Revision)
    {
        public int CompareTo(VersionKey other)
        {
            for (int index = 0; index < Math.Max(Numbers.Length, other.Numbers.Length); index++)
            {
                int first = index < Numbers.Length ? Numbers[index] : 0;
                int second = index < other.Numbers.Length ? other.Numbers[index] : 0;
                if (first != second)
                    return first.CompareTo(second);
            }
            int order = Stage.CompareTo(other.Stage);
            return order != 0 ? order : Revision.CompareTo(other.Revision);
        }
    }
}
