using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kairos.App;

/// <summary>
/// The "clip saved" card in the top-right corner. A click-through layered window that never takes focus;
/// it fades in, holds, and fades out. Not shown over exclusive-fullscreen games or presentations.
/// </summary>
/// <summary>
/// The toast's mark: saved (a green check), something that did not work (an amber "!"), an update (a rose arrow), a tip
/// (a rose screen: something the user may change, nothing wrong).
/// </summary>
public enum ToastIcon { Saved, Warning, Update, Tip }

/// <summary>
/// How a card comes and goes (seconds, and how far it slides in at 100 %): <see cref="Usual"/> for what Kairos just did,
/// <see cref="Calm"/> for a tip shown over a game, which comes in slower and from further so it never startles, and
/// stays long enough to read two lines.
/// </summary>
public sealed record ToastTiming(double FadeIn, double Hold, double FadeOut, int SlidePx)
{
    public static readonly ToastTiming Usual = new(SaveToast.FadeIn, SaveToast.Hold, SaveToast.FadeOut, 14);
    public static readonly ToastTiming Calm = new(0.45, 7, 0.6, 28);

    public double Total => FadeIn + Hold + FadeOut;
}

public sealed class SaveToast : NativeWindow, IDisposable
{
    public const double FadeIn = 0.18, Hold = 2.2, FadeOut = 0.35;
    public const double Total = FadeIn + Hold + FadeOut;
    const int BaseWidth = 300, BaseHeight = 64, BaseMargin = 16;

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    Bitmap? _image;
    Point _pos;
    float _scale = 1;
    DateTime _shownAt;
    ToastTiming _timing = ToastTiming.Usual;

    /// <summary>Self-test hook: draw here (off-screen) instead of the top-right corner.</summary>
    public Point? TestPosition { get; set; }

    public bool ExcludedFromCapture { get; }

    /// <summary>Frames presented since the last Show, and whether the last one succeeded.</summary>
    public int Frames { get; private set; }
    public bool LastFrameOk { get; private set; }

