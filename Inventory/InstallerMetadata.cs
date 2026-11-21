using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FilesizeChecker.Inventory;

internal static class InstallerMetadata
{
    public static Dictionary<string, string> ReadMsi(string path)
    {
        Check(OpenDatabase(path, IntPtr.Zero, out uint database));
        try
        {
            const string query = "SELECT `Property`, `Value` FROM `Property` WHERE `Property` = 'ProductVersion' OR `Property` = 'ProductName' OR `Property` = 'UpgradeCode'";
            Check(OpenView(database, query, out uint view));
            try
            {
                Check(ExecuteView(view, 0));
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    uint result = Fetch(view, out uint record);
                    if (result == 259)
                        return values;
                    Check(result);
                    try { values[Read(record, 1)] = Read(record, 2); }
                    finally { Close(record); }
                }
            }
            finally { Close(view); }
        }
        finally { Close(database); }
    }

    private static string Read(uint record, uint field)
    {
        uint size = 512;
        var value = new StringBuilder((int)size);
        uint result = GetString(record, field, value, ref size);
        if (result == 234 && size < 8_192)
        {
            size++;
            value = new StringBuilder((int)size);
            result = GetString(record, field, value, ref size);
        }
        Check(result);
        return value.ToString();
    }

    private static void Check(uint code)
    {
        if (code != 0)
            throw new IOException($"Windows-Installer-Metadaten konnten nicht gelesen werden ({code}).",
                new Win32Exception((int)code));
    }

    [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode)]
    private static extern uint OpenDatabase(string path, IntPtr persist, out uint database);

    [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode)]
    private static extern uint OpenView(uint database, string query, out uint view);

    [DllImport("msi.dll", EntryPoint = "MsiViewExecute")]
    private static extern uint ExecuteView(uint view, uint record);

    [DllImport("msi.dll", EntryPoint = "MsiViewFetch")]
    private static extern uint Fetch(uint view, out uint record);

    [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode)]
    private static extern uint GetString(uint record, uint field, StringBuilder value, ref uint size);

    [DllImport("msi.dll", EntryPoint = "MsiCloseHandle")]
    private static extern uint Close(uint handle);
}
