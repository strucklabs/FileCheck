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

    public static string SizeClass(long bytes) => bytes switch
    {
        < 1_024 => "Unter 1 KiB",
        < 1_048_576 => "1 KiB bis unter 1 MiB",
        < 104_857_600 => "1 MiB bis unter 100 MiB",
        < 1_073_741_824 => "100 MiB bis unter 1 GiB",
        _ => "Ab 1 GiB"
    };
}
