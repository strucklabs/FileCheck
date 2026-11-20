using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesizeChecker.Inventory;

[JsonSerializable(typeof(InventorySnapshot))]
internal partial class InventoryJson : JsonSerializerContext;

internal sealed class InventoryStore(string filePath)
{
    public InventorySnapshot? Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(filePath);
            return JsonSerializer.Deserialize(stream, InventoryJson.Default.InventorySnapshot)
                ?? throw new InvalidDataException("Das gespeicherte Inventar ist leer.");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Save(InventorySnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temporaryPath = filePath + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, InventoryJson.Default.InventorySnapshot);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

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
