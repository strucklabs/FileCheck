namespace FilesizeChecker;

internal static class FileCheckTheme
{
    public static readonly Color Accent = Color.FromArgb(20, 88, 113);
    public static Color SelectionBackground => SystemInformation.HighContrast ? SystemColors.Highlight : Accent;
    public static Color SelectionForeground => SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White;

    private static readonly ToolStripRenderer menuRenderer = new ContextMenuRenderer();

    public static ContextMenuStrip CreateContextMenu() => new() { Renderer = menuRenderer };

    private sealed class ContextMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!SystemInformation.HighContrast && e.Item.Selected && e.Item.Enabled)
            {
                using var brush = new SolidBrush(SelectionBackground);
                e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
                return;
            }
            base.OnRenderMenuItemBackground(e);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!SystemInformation.HighContrast && e.Item.Selected && e.Item.Enabled)
                e.TextColor = SelectionForeground;
            base.OnRenderItemText(e);
        }
    }
}
