using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace FilesizeChecker.Inventory;

internal sealed record ContentDifference(string Location, string Change, string Left, string Right);
internal sealed record ContentComparison(string Summary, IReadOnlyList<ContentDifference> Differences, int Total);

internal static class DocumentComparison
{
    private const int MaximumTextBytes = 12 * 1024 * 1024;
    private const int DisplayLimit = 2_000;
    private const int PdfPageLimit = 1_000;

    public static ContentComparison Compare(FileEntry first, FileEntry second, CancellationToken cancellation = default)
    {
        string? kind = Kind(first.Extension);
        if (kind is null || kind != Kind(second.Extension))
            return new ContentComparison("Für diese Formatkombination steht die Versions- und Metadatenprüfung zur Verfügung.", [], 0);
        try
        {
            Document left = Read(first, kind, cancellation);
            Document right = Read(second, kind, cancellation);
            if (kind == "pdf" && left.Items.Values.Concat(right.Items.Values).All(string.IsNullOrWhiteSpace))
                return new ContentComparison("Kein auslesbarer PDF-Seitentext. Bildbasierte Seiten benötigen OCR.", [], 0);

            var differences = new ChangeList();
            bool positionBased = false;
            if (left.Items.Count > 0 || right.Items.Count > 0)
                CompareItems(left.Items, right.Items, differences, cancellation);
            else
                positionBased = CompareLines(left.Lines, right.Lines, left.Unit, differences, cancellation);

            string summary = differences.Total == 0 ? $"{left.Label}: keine Unterschiede gefunden."
                : $"{left.Label}: {differences.Total:N0} Unterschiede.";
            if (positionBased)
                summary += " Der lange veränderte Abschnitt wurde nach Position verglichen.";
            if (differences.Total > differences.Rows.Count)
                summary += $" Die ersten {DisplayLimit:N0} Befunde werden angezeigt.";
            return new ContentComparison(summary, differences.Rows, differences.Total);
        }
        catch (Exception error) when (FileVersions.IsReadError(error))
        {
            return new ContentComparison($"Inhalt konnte nicht verglichen werden. {FileVersions.DescribeReadError(error)}", [], 0);
        }
    }

    private static string? Kind(string extension)
    {
        string lower = extension.ToLowerInvariant();
        return FileVersions.OfficeKind(lower) ?? (FileVersions.IsText(lower) ? "text" : lower is ".pdf" or ".odt" ? lower[1..] : null);
    }

    private sealed record Document(string Label, string Unit, string[] Lines, Dictionary<string, string> Items);

