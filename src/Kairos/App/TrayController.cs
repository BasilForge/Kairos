using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kairos.App;

public enum TrayState { Starting, Active, Waiting, Paused, Recording, Error }

/// <summary>Tray icon and menu. Icons are drawn at runtime, so no image files ship with the app.</summary>
public sealed class TrayController : IDisposable
{
    readonly NotifyIcon _icon = new();
    readonly Dictionary<string, Icon> _icons = [];
    readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 1500 };
    TrayState _state = TrayState.Starting;
    string? _menuKey;

    public event Action? SaveClicked, QuickClicked, ScreenshotClicked, PauseClicked, RecordClicked, PauseRecordClicked, FolderClicked, ShareClicked, DeleteLastClicked, SettingsClicked, ExitClicked, UpdateClicked;
    /// <summary>A newer Kairos is offered: an amber dot on the icon and "Update to …" first in the menu.</summary>
    string? _update;

    public TrayController()
    {
        DrawIcons();
        _icon.Icon = _icons["waiting"];
        _icon.Text = Tooltip;
        _icon.Visible = true;
        _icon.DoubleClick += (_, _) => SettingsClicked?.Invoke();
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); ApplyIcon(); };
    }

    /// <summary>The tray icon's tooltip: just the name (the icon's color already tells the state).</summary>
    public static string Tooltip => Text.Get("app");

    /// <summary>Everything the menu's items depend on. The tooltip is not part of it.</summary>
    public static string MenuKey(TrayState state, bool quickEnabled, int quickSeconds, bool paused, bool recording, string language,
        bool canUndo = false, bool canShare = false, bool recordingPaused = false, bool lastIsShot = false, string? update = null) =>
        $"{state}|{quickEnabled}|{quickSeconds}|{paused}|{recording}|{language}|{canUndo}|{canShare}|{recordingPaused}|{lastIsShot}|{update}";

    /// <summary>Updates the icon; rebuilds the menu only when its items change (so an open menu stays open).</summary>
    /// <param name="canUndo">A clip saved in this session can still be sent to the Recycle Bin from the menu.</param>
    /// <param name="canShare">There is a last clip to open in the share panel.</param>
    /// <param name="recordingPaused">The full recording is paused: its menu item resumes it.</param>
    /// <param name="lastIsShot">The last thing saved is a screenshot: the share and delete items say so.</param>
    /// <param name="update">A newer Kairos's version, shown as offered; null when there is none.</param>
    public void Update(TrayState state, bool quickEnabled, int quickSeconds, bool paused, bool recording, bool canUndo = false,
        bool canShare = false, bool recordingPaused = false, bool lastIsShot = false, string? update = null)
    {
        _state = state;
        _update = update;
        _icon.Text = Tooltip;
        if (!_flashTimer.Enabled)
            ApplyIcon();

        string key = MenuKey(state, quickEnabled, quickSeconds, paused, recording, Text.Language, canUndo, canShare, recordingPaused, lastIsShot, update);
        if (key == _menuKey)
            return;
        _menuKey = key;

        var menu = TrayMenu.Create(Text.IsRightToLeft);
        if (update != null)
        {
            var item = new ToolStripMenuItem(Text.Format("tray.update", update), null, (_, _) => UpdateClicked?.Invoke())
            {
                ForeColor = TrayMenu.Rgb(Settings.ThemeColors.Accent),
                Font = TrayMenu.AccentFont,
            };
            menu.Items.Add(item);
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(Text.Get("tray.save"), null, (_, _) => SaveClicked?.Invoke());
        if (quickEnabled)
            menu.Items.Add(Text.Format("tray.quick", Text.Count("count.seconds", quickSeconds)), null, (_, _) => QuickClicked?.Invoke());
        menu.Items.Add(Text.Get("tray.screenshot"), null, (_, _) => ScreenshotClicked?.Invoke());
        // While a full recording runs, pause is for the recording (as on the Status page).
        if (recording)
            menu.Items.Add(Text.Get(recordingPaused ? "tray.resumeRecord" : "tray.pauseRecord"), null, (_, _) => PauseRecordClicked?.Invoke());
        else
            menu.Items.Add(Text.Get(paused ? "tray.resume" : "tray.pause"), null, (_, _) => PauseClicked?.Invoke());
        menu.Items.Add(Text.Get(recording ? "tray.recordStop" : "tray.recordStart"), null, (_, _) => RecordClicked?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Text.Get("tray.openFolder"), null, (_, _) => FolderClicked?.Invoke());
        if (canShare)
            menu.Items.Add(Text.Get(lastIsShot ? "tray.shareLastShot" : "tray.share"), null, (_, _) => ShareClicked?.Invoke());
        if (canUndo)
            menu.Items.Add(Text.Get(lastIsShot ? "tray.deleteLastShot" : "tray.deleteLast"), null, (_, _) => DeleteLastClicked?.Invoke());
        menu.Items.Add(Text.Get("tray.settings"), null, (_, _) => SettingsClicked?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Text.Get("tray.exit"), null, (_, _) => ExitClicked?.Invoke());
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    void DrawIcons()
    {
        foreach (string kind in new[] { "active", "waiting", "paused", "recording", "error", "saved" })
            _icons[kind] = DrawIcon(kind);
    }

    /// <summary>Active and recording use the theme color; saved, error and idle keep their meaning colors.</summary>
    public static uint ColorFor(string kind) => kind switch
    {
        "active" or "recording" => Settings.ThemeColors.Accent,
        "saved" => Settings.ThemeColors.Saved,
        "error" => Settings.ThemeColors.Warning,
        _ => 0xA3928F,
    };

    /// <summary>Briefly shows the green "saved" icon.</summary>
    public void FlashSaved()
    {
        _icon.Icon = _icons["saved"];
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    void ApplyIcon()
    {
        string kind = _state switch
        {
            TrayState.Active => "active",
            TrayState.Paused => "paused",
            TrayState.Recording => "recording",
            TrayState.Error => "error",
            _ => "waiting",
        };
        if (_update != null)
        {
            // With the update's dot: drawn the first time one is offered (most sessions never need them).
            string dotted = kind + "+update";
            if (!_icons.TryGetValue(dotted, out var icon))
                _icons[dotted] = icon = DrawIcon(kind, updateDot: true);
            _icon.Icon = icon;
        }
        else
        {
            _icon.Icon = _icons[kind];
        }
    }

    static Icon DrawIcon(string kind, bool updateDot = false)
    {
        using var bmp = DrawBitmap(kind, updateDot);
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    /// <summary>The 32 px tray picture for <paramref name="kind"/>: the app's mark in the state's color.</summary>
    /// <param name="updateDot">A newer Kairos is offered: an amber dot in the top corner.</param>
    public static Bitmap DrawBitmap(string kind, bool updateDot = false)
    {
        const int size = 32;
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(255, 46, 39, 38)))
            using (var path = GdiShapes.Rounded(new RectangleF(1, 1, size - 2, size - 2), 8))
                g.FillPath(bg, path);

            Color accent = Color.FromArgb(unchecked((int)(0xFF000000 | ColorFor(kind))));
            using var pen = new Pen(accent, 3f);
            AppIconArt.DrawRing(g, pen, new RectangleF(6, 6, 20, 20));
            using var brush = new SolidBrush(accent);
            switch (kind)
            {
                case "paused":
                    g.FillRectangle(brush, 12, 11, 3, 10);
                    g.FillRectangle(brush, 17, 11, 3, 10);
                    break;
                case "recording":
                    g.FillEllipse(brush, 10, 10, 12, 12);
                    break;
                default:
                    g.FillEllipse(brush, 12, 12, 8, 8);
                    break;
            }
            if (updateDot)
            {
                // Amber (the theme's Warning), ringed in the icon's background so it reads on any taskbar.
                using var ring = new SolidBrush(TrayMenu.Rgb(Settings.ThemeColors.Background));
                using var dot = new SolidBrush(TrayMenu.Rgb(Settings.ThemeColors.Warning));
                g.FillEllipse(ring, 19, 0, 13, 13);
                g.FillEllipse(dot, 21, 2, 9, 9);
            }
        }
        return bmp;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _flashTimer.Dispose();
        foreach (var i in _icons.Values)
            i.Dispose();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}
