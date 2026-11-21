using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace FilesizeChecker.Inventory;

internal enum VersionKind { Filename, File, Product, Assembly, Package, Document, Revision, Model, Technical, Date, Hint }
internal sealed record VersionEvidence(VersionKind Kind, string Label, string Value, string? Scope = null);
internal sealed record VersionReport(FileEntry File, IReadOnlyList<VersionEvidence> Evidence, string? Issue)
{
    public string InternalSummary => string.Join(" · ", Evidence.Where(item => item.Kind is >= VersionKind.File and <= VersionKind.Model)
        .Select(item => $"{item.Label} {item.Value}"));
}

internal static partial class FileVersions
{
    private const long MaximumPdfSize = 96L * 1024 * 1024;
    private const int MaximumXmlSize = 16 * 1024 * 1024;
    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".yaml", ".yml", ".json", ".xml", ".ini", ".log", ".cfg", ".html", ".htm", ".css", ".cs", ".ps1"
    };

    [GeneratedRegex("^\\s*(?:(?:[#;]|//)\\s*)?[\"']?(?<label>version|revision)[\"']?\\s*[:=]\\s*[\"']?(?<value>[0-9vV][0-9A-Za-z.+_\\- ]{0,80})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderVersion();

    public static VersionReport Inspect(FileEntry file)
    {
        NameClues name = NameSimilarity.Analyze(file.Name);
        var evidence = new List<VersionEvidence>();
        Add(evidence, VersionKind.Filename, "Dateiname", name.Version);
        Add(evidence, VersionKind.Date, "Datum im Namen", name.Date);
        Add(evidence, VersionKind.Hint, "Namenszusatz", name.Marker);
        string? issue = null;
        try
        {
            string extension = file.Extension.ToLowerInvariant();
            if (SupportsMetadata(extension))
            {
                using FileStream stream = OpenUnchanged(file);
                ReadMetadata(file, extension, stream, evidence);
            }
        }
        catch (Exception error) when (IsReadError(error))
        {
            issue = DescribeReadError(error);
        }
        return new VersionReport(file, evidence, issue);
    }

    public static FileEntry ReadEntry(string path)
    {
        var file = new FileInfo(path);
        return new FileEntry(file.FullName, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, file.Attributes);
    }

    private static bool SupportsMetadata(string extension) => extension is ".exe" or ".dll" or ".ocx" or ".sys" or ".cpl"
        or ".msi" or ".msix" or ".appx" or ".msixbundle" or ".appxbundle" or ".nupkg"
        or ".safetensors" or ".pdf" or ".odt" or ".ods" or ".odp" || OfficeKind(extension) is not null || PlainText.Contains(extension);

    private static void ReadMetadata(FileEntry file, string extension, FileStream stream, List<VersionEvidence> evidence)
    {
        switch (extension)
        {
            case ".exe": case ".dll": case ".ocx": case ".sys": case ".cpl":
                ReadProgramVersions(file.Path, stream, evidence);
                break;
            case ".msi":
                Dictionary<string, string> msi = InstallerMetadata.ReadMsi(file.Path);
                Add(evidence, VersionKind.Package, "MSI-Produktversion", msi.GetValueOrDefault("ProductVersion"),
                    msi.GetValueOrDefault("UpgradeCode") ?? msi.GetValueOrDefault("ProductName"));
                Add(evidence, VersionKind.Hint, "Produkt", msi.GetValueOrDefault("ProductName"));
                break;
            case ".safetensors":
                ReadModelMetadata(stream, evidence);
                break;
            case ".pdf":
                using (PdfDocument pdf = OpenPdf(stream))
                {
                    Add(evidence, VersionKind.Technical, "PDF-Format", pdf.Version.ToString(CultureInfo.InvariantCulture));
                    if (pdf.TryGetXmpMetadata(out var xmp))
                    {
                        ReadOnlyMemory<byte> bytes = xmp.GetXmlMemory();
                        if (bytes.Length > MaximumXmlSize)
                            throw new InvalidDataException("PDF-Metadaten sind zu groß.");
                        using var metadata = new MemoryStream(bytes.ToArray(), writable: false);
                        XDocument xml = ReadXml(metadata);
                        XNamespace schema = "http://ns.adobe.com/xap/1.0/";
                        Add(evidence, VersionKind.Document, "XMP-Version", XmlField(xml, schema + "VersionID"));
                        Add(evidence, VersionKind.Date, "XMP-Änderungsdatum", XmlField(xml, schema + "ModifyDate"));
                    }
                }
                break;
            case ".msix": case ".appx": case ".msixbundle": case ".appxbundle": case ".nupkg":
            case ".odt": case ".ods": case ".odp":
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
                    ReadPackageMetadata(zip, extension, evidence);
                break;
            default:
                if (OfficeKind(extension) is not null)
                {
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    XDocument? core = ReadPart(zip, "docProps/core.xml");
                    XNamespace properties = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
                    XNamespace terms = "http://purl.org/dc/terms/";
                    Add(evidence, VersionKind.Document, "Office-Version", core?.Root?.Element(properties + "version")?.Value);
                    Add(evidence, VersionKind.Revision, "Office-Revision", core?.Root?.Element(properties + "revision")?.Value);
                    Add(evidence, VersionKind.Date, "Im Dokument geändert", core?.Root?.Element(terms + "modified")?.Value);
                }
                else
                {
                    ReadTextMetadata(stream, extension, evidence);
                }
                break;
        }
    }

    private static void ReadTextMetadata(Stream stream, string extension, List<VersionEvidence> evidence)
    {
        if (extension == ".json" && stream.Length is > 0 and <= 1024 * 1024)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
            using JsonDocument metadata = JsonDocument.Parse(reader.ReadToEnd());
            if (metadata.RootElement.ValueKind != JsonValueKind.Object)
                return;
            foreach (JsonProperty property in metadata.RootElement.EnumerateObject())
            {
                bool revision = property.Name.Equals("revision", StringComparison.OrdinalIgnoreCase);
                if (!revision && !property.Name.Equals("version", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null
                };
                Add(evidence, revision ? VersionKind.Revision : VersionKind.Document,
                    revision ? "JSON-Revision" : "JSON-Version", value);
            }
            return;
        }

        string header = ReadTextPrefix(stream);
        foreach (string line in header.Split('\n').Take(32))
        {
            Match match = HeaderVersion().Match(line);
            if (!match.Success)
                continue;
            bool revision = match.Groups["label"].Value.Equals("revision", StringComparison.OrdinalIgnoreCase);
            Add(evidence, revision ? VersionKind.Revision : VersionKind.Document,
                revision ? "Text-Revision" : "Text-Version", match.Groups["value"].Value);
        }
    }

    private static void ReadProgramVersions(string path, Stream stream, List<VersionEvidence> evidence)
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
        Add(evidence, VersionKind.File, "Dateiversion", info.FileVersion, info.OriginalFilename);
        Add(evidence, VersionKind.Product, "Produktversion", info.ProductVersion, info.ProductName);
        Add(evidence, VersionKind.Hint, "Produkt", info.ProductName);
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (reader.HasMetadata)
        {
            MetadataReader metadata = reader.GetMetadataReader();
            if (metadata.IsAssembly)
            {
                AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
                Add(evidence, VersionKind.Assembly, ".NET-Assembly", assembly.Version.ToString(), metadata.GetString(assembly.Name));
            }
        }
    }

    private static void ReadPackageMetadata(ZipArchive zip, string extension, List<VersionEvidence> evidence)
    {
        if (extension == ".nupkg")
        {
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(part => !part.FullName.Contains('/') && part.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            XElement? metadata = entry is null ? null : ReadPart(zip, entry.FullName)?.Root?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "metadata");
            Add(evidence, VersionKind.Package, "NuGet-Paketversion", NamedValue(metadata, "version"), NamedValue(metadata, "id"));
            Add(evidence, VersionKind.Hint, "Paket-ID", NamedValue(metadata, "id"));
        }
        else if (extension is ".odt" or ".ods" or ".odp")
        {
            XDocument? xml = ReadPart(zip, "meta.xml");
            XNamespace meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            Add(evidence, VersionKind.Hint, "OpenDocument-Bearbeitungszyklen", xml?.Descendants(meta + "editing-cycles").FirstOrDefault()?.Value);
            Add(evidence, VersionKind.Date, "Im Dokument geändert", xml?.Descendants(dc + "date").FirstOrDefault()?.Value);
            foreach (XElement field in xml?.Descendants(meta + "user-defined") ?? [])
            {
                if (string.Equals((string?)field.Attribute(meta + "name"), "Version", StringComparison.OrdinalIgnoreCase))
                    Add(evidence, VersionKind.Document, "OpenDocument-Version", field.Value);
            }
        }
        else
        {
            string part = extension.EndsWith("bundle", StringComparison.Ordinal) ? "AppxMetadata/AppxBundleManifest.xml" : "AppxManifest.xml";
            XElement? identity = ReadPart(zip, part)?.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "Identity");
            Add(evidence, VersionKind.Package, "App-Paketversion", (string?)identity?.Attribute("Version"), (string?)identity?.Attribute("Name"));
            Add(evidence, VersionKind.Hint, "Paket-ID", (string?)identity?.Attribute("Name"));
        }
    }

    private static void ReadModelMetadata(Stream stream, List<VersionEvidence> evidence)
    {
        Span<byte> prefix = stackalloc byte[8];
        stream.ReadExactly(prefix);
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
        if (size == 0 || size > 4 * 1024 * 1024 || size > (ulong)(stream.Length - 8))
            throw new InvalidDataException("Safetensors-Kopf fehlt oder überschreitet die Metadatengrenze.");
        var buffer = new byte[(int)size];
        stream.ReadExactly(buffer);
        using JsonDocument header = JsonDocument.Parse(buffer);
        if (header.RootElement.ValueKind != JsonValueKind.Object || !header.RootElement.TryGetProperty("__metadata__", out JsonElement metadata)
            || metadata.ValueKind != JsonValueKind.Object)
            return;
        foreach (JsonProperty field in metadata.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.String)
                continue;
            string label = field.Name.ToLowerInvariant();
            if (label is "version" or "model_version" or "modelspec.version" or "ss_output_name" or "ss_base_model_version" or "modelspec.architecture")
            {
                VersionKind kind = label is "version" or "model_version" ? VersionKind.Model
                    : label == "modelspec.version" ? VersionKind.Technical : VersionKind.Hint;
                Add(evidence, kind, field.Name, field.Value.GetString());
            }
        }
    }

    private static string? NamedValue(XElement? root, string name) => root?.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    private static string? XmlField(XDocument document, XName name) => document.Descendants(name).FirstOrDefault()?.Value
        ?? document.Descendants().Attributes(name).FirstOrDefault()?.Value;

    private static void Add(List<VersionEvidence> evidence, VersionKind kind, string label, string? value, string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        string cleaned = value.Trim();
        var item = new VersionEvidence(kind, label, cleaned[..Math.Min(cleaned.Length, 240)], scope?.Trim());
        if (!evidence.Contains(item))
            evidence.Add(item);
    }

    public static string? OfficeKind(string extension) => extension switch
    {
        ".docx" or ".docm" => "word",
        ".xlsx" or ".xlsm" => "excel",
        ".pptx" or ".pptm" => "powerpoint",
        _ => null
    };

    public static bool IsText(string extension) => PlainText.Contains(extension);

    public static FileStream OpenUnchanged(FileEntry file)
    {
        if (File.GetAttributes(file.Path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Verknüpfte Dateien werden hier übersprungen.");
        var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024);
        try
        {
            if (stream.Length != file.Bytes || File.GetLastWriteTimeUtc(stream.SafeFileHandle) != file.ModifiedUtc
                || File.GetCreationTimeUtc(stream.SafeFileHandle) != file.CreatedUtc)
                throw new IOException("Datei hat sich seit dem Inventarscan geändert. Aktualisiere das Inventar.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static PdfDocument OpenPdf(Stream stream)
    {
        if (stream.Length > MaximumPdfSize)
            throw new InvalidDataException("PDF überschreitet die Grenze von 96 MiB für den Inhalts- und Metadatenleser.");
        return PdfDocument.Open(stream);
    }

    public static XDocument? ReadPart(ZipArchive zip, string path)
    {
        ZipArchiveEntry? entry = zip.GetEntry(path);
        if (entry is null)
            return null;
        if (entry.Length > MaximumXmlSize)
            throw new InvalidDataException($"Dokumentteil {path} überschreitet die Grenze von 16 MiB.");
        using Stream stream = entry.Open();
        return ReadXml(stream);
    }

    private static XDocument ReadXml(Stream stream)
    {
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlSize
        });
        return XDocument.Load(reader);
    }

    private static string ReadTextPrefix(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, bufferSize: 4096, leaveOpen: true);
        var chars = new char[8192];
        int count = reader.ReadBlock(chars, 0, chars.Length);
        return new string(chars, 0, count);
    }

    public static bool IsReadError(Exception error) => error is IOException or UnauthorizedAccessException or XmlException
        or InvalidDataException or JsonException or BadImageFormatException or ArgumentException or NotSupportedException
        or InvalidOperationException or FormatException or OverflowException or System.ComponentModel.Win32Exception
        || error.GetType().Namespace?.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal) == true;

    public static string DescribeReadError(Exception error) => error.Message.Length <= 350 ? error.Message : error.Message[..350];
}
