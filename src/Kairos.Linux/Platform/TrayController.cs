using Avalonia;
using Avalonia.Controls;
using Kairos.Platform;
using SkiaSharp;

namespace Kairos.App;

public enum TrayState { Starting, Active, Waiting, Paused, Recording, Error }

/// <summary>
/// Tray icon and menu (StatusNotifierItem + DBusMenu through Avalonia). Icons are drawn at runtime, so no image files
/// ship with the app.
/// </summary>
public sealed class TrayController : IDisposable
{
    readonly TrayIcon _icon = new();
    readonly Dictionary<string, WindowIcon> _icons = [];
    readonly UiTimer _flashTimer = new(1500);
    TrayState _state = TrayState.Starting;
    string? _menuKey;

    public event Action? SaveClicked, QuickClicked, ScreenshotClicked, PauseClicked, RecordClicked, PauseRecordClicked, FolderClicked, ShareClicked, DeleteLastClicked, SettingsClicked, ExitClicked;

    public TrayController()
    {
        foreach (string kind in new[] { "active", "waiting", "paused", "recording", "error", "saved" })
            _icons[kind] = DrawIcon(kind);
        _icon.Icon = _icons["waiting"];
        _icon.ToolTipText = Tooltip;
        _icon.Menu = new NativeMenu();
        _icon.Clicked += (_, _) => SettingsClicked?.Invoke();
        _flashTimer.Tick += () => { _flashTimer.Stop(); ApplyIcon(); };
        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

    public static string Tooltip => Text.Get("app");

    /// <summary>Everything the menu's items depend on. The tooltip is not part of it.</summary>
    public static string MenuKey(TrayState state, bool quickEnabled, int quickSeconds, bool paused, bool recording, string language,
        bool canUndo = false, bool canShare = false, bool recordingPaused = false, bool lastIsShot = false) =>
        $"{state}|{quickEnabled}|{quickSeconds}|{paused}|{recording}|{language}|{canUndo}|{canShare}|{recordingPaused}|{lastIsShot}";

    public void Update(TrayState state, bool quickEnabled, int quickSeconds, bool paused, bool recording, bool canUndo = false,
        bool canShare = false, bool recordingPaused = false, bool lastIsShot = false)
    {
        _state = state;
        _icon.ToolTipText = Tooltip;
        if (!_flashTimer.Enabled)
            ApplyIcon();

        string key = MenuKey(state, quickEnabled, quickSeconds, paused, recording, Text.Language, canUndo, canShare, recordingPaused, lastIsShot);
        if (key == _menuKey)
            return;
        _menuKey = key;

        var menu = new NativeMenu();
        void Add(string header, Action? click)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => click?.Invoke();
            menu.Items.Add(item);
        }
        Add(Text.Get("tray.save"), SaveClicked);
        if (quickEnabled)
            Add(Text.Format("tray.quick", Text.Count("count.seconds", quickSeconds)), QuickClicked);
        Add(Text.Get("tray.screenshot"), ScreenshotClicked);
        // While a full recording runs, pause is for the recording (as on the Status page).
        if (recording)
            Add(Text.Get(recordingPaused ? "tray.resumeRecord" : "tray.pauseRecord"), PauseRecordClicked);
        else
            Add(Text.Get(paused ? "tray.resume" : "tray.pause"), PauseClicked);
        Add(Text.Get(recording ? "tray.recordStop" : "tray.recordStart"), RecordClicked);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(Text.Get("tray.openFolder"), FolderClicked);
        if (canShare)
            Add(Text.Get(lastIsShot ? "tray.shareLastShot" : "tray.share"), ShareClicked);
        if (canUndo)
            Add(Text.Get(lastIsShot ? "tray.deleteLastShot" : "tray.deleteLast"), DeleteLastClicked);
        Add(Text.Get("tray.settings"), SettingsClicked);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(Text.Get("tray.exit"), ExitClicked);
        _icon.Menu = menu;
    }

    /// <summary>Active and recording use the theme color; saved, error and idle keep their meaning colors.</summary>
    public static uint ColorFor(string kind) => kind switch
    {
        "active" or "recording" => Settings.ThemeColors.Accent,
        "saved" => 0x2ED47A,
        "error" => 0xFFB020,
        _ => 0xA3928F,
    };

    /// <summary>Briefly shows the green "saved" icon.</summary>
    public void FlashSaved()
    {
        _icon.Icon = _icons["saved"];
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    void ApplyIcon() => _icon.Icon = _icons[_state switch
    {
        TrayState.Active => "active",
        TrayState.Paused => "paused",
        TrayState.Recording => "recording",
        TrayState.Error => "error",
        _ => "waiting",
    }];

    static WindowIcon DrawIcon(string kind)
    {
        using var bmp = DrawBitmap(kind);
        return new WindowIcon(new MemoryStream(AppIconArt.Png(bmp)));
    }

    /// <summary>The 32 px tray picture for <paramref name="kind"/>: the app's mark in the state's color.</summary>
    public static SKBitmap DrawBitmap(string kind)
    {
        const int size = 32;
        var bmp = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(46, 39, 38) })
            canvas.DrawRoundRect(new SKRect(1, 1, size - 1, size - 1), 8, 8, bg);

        var accent = AppIconArt.Opaque(ColorFor(kind));
        using (var pen = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = 3, Color = accent })
            AppIconArt.DrawRing(canvas, pen, new SKRect(6, 6, 26, 26));
        using var brush = new SKPaint { IsAntialias = true, Color = accent };
        switch (kind)
        {
            case "paused":
                canvas.DrawRect(new SKRect(12, 11, 15, 21), brush);
                canvas.DrawRect(new SKRect(17, 11, 20, 21), brush);
                break;
            case "recording":
                canvas.DrawOval(new SKRect(10, 10, 22, 22), brush);
                break;
            default:
                canvas.DrawOval(new SKRect(12, 12, 20, 20), brush);
                break;
        }
        return bmp;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
        _flashTimer.Dispose();
    }
}
