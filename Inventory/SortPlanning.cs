using System.IO.Enumeration;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FilesizeChecker.Inventory;

[Flags]
internal enum SortCriteria { None = 0, Type = 1, Date = 2, Size = 4, Pattern = 8, NameFamily = 16, FuzzyNames = 32 }
internal enum SortDate { Modified, Created }
internal enum MissingDate { OwnFolder, OtherTimestamp }
internal enum CollisionAction { Pending, Skip, Replace, KeepBoth, Affixes }

internal sealed record SortRule(string Name, SortCriteria Criteria, SortDate Date, MissingDate MissingDate,
    string Pattern);
internal sealed record PlanChoice(bool Excluded = false, string? Folder = null,
    CollisionAction Collision = CollisionAction.Pending, string Prefix = "", string Suffix = "",
    string? ConflictTarget = null, string? ResolvedTarget = null, WindowsFileStamp? ExpectedReplacement = null);
internal sealed record SortConflictTarget(string Path, FileEntry? File, string Details);
internal sealed record PlanRow(FileEntry Source, string Target, string Group, string Action,
    string Details, bool Blocked = false, bool Collision = false, bool Excluded = false, bool Replace = false,
    WindowsFileStamp? Replacement = null, string? ConflictTarget = null, SortConflictTarget? ConflictDetails = null)
{
    public bool NeedsAttention => Blocked && !Excluded;
    public bool IsOpenConflict => NeedsAttention && Collision;
}

[JsonSerializable(typeof(SortRule[]))]
internal partial class SortRulesJson : JsonSerializerContext;

