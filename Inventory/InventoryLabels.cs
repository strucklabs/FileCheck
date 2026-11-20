namespace FilesizeChecker.Inventory;

internal static class InventoryLabels
{
    private static readonly string[] SizeUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string Status(FileStatus status) => status switch
    {
        FileStatus.New => "Neu",
        FileStatus.Missing => "Fehlend",
        FileStatus.Changed => "Verändert",
        FileStatus.Unavailable => "Nicht erreichbar",
        _ => "Erfasst"
    };

    public static string Size(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N2} {SizeUnits[unit]}";
    }
}
