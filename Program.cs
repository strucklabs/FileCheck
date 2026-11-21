using FilesizeChecker.Inventory;

namespace FilesizeChecker;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\CheckTools.FilesizeChecker", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("FileCheck ist bereits geöffnet.", "FileCheck");
            return;
        }

        string dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CheckTools", "FilesizeChecker");
        Application.Run(new InventoryForm(new InventoryStore(Path.Combine(dataFolder, "inventory.json"))));
    }
}