internal static partial class SortPlanning
{
    [GeneratedRegex(@"(?:[\s._-]*\(\d+\)|[\s._-]+(?:kopie|copy)(?:[\s._-]*\(\d+\))?|[\s._-]+(?:v(?:ersion)?[\s._-]*)\d+(?:\.\d+)*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VariantSuffix();

    [GeneratedRegex(@"[\s._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();

    public static PlanRow[] Create(FileEntry[] files, string destination, SortRule rule, bool move,
        IReadOnlyDictionary<string, PlanChoice> choices)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Wähle einen absoluten Zielordner.");
        if (rule.Criteria.HasFlag(SortCriteria.Pattern) && string.IsNullOrWhiteSpace(rule.Pattern))
            throw new ArgumentException("Gib für das Namensmuster zum Beispiel *.pdf ein.");

        string root = NormalizeFolder(destination);
        Dictionary<string, string> families = NameFolders(files, rule.Criteria);
        var claimed = new Dictionary<string, FileEntry>(InventoryPaths.Comparer);
        var folderErrors = new Dictionary<string, string?>(InventoryPaths.Comparer);
        var rows = new List<PlanRow>(files.Length);
        string action = move ? "Verschieben" : "Kopieren";

        foreach (FileEntry file in files)
        {
            PlanChoice choice = choices.GetValueOrDefault(file.Path) ?? new PlanChoice();
            if (choice.Excluded)
            {
                rows.Add(new PlanRow(file, "", "", "Ausgeschlossen", "", Excluded: true));
                continue;
            }

            string family = rule.Criteria.HasFlag(SortCriteria.NameFamily)
                ? families.GetValueOrDefault(file.Path, "Einzeldateien") : "";
            try
            {
                string folder = choice.Folder is null ? BuildFolder(root, rule, file, family) : NormalizeFolder(choice.Folder);
                string target = Path.Combine(folder, file.Name);
                if (!folderErrors.TryGetValue(folder, out string? error))
                    folderErrors[folder] = error = CheckFolder(folder);
                rows.Add(error is null
                    ? BuildRow(file, target, choice.Folder is null ? family : "Eigenes Ziel", action, choice, claimed)
                    : new PlanRow(file, target, family, "Blockiert", error, Blocked: true));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                rows.Add(new PlanRow(file, "", family, "Blockiert", error.Message, Blocked: true));
            }
        }
        return [.. rows];
    }

    private static Dictionary<string, string> NameFolders(FileEntry[] files, SortCriteria criteria)
    {
        var folders = new Dictionary<string, string>(InventoryPaths.Comparer);
        if (!criteria.HasFlag(SortCriteria.NameFamily))
            return folders;
        if (criteria.HasFlag(SortCriteria.FuzzyNames))
        {
            foreach (NameFamily family in NameSimilarity.Find(files, SimilarityLevel.Balanced))
            {
                foreach (NameMatch member in family.Members)
                    folders[member.File.Path] = family.Name;
            }
        }
        else
        {
            foreach (var group in files.GroupBy(file => NormalizeName(file.Name), InventoryPaths.Comparer).Where(group => group.Skip(1).Any()))
            {
                foreach (FileEntry file in group)
                    folders[file.Path] = group.Key;
            }
        }
        return folders;
    }

    private static string BuildFolder(string root, SortRule rule, FileEntry file, string family)
    {
        string folder = root;
        if (rule.Criteria.HasFlag(SortCriteria.Type))
            folder = Path.Combine(folder, file.Extension.Length == 0 ? "Ohne Endung" : SafeFolder(file.Extension[1..].ToLowerInvariant()));
        if (rule.Criteria.HasFlag(SortCriteria.Date))
        {
            DateTime preferred = rule.Date == SortDate.Modified ? file.ModifiedUtc : file.CreatedUtc;
            DateTime alternate = rule.Date == SortDate.Modified ? file.CreatedUtc : file.ModifiedUtc;
            if (preferred.Year <= 1601 && rule.MissingDate == MissingDate.OtherTimestamp)
                preferred = alternate;
            folder = preferred.Year <= 1601 ? Path.Combine(folder, "Ohne Datum")
                : Path.Combine(folder, preferred.ToLocalTime().ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
                    preferred.ToLocalTime().ToString("MM", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (rule.Criteria.HasFlag(SortCriteria.Size))
            folder = Path.Combine(folder, InventoryLabels.SizeClass(file.Bytes));
        if (rule.Criteria.HasFlag(SortCriteria.Pattern))
            folder = Path.Combine(folder, FileSystemName.MatchesSimpleExpression(rule.Pattern.Trim(), file.Name)
                ? "Passendes Namensmuster" : "Übrige Namen");
        if (rule.Criteria.HasFlag(SortCriteria.NameFamily))
            folder = Path.Combine(folder, SafeFolder(family));
        return folder;
    }

    private static PlanRow BuildRow(FileEntry file, string target, string family, string action,
        PlanChoice choice, Dictionary<string, FileEntry> claimed)
    {
        if (InventoryPaths.Comparer.Equals(file.Path, target))
            return new PlanRow(file, target, family, "Übersprungen", "Quelle liegt bereits am Ziel.", Excluded: true);

        if (choice.ConflictTarget is not null && !InventoryPaths.Comparer.Equals(choice.ConflictTarget, target))
            choice = choice with { Collision = CollisionAction.Pending, ResolvedTarget = null, ExpectedReplacement = null };

        FileAttributes? attributes = GetAttributes(target);
        bool planned = claimed.TryGetValue(target, out FileEntry? owner);
        bool collision = planned || attributes is not null;
        if (!collision && choice.Collision is CollisionAction.Pending or CollisionAction.Replace)
        {
            claimed.Add(target, file);
            return new PlanRow(file, target, family, action, "");
        }

        bool directory = attributes?.HasFlag(FileAttributes.Directory) == true;
        bool reparse = attributes?.HasFlag(FileAttributes.ReparsePoint) == true;
        SortConflictTarget targetDetails = DescribeConflictTarget(target, attributes, owner);
        string reason = targetDetails.Details;

        switch (choice.Collision)
        {
            case CollisionAction.Skip:
                return new PlanRow(file, target, family, "Übersprungen", reason, Collision: true, Excluded: true,
                    ConflictTarget: target, ConflictDetails: targetDetails);
            case CollisionAction.Replace when !planned && !directory && !reparse:
                claimed.Add(target, file);
                WindowsFileStamp? replacement;
                try { replacement = WindowsFileOperations.Probe(target); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return OpenConflict(file, target, family, $"Zieldatei lässt sich für Ersetzen nicht prüfen. {error.Message}", targetDetails);
                }
                if (replacement is null || (choice.ExpectedReplacement is not null && replacement != choice.ExpectedReplacement))
                    return OpenConflict(file, target, family, "Zieldatei hat sich seit der Entscheidung geändert. Bitte neu entscheiden.", targetDetails);
                return new PlanRow(file, target, family, $"{action} · Ersetzen",
                    $"Zieldatei vorhanden · {InventoryLabels.Size(replacement.Bytes)} · geändert {DateTime.FromFileTimeUtc(replacement.Written).ToLocalTime():g}",
                    Collision: true, Replace: true, Replacement: replacement, ConflictTarget: target,
                    ConflictDetails: targetDetails with { File = new FileEntry(target, replacement.Bytes,
                        DateTime.FromFileTimeUtc(replacement.Written), DateTime.FromFileTimeUtc(replacement.Created), replacement.Attributes) });
            case CollisionAction.KeepBoth:
            case CollisionAction.Affixes:
                string stem = Path.GetFileNameWithoutExtension(file.Name);
                if (choice.Collision == CollisionAction.Affixes)
                    stem = choice.Prefix + stem + choice.Suffix;
                try
                {
                    string free = choice.ResolvedTarget ?? FindFree(Path.GetDirectoryName(target)!, stem, file.Extension,
                        claimed, choice.Collision == CollisionAction.KeepBoth);
                    FileAttributes? freeAttributes = GetAttributes(free);
                    if (claimed.TryGetValue(free, out FileEntry? freeOwner) || freeAttributes is not null)
                    {
                        claimed.TryAdd(target, file);
                        return OpenConflict(file, target, family, $"Das gewählte Ziel ist inzwischen belegt: {free}",
                            DescribeConflictTarget(free, freeAttributes, freeOwner));
                    }
                    claimed.Add(free, file);
                    return new PlanRow(file, free, family, action, reason, Collision: true, ConflictTarget: target, ConflictDetails: targetDetails);
                }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    claimed.TryAdd(target, file);
                    return OpenConflict(file, target, family, error.Message, targetDetails);
                }
            default:
                claimed.TryAdd(target, file);
                bool canReplace = !planned && !directory && !reparse;
                return OpenConflict(file, target, family,
                    canReplace ? reason : $"{reason} Wähle einen anderen Namen oder überspringe den Eintrag.", targetDetails);
        }
    }

    private static PlanRow OpenConflict(FileEntry file, string target, string family, string reason, SortConflictTarget details) =>
        new(file, target, family, "Konflikt offen", reason, Blocked: true, Collision: true, ConflictTarget: target, ConflictDetails: details);

    private static string FindFree(string folder, string stem, string extension, Dictionary<string, FileEntry> claimed, bool numbered)
    {
        for (int number = numbered ? 2 : 1; ; number++)
        {
            string name = number == 1 ? stem + extension : $"{stem} ({number}){extension}";
            if (!IsValidName(name))
                throw new ArgumentException($"Ungültiger Windows-Dateiname: {name}");
            string candidate = Path.Combine(folder, name);
            if (!claimed.ContainsKey(candidate) && GetAttributes(candidate) is null)
                return candidate;
        }
    }

    private static SortConflictTarget DescribeConflictTarget(string target, FileAttributes? attributes, FileEntry? planned)
    {
        if (planned is not null)
            return new SortConflictTarget(target, planned, "Ziel bereits in diesem Plan eingeplant.");
        if (attributes is null)
            return new SortConflictTarget(target, null, "Gewählte Konfliktaktion beibehalten.");
        if (attributes.Value.HasFlag(FileAttributes.Directory))
            return new SortConflictTarget(target, null, "Am Ziel liegt ein Ordner.");
        if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
            return new SortConflictTarget(target, null, "Am Ziel liegt eine Verknüpfung.");

        try
        {
            FileEntry file = FileVersions.ReadEntry(target);
            return new SortConflictTarget(target, file,
                $"Zieldatei vorhanden · {InventoryLabels.Size(file.Bytes)} · geändert {file.ModifiedUtc.ToLocalTime():g}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new SortConflictTarget(target, null, "Angaben zur Zieldatei konnten nicht gelesen werden.");
        }
    }

    private static string NormalizeFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Der Zielordner muss ein absoluter Pfad sein.");
        foreach (string segment in folder[Path.GetPathRoot(folder)!.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsValidName(segment))
                throw new ArgumentException($"Ungültiger Windows-Ordnername: {segment}");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }

    private static string? CheckFolder(string folder)
    {
        for (string? path = folder; path is not null; path = Path.GetDirectoryName(path))
        {
            FileAttributes? attributes = GetAttributes(path);
            if (attributes?.HasFlag(FileAttributes.Directory) == false)
                return $"Ein Teil des Zielpfads ist eine Datei: {path}";
            if (attributes is null && Path.GetDirectoryName(path) is null)
                return $"Das Ziellaufwerk ist nicht erreichbar: {path}";
        }
        return null;
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static bool IsValidName(string name) => name.Length is > 0 and <= 255 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !name.EndsWith(' ') && !name.EndsWith('.') && !ReservedName().IsMatch(name);

    private static string NormalizeName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        while (true)
        {
            string shorter = VariantSuffix().Replace(stem, "");
            if (shorter.Length == 0 || shorter.Length == stem.Length)
                break;
            stem = shorter;
        }
        string normalized = Separators().Replace(stem, " ").Trim();
        return normalized.Length == 0 ? stem : normalized;
    }

    private static string SafeFolder(string value)
    {
        string name = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character))
            .TrimEnd(' ', '.');
        return IsValidName(name) ? name : name.Length == 0 ? "Ohne Namen" : "_" + name[..Math.Min(254, name.Length)];
    }
}