    public SaveToast()
    {
        CreateHandle(new CreateParams
        {
            Style = unchecked((int)0x80000000),                      // WS_POPUP
            ExStyle = 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8,     // LAYERED, TRANSPARENT, TOOLWINDOW, NOACTIVATE, TOPMOST
        });
        // Seen on screen only: kept out of Kairos's own clips and recordings, and out of streams and screenshots.
        ExcludedFromCapture = SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004 and later)
        _timer.Tick += (_, _) => Animate();
    }

    public static Point Place(Rectangle workArea, Size size, int margin) =>
        new(workArea.Right - size.Width - margin, workArea.Top + margin);

    /// <summary>Opacity 0..1 at <paramref name="t"/> seconds after showing.</summary>
    public static double Opacity(double t) => Opacity(t, ToastTiming.Usual);

    /// <summary>Opacity 0..1 at <paramref name="t"/> seconds after showing, eased in and out as <paramref name="timing"/> says.</summary>
    public static double Opacity(double t, ToastTiming timing)
    {
        if (t <= 0 || t >= timing.Total)
            return 0;
        if (t < timing.FadeIn)
            return 1 - Math.Pow(1 - t / timing.FadeIn, 3);
        if (t < timing.FadeIn + timing.Hold)
            return 1;
        double k = (t - timing.FadeIn - timing.Hold) / timing.FadeOut;
        return 1 - k * k * k;
    }

    /// <summary>Windows says a game holds the screen in full screen (QUNS_RUNNING_D3D_FULL_SCREEN).</summary>
    public static bool FullScreenGame => NotificationState() == 3;

    /// <summary>An exclusive full-screen game or presentation mode: Windows asks apps to show no card now.</summary>
    public static bool FullScreenBusy => NotificationState() is 3 or 4;

    static int NotificationState() => SHQueryUserNotificationState(out int state) == 0 ? state : 0;

    /// <summary>Shows (or restarts) the card on the monitor of the foreground window.</summary>
    /// <param name="icon">The green check (saved), an amber "!" (something did not work), a rose arrow (an update) or a rose screen (a tip).</param>
    /// <param name="timing"><see cref="ToastTiming.Usual"/> when null.</param>
    /// <param name="overGame">
    /// Shown over a full-screen game too (a tip the player must see before a clip is lost): the card never takes focus,
    /// and Windows composes it over the game. Never over presentation mode.
    /// </param>
    /// <returns>False when nothing showed (an exclusive-fullscreen game or presentation mode).</returns>
    public bool Show(string title, string subtitle, bool rtl, ToastIcon icon = ToastIcon.Saved, ToastTiming? timing = null, bool overGame = false)
    {
        // What the user was told, in the log (someone saying "nothing happened" may have been told why).
        string said = subtitle.Length > 0 ? $"{title} | {subtitle}" : title;
        if (NotificationState() is var state && (state == 4 || (state == 3 && !overGame)))
        {
            Engine.ObsLog.Info($"toast (not shown: a full-screen game or presentation): {said}");
            return false; // exclusive-fullscreen game or presentation mode
        }
        Engine.ObsLog.Info("toast: " + said);

        IntPtr monitor = MonitorFromWindow(GetForegroundWindow(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        _scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;

        _image?.Dispose();
        _image = Paint(title, subtitle, rtl, _scale, icon);
        var work = Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        _pos = TestPosition ?? Place(work, _image.Size, (int)(BaseMargin * _scale));
        _shownAt = DateTime.UtcNow;
        _timing = timing ?? ToastTiming.Usual;
        Frames = 0;
        Animate();
        ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
        SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE NOMOVE NOACTIVATE */);
        _timer.Start();
        return true;
    }

    void Animate()
    {
        double t = (DateTime.UtcNow - _shownAt).TotalSeconds;
        double opacity = Opacity(t, _timing);
        if (t >= _timing.Total || _image == null)
        {
            _timer.Stop();
            ShowWindow(Handle, 0 /* SW_HIDE */);
            return;
        }
        int slide = t < _timing.FadeIn ? (int)((1 - opacity) * _timing.SlidePx * _scale) : 0;
        Present(_image, new Point(_pos.X + slide, _pos.Y), (byte)Math.Round(opacity * 255));
    }

    /// <summary>The card's edge padding, its icon's size and the gap between icon and text, at 100 %.</summary>
    const float Pad = 16, IconSize = 30, IconGap = 12;

    static float TextWidth(int cardWidth, float scale) => cardWidth - (Pad * 2 + IconSize + IconGap) * scale;
    static Font TitleFont(float scale) => new("Segoe UI Semibold", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Point);
    static Font SubtitleFont(float scale) => new("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>The line under the title may take a second line: the card grows by this much (at 100 %).</summary>
    const float SecondLine = 16;

    /// <summary>
    /// Whether a title and the line under it show whole on the card: the title on one line, the line under it on up to
    /// two (more ends in "…").
    /// </summary>
    public static bool Fits(string title, string subtitle, float scale = 1) => Measure(g =>
    {
        using var titleFont = TitleFont(scale);
        using var subFont = SubtitleFont(scale);
        float width = TextWidth((int)(BaseWidth * scale), scale);
        return g.MeasureString(title, titleFont).Width <= width
            && g.MeasureString(subtitle, subFont, (int)width).Height < subFont.GetHeight(g) * 2.5; // two lines, with GDI+'s padding
    });

    /// <summary>The line under the title needs a second line on a card at <paramref name="scale"/>.</summary>
    static bool Wraps(string subtitle, float scale) => Measure(g =>
    {
        using var subFont = SubtitleFont(scale);
        return g.MeasureString(subtitle, subFont).Width > TextWidth((int)(BaseWidth * scale), scale);
    });

    /// <summary>Text measured as the card draws it: on a 96-dpi bitmap (the screen's own scale would be counted twice).</summary>
    static T Measure<T>(Func<Graphics, T> measure)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return measure(g);
    }

    /// <summary>Draws the card. Shared with --render-toast so it can be checked without showing a window.</summary>
    public static Bitmap Paint(string title, string subtitle, bool rtl, float scale, ToastIcon icon = ToastIcon.Saved)
    {
        bool twoLines = Wraps(subtitle, scale);
        int w = (int)(BaseWidth * scale), h = (int)((BaseHeight + (twoLines ? SecondLine : 0)) * scale);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        float r = 12 * scale;
        using (var path = GdiShapes.Rounded(new RectangleF(0.5f, 0.5f, w - 1, h - 1), r))
        using (var bg = new SolidBrush(Color.FromArgb(245, 58, 49, 48)))
        using (var border = new Pen(Color.FromArgb(255, 98, 84, 83), 1))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        float pad = Pad * scale, size = IconSize * scale;
        float iconX = rtl ? w - pad - size : pad;
        float iconY = (h - size) / 2;
        using (var disc = new SolidBrush(TrayMenu.Rgb(icon switch
               {
                   ToastIcon.Warning => Settings.ThemeColors.Warning,
                   ToastIcon.Update or ToastIcon.Tip => Settings.ThemeColors.Accent,
                   _ => Settings.ThemeColors.Saved,
               })))
            g.FillEllipse(disc, iconX, iconY, size, size);
        using (var mark = new Pen(TrayMenu.Rgb(icon == ToastIcon.Tip ? Settings.ThemeColors.OnAccent : Settings.ThemeColors.Background),
                   (icon == ToastIcon.Tip ? 2.2f : 2.6f) * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            if (icon == ToastIcon.Tip)
            {
                // A screen on its stand: the game's display mode
                using (var screen = GdiShapes.Rounded(new RectangleF(iconX + size * 0.27f, iconY + size * 0.29f, size * 0.46f, size * 0.32f), 2.5f * scale))
                    g.DrawPath(mark, screen);
                g.DrawLine(mark, iconX + size * 0.5f, iconY + size * 0.62f, iconX + size * 0.5f, iconY + size * 0.71f);
                g.DrawLine(mark, iconX + size * 0.39f, iconY + size * 0.72f, iconX + size * 0.61f, iconY + size * 0.72f);
            }
            else if (icon == ToastIcon.Update)
            {
                // An arrow down: a stroke and its head
                g.DrawLine(mark, iconX + size * 0.5f, iconY + size * 0.27f, iconX + size * 0.5f, iconY + size * 0.68f);
                g.DrawLines(mark,
                [
                    new PointF(iconX + size * 0.33f, iconY + size * 0.52f),
                    new PointF(iconX + size * 0.5f, iconY + size * 0.69f),
                    new PointF(iconX + size * 0.67f, iconY + size * 0.52f),
                ]);
            }
            else if (icon == ToastIcon.Warning)
            {
                // "!": a stroke and a dot
                g.DrawLine(mark, iconX + size * 0.5f, iconY + size * 0.26f, iconX + size * 0.5f, iconY + size * 0.56f);
                g.DrawLine(mark, iconX + size * 0.5f, iconY + size * 0.74f, iconX + size * 0.5f, iconY + size * 0.745f);
            }
            else
            {
                g.DrawLines(mark,
                [
                    new PointF(iconX + size * 0.28f, iconY + size * 0.52f),
                    new PointF(iconX + size * 0.44f, iconY + size * 0.67f),
                    new PointF(iconX + size * 0.73f, iconY + size * 0.36f),
                ]);
            }
        }

        float textLeft = rtl ? pad : pad + size + IconGap * scale;
        float textWidth = TextWidth(w, scale);
        var format = new StringFormat(rtl ? StringFormatFlags.DirectionRightToLeft : 0)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = (rtl ? StringFormatFlags.DirectionRightToLeft : 0) | StringFormatFlags.NoWrap,
        };
        using var titleFont = TitleFont(scale);
        using var subFont = SubtitleFont(scale);
        using var white = new SolidBrush(Color.FromArgb(255, 245, 236, 234));
        using var grey = new SolidBrush(Color.FromArgb(255, 187, 170, 167));
        g.DrawString(title, titleFont, white, new RectangleF(textLeft, 12 * scale, textWidth, 22 * scale), format);
        if (twoLines)
        {
            format.FormatFlags = (format.FormatFlags & ~StringFormatFlags.NoWrap) | StringFormatFlags.LineLimit;
            format.Trimming = StringTrimming.EllipsisWord;
        }
        g.DrawString(subtitle, subFont, grey, new RectangleF(textLeft, 34 * scale, textWidth, (20 + (twoLines ? SecondLine : 0)) * scale), format);
        return bmp;
    }

    void Present(Bitmap image, Point pos, byte alpha)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = image.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { cx = image.Width, cy = image.Height };
            var src = new POINT();
            var dst = new POINT { x = pos.X, y = pos.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            LastFrameOk = UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            Frames++;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _image?.Dispose();
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
