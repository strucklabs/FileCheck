using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FilesizeChecker.Inventory;

internal static class LocalJsonFile
{
    public static T? Load<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type)
                ?? throw new InvalidDataException("Die gespeicherte Datei enthält keine Daten.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static void Save<T>(string path, T data, JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, type);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
