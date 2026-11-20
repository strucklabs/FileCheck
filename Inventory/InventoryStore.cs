using System.Text;
using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

[JsonSerializable(typeof(InventorySnapshot))]
internal partial class InventoryJson : JsonSerializerContext;

internal sealed class InventoryStore(string filePath)
{
    public string DataFolder => Path.GetDirectoryName(filePath)!;

    public InventorySnapshot? Load() => LocalJsonFile.Load(filePath, InventoryJson.Default.InventorySnapshot);

    public void Save(InventorySnapshot snapshot) => LocalJsonFile.Save(filePath, snapshot, InventoryJson.Default.InventorySnapshot);

    public static void ExportCsv(string path, IEnumerable<FileResult> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("Status;Dateiname;Pfad;Typ;Bytes;Geändert (UTC);Erstellt (UTC);Attribute;Details");
        foreach (FileResult result in rows)
        {
            FileEntry file = result.File;
            writer.WriteLine(string.Join(';',
                Csv(InventoryLabels.Status(result.Status)), Csv(file.Name), Csv(file.Path), Csv(file.Extension),
                file.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{file.ModifiedUtc:O}", $"{file.CreatedUtc:O}", Csv(file.Attributes.ToString()), Csv(result.Details)));
        }
    }

    private static string Csv(string value)
    {
        ReadOnlySpan<char> trimmed = value.AsSpan().TrimStart();
        if (!trimmed.IsEmpty && "=+-@".Contains(trimmed[0]))
            value = "'" + value;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
