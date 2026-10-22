using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Kairos.Settings;

namespace Kairos.App;

/// <summary>
/// The tray's right-click menu in the app's own look: the window's warm brown, light text, a rose-tinted highlight and
/// rounded corners (Windows 11), instead of Windows' white menu.
/// </summary>
public static class TrayMenu
{
    static readonly Color Back = Rgb(ThemeColors.Background);
    static readonly Color Line = Rgb(0x4E4241); // Theme.xaml's Stroke
    static readonly Color Ink = Rgb(0xF5ECEA); // TextPrimary
    static readonly Color Muted = Rgb(0xBBAAA7); // TextSecondary
    static readonly Color Highlight = Rgb(ThemeColors.Soft);
    static readonly Font MenuFont = new("Segoe UI", 9.75f);
    /// <summary>The offered update, first in the menu: bold, in the accent.</summary>
    internal static readonly Font AccentFont = new("Segoe UI", 9.75f, FontStyle.Bold);

    public static ContextMenuStrip Create(bool rightToLeft)
    {
        var menu = new ContextMenuStrip
        {
            RightToLeft = rightToLeft ? RightToLeft.Yes : RightToLeft.No,
            ShowImageMargin = false,
            Renderer = new Renderer(),
            BackColor = Back,
            ForeColor = Ink,
            Font = MenuFont,
            Padding = new Padding(4, 6, 4, 6),
        };
        menu.ItemAdded += (_, e) =>
        {
            if (e.Item is ToolStripMenuItem)
                e.Item.Padding = new Padding(10, 5, 10, 5);
        };
        menu.HandleCreated += (_, _) => RoundCorners(menu.Handle);
        return menu;
    }

    sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(Back);
            e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
                return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Measured from the menu's edges: items run under one side's padding (the right in English, the left in Arabic).
            int left = 5 - e.Item.Bounds.Left, right = (e.Item.Owner?.Width ?? e.Item.Width) - 5 - e.Item.Bounds.Left;
            using var path = GdiShapes.Rounded(new RectangleF(left, 1, right - left, e.Item.Height - 2), 4);
            using var fill = new SolidBrush(Highlight);
            e.Graphics.FillPath(fill, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ink : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10 - e.Item.Bounds.Left, y, (e.Item.Owner?.Width ?? e.Item.Width) - 10 - e.Item.Bounds.Left, y);
        }
    }

    /// <summary>Windows 11 rounds the menu and draws its edge in the theme's hairline; older Windows ignore both.</summary>
    static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        int edge = Line.R | Line.G << 8 | Line.B << 16; // COLORREF
        DwmSetWindowAttribute(hwnd, 34, ref edge, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    internal static Color Rgb(uint rgb) => Color.FromArgb((int)(rgb >> 16) & 0xFF, (int)(rgb >> 8) & 0xFF, (int)rgb & 0xFF);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