    private static Document Read(FileEntry file, string kind, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using FileStream stream = FileVersions.OpenUnchanged(file);
        if (kind == "text")
            return new Document("Textzeilen", "Zeile", ReadLines(stream), []);
        if (kind == "pdf")
        {
            using var pdf = FileVersions.OpenPdf(stream);
            if (pdf.NumberOfPages > PdfPageLimit)
                throw new InvalidDataException($"Der PDF-Seitenvergleich verarbeitet bis zu {PdfPageLimit:N0} Seiten je Datei.");
            var pages = new Dictionary<string, string>(StringComparer.Ordinal);
            long textLength = 0;
            foreach (var page in pdf.GetPages())
            {
                cancellation.ThrowIfCancellationRequested();
                string text = ContentOrderTextExtractor.GetText(page).Trim();
                textLength += text.Length;
                if (textLength > MaximumTextBytes)
                    throw new InvalidDataException("Der extrahierte PDF-Text überschreitet die Vergleichsgrenze.");
                pages[$"Seite {page.Number:D4}"] = text;
            }
            return new Document("PDF-Seitentext", "Seite", [], pages);
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return kind switch
        {
            "word" => new Document("Word-Absatztext", "Absatz", ReadWord(archive), []),
            "excel" => new Document("Excel-Blätter, Zellwerte und Formeln", "Zelle", [], ReadExcel(archive, cancellation)),
            "powerpoint" => new Document("PowerPoint-Folientext", "Folie", [], ReadSlides(archive, cancellation)),
            _ => new Document("OpenDocument-Absatztext", "Absatz", ReadOpenDocument(archive), [])
        };
    }

    private static string[] ReadLines(Stream stream)
    {
        if (stream.Length > MaximumTextBytes)
            throw new InvalidDataException("Der Textvergleich unterstützt Dateien bis 12 MiB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, leaveOpen: true);
        string text;
        try { text = reader.ReadToEnd(); }
        catch (DecoderFallbackException)
        {
            stream.Position = 0;
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;
            using var legacy = new StreamReader(stream, encoding, true, leaveOpen: true);
            text = legacy.ReadToEnd();
        }
        if (text.Contains('\0'))
            throw new InvalidDataException("Die Datei enthält binäre Daten oder eine nicht erkannte Textkodierung.");
        return text.ReplaceLineEndings("\n").Split('\n');
    }

    private static string[] ReadWord(ZipArchive zip)
    {
        XElement body = RequiredPart(zip, "word/document.xml").Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "body")
            ?? throw new InvalidDataException("Haupttext des Word-Dokuments fehlt.");
        return body.Descendants().Where(element => element.Name.LocalName == "p")
            .Select(paragraph => string.Concat(paragraph.Descendants().Where(element => element.Name.LocalName is "t" or "tab" or "br")
                .Select(element => element.Name.LocalName switch { "tab" => "\t", "br" => "\n", _ => element.Value }))).ToArray();
    }

    private static string[] ReadOpenDocument(ZipArchive zip)
    {
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        return RequiredPart(zip, "content.xml").Descendants()
            .Where(element => element.Name == text + "p" || element.Name == text + "h")
            .Select(element => element.Value).ToArray();
    }

    private static Dictionary<string, string> ReadExcel(ZipArchive zip, CancellationToken cancellation)
    {
        XDocument workbook = RequiredPart(zip, "xl/workbook.xml");
        var relationships = Relationships(zip, "xl/workbook.xml");
        string[] shared = FileVersions.ReadPart(zip, "xl/sharedStrings.xml")?.Root?.Elements()
            .Where(element => element.Name.LocalName == "si").Select(RichText).ToArray() ?? [];
        var cells = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement sheet in workbook.Descendants().Where(element => element.Name.LocalName == "sheet"))
        {
            cancellation.ThrowIfCancellationRequested();
            string name = (string?)sheet.Attribute("name") ?? throw new InvalidDataException("Excel-Blattname fehlt.");
            string path = ResolvePart(sheet, relationships);
            cells[$"Blatt «{name}»"] = "Vorhanden";
            foreach (XElement cell in RequiredPart(zip, path).Descendants().Where(element => element.Name.LocalName == "c"))
            {
                cancellation.ThrowIfCancellationRequested();
                string? address = (string?)cell.Attribute("r");
                if (address is null)
                    continue;
                string? type = (string?)cell.Attribute("t");
                string? value = Local(cell, "v")?.Value;
                XElement? formula = Local(cell, "f");
                if (type == "s")
                {
                    if (!int.TryParse(value, out int index) || (uint)index >= (uint)shared.Length)
                        throw new InvalidDataException($"Gemeinsamer Zelltext fehlt für {name}!{address}.");
                    value = shared[index];
                }
                else if (type == "inlineStr")
                    value = RichText(cell);
                else if (type == "b")
                    value = value == "1" ? "WAHR" : "FALSCH";

                if (formula is not null)
                {
                    string content = formula.Value;
                    if (content.Length == 0 && (string?)formula.Attribute("t") == "shared")
                        content = $"[gemeinsame Formel {(string?)formula.Attribute("si")}]";
                    value = value is null ? $"={content}" : $"={content} → {value}";
                }
                if (value is not null)
                    cells[$"{name}!{address}"] = value;
                if (cells.Count > 250_000)
                    throw new InvalidDataException("Der Excel-Vergleich verarbeitet bis zu 250.000 belegte Zellen je Datei.");
            }
        }
        return cells;
    }

    private static Dictionary<string, string> ReadSlides(ZipArchive zip, CancellationToken cancellation)
    {
        XDocument presentation = RequiredPart(zip, "ppt/presentation.xml");
        var relationships = Relationships(zip, "ppt/presentation.xml");
        var slides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement slide in presentation.Descendants().Where(element => element.Name.LocalName == "sldId"))
        {
            cancellation.ThrowIfCancellationRequested();
            XDocument part = RequiredPart(zip, ResolvePart(slide, relationships));
            slides[$"Folie {slides.Count + 1:D4}"] = string.Join('\n', part.Descendants().Where(element => element.Name.LocalName == "p").Select(RichText));
        }
        return slides;
    }

    private static Dictionary<string, string> Relationships(ZipArchive zip, string part)
    {
        int separator = part.LastIndexOf('/');
        XDocument relationship = RequiredPart(zip, part[..(separator + 1)] + "_rels/" + part[(separator + 1)..] + ".rels");
        var origin = new Uri("https://package.invalid/" + part);
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement element in relationship.Root?.Elements() ?? [])
        {
            string? id = (string?)element.Attribute("Id");
            string? target = (string?)element.Attribute("Target");
            if (id is not null && target is not null && (string?)element.Attribute("TargetMode") != "External"
                && Uri.TryCreate(origin, target, out Uri? resolved) && resolved.Scheme == origin.Scheme && resolved.Host == origin.Host)
                results[id] = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
        }
        return results;
    }

    private static string ResolvePart(XElement element, Dictionary<string, string> relationships)
    {
        string? id = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id" && attribute.Name.NamespaceName.Length > 0)?.Value;
        return id is not null && relationships.TryGetValue(id, out string? path) ? path
            : throw new InvalidDataException("Interner Office-Dokumentteil ist nicht verfügbar.");
    }

    private static XDocument RequiredPart(ZipArchive zip, string part) => FileVersions.ReadPart(zip, part)
        ?? throw new InvalidDataException($"Dokumentteil fehlt: {part}");

    private static XElement? Local(XElement element, string name) => element.Elements().FirstOrDefault(child => child.Name.LocalName == name);

    private static string RichText(XElement element) => string.Concat(element.Descendants()
        .Where(child => child.Name.LocalName == "t" && !child.Ancestors().Any(parent => parent.Name.LocalName == "rPh"))
        .Select(child => child.Value));

    private static void CompareItems(Dictionary<string, string> first, Dictionary<string, string> second, ChangeList changes,
        CancellationToken cancellation)
    {
        foreach (string key in first.Keys.Concat(second.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            changes.Add(key, first.GetValueOrDefault(key), second.GetValueOrDefault(key));
        }
    }

    private static bool CompareLines(string[] first, string[] second, string unit, ChangeList changes, CancellationToken cancellation)
    {
        int start = 0;
        while (start < first.Length && start < second.Length && first[start] == second[start])
            start++;
        int a = first.Length;
        int b = second.Length;
        while (a > start && b > start && first[a - 1] == second[b - 1])
        {
            a--;
            b--;
        }

        int width = b - start + 1;
        if (a == start || b == start || (long)(a - start + 1) * width > 4_000_000)
        {
            CompareSegment(first, second, unit, start, a, start, b, changes, cancellation);
            return a != start && b != start;
        }

        var matches = new int[(a - start + 1) * width];
        for (int i = a - 1; i >= start; i--)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = (i - start) * width;
            for (int j = b - 1; j >= start; j--)
            {
                int at = row + j - start;
                matches[at] = first[i] == second[j] ? matches[at + width + 1] + 1
                    : Math.Max(matches[at + 1], matches[at + width]);
            }
        }

        int previousA = start;
        int previousB = start;
        int left = start;
        int right = start;
        while (left < a && right < b)
        {
            cancellation.ThrowIfCancellationRequested();
            if (first[left] == second[right])
            {
                CompareSegment(first, second, unit, previousA, left, previousB, right, changes, cancellation);
                previousA = ++left;
                previousB = ++right;
            }
            else
            {
                int index = (left - start) * width + right - start;
                if (matches[index + width] >= matches[index + 1])
                    left++;
                else
                    right++;
            }
        }
        CompareSegment(first, second, unit, previousA, a, previousB, b, changes, cancellation);
        return false;
    }

    private static void CompareSegment(string[] first, string[] second, string unit, int left, int leftEnd,
        int right, int rightEnd, ChangeList changes, CancellationToken cancellation)
    {
        while (left < leftEnd || right < rightEnd)
        {
            cancellation.ThrowIfCancellationRequested();
            string a = left < leftEnd ? $"L {left + 1}" : "";
            string b = right < rightEnd ? $"R {right + 1}" : "";
            changes.Add($"{unit} {string.Join(" / ", new[] { a, b }.Where(value => value.Length != 0))}",
                left < leftEnd ? first[left++] : null, right < rightEnd ? second[right++] : null);
        }
    }

    private sealed class ChangeList
    {
        public List<ContentDifference> Rows { get; } = [];
        public int Total { get; private set; }

        public void Add(string key, string? first, string? second)
        {
            if (first == second)
                return;
            Total++;
            if (Rows.Count < DisplayLimit)
                Rows.Add(new ContentDifference(key, first is null ? "Hinzugefügt" : second is null ? "Entfernt" : "Geändert",
                    first ?? "", second ?? ""));
        }
    }
}
