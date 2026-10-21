using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kairos.Engine.Edit;
using TextEffect = Kairos.Engine.Edit.TextEffect;

namespace Kairos.Ui.Edit;

/// <summary>
/// Draws an edit's items. The editor's preview and the export use this same drawing (the export as one transparent PNG
/// per item at the clip's size), so what is seen is what is saved. Blur boxes are not drawn here: FFmpeg blurs them.
/// </summary>
public static class ItemRenderer
{
    static readonly IBrush Backing = new SolidColorBrush(Color.FromArgb(0xB8, 0x14, 0x10, 0x10)).ToImmutable();

    public static void Draw(DrawingContext dc, EditItem item, System.Windows.Size frame) => Draw(dc, item, new Avalonia.Size(frame.Width, frame.Height));

    static void Draw(DrawingContext dc, EditItem item, Avalonia.Size frame)
    {
        switch (item)
        {
            case TextItem text:
                DrawText(dc, text, frame);
                break;
            case StrokeItem drawing:
                foreach (var stroke in drawing.Strokes.Where(s => s.Count > 0))
                    DrawStroke(dc, stroke, drawing.ColorHex, drawing.ThicknessRel, frame);
                break;
            case ShapeItem shape:
                DrawShape(dc, shape, frame);
                break;
        }
    }

    /// <summary>A control that draws one item, translated so that <c>(x0, y0)</c> of the frame is its top left.</summary>
    sealed class OverlayVisual(EditItem item, Avalonia.Size frame, int x0, int y0) : Control
    {
        public override void Render(DrawingContext context)
        {
            using (context.PushTransform(Matrix.CreateTranslation(-x0, -y0)))
                Draw(context, item, frame);
        }
    }

    /// <summary>
    /// One item on a transparent picture just big enough for it (drawn at the clip's size), written as PNG into
    /// <paramref name="folder"/>, with where it goes in the frame. Call on the UI thread.
    /// </summary>
    public static Overlay RenderPng(EditItem item, int width, int height, string folder)
    {
        Directory.CreateDirectory(folder);
        var frame = new Avalonia.Size(width, height);
        var bounds = Extent(item, frame);
        int x0 = 0, y0 = 0, x1 = 2, y1 = 2;
        if (bounds is { } b)
        {
            // A pixel of room for anti-aliasing, kept inside the frame.
            x0 = Math.Clamp((int)Math.Floor(b.Left) - 1, 0, width - 1);
            y0 = Math.Clamp((int)Math.Floor(b.Top) - 1, 0, height - 1);
            x1 = Math.Clamp((int)Math.Ceiling(b.Right) + 1, x0 + 1, width);
            y1 = Math.Clamp((int)Math.Ceiling(b.Bottom) + 1, y0 + 1, height);
        }
        int w = x1 - x0, h = y1 - y0;
        var visual = new OverlayVisual(item, frame, x0, y0) { Width = w, Height = h };
        visual.Measure(new Avalonia.Size(w, h));
        visual.Arrange(new Avalonia.Rect(0, 0, w, h));
        using var bitmap = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        bitmap.Render(visual);
        string path = Path.Combine(folder, item.Id.ToString("N") + ".png");
        bitmap.Save(path);
        return new Overlay(path, x0, y0);
    }

    /// <summary>
    /// Where the item is drawn, with room for its effects (halo, shadow, outline) and line width; a little more than it needs is
    /// harmless, as it only sizes the picture the item is rendered into.
    /// </summary>
    static Avalonia.Rect? Extent(EditItem item, Avalonia.Size frame)
    {
        switch (item)
        {
            case TextItem text:
            {
                double size = Math.Max(1, text.SizeRel * frame.Height);
                return Scale(text.Box, frame).Inflate(size * 0.6);
            }
            case StrokeItem drawing:
            {
                var points = drawing.Strokes.SelectMany(s => s).ToList();
                if (points.Count == 0)
                    return null;
                double pad = Math.Max(1, drawing.ThicknessRel * frame.Height);
                double l = points.Min(p => p.X) * frame.Width, t = points.Min(p => p.Y) * frame.Height;
                double r = points.Max(p => p.X) * frame.Width, b = points.Max(p => p.Y) * frame.Height;
                return new Avalonia.Rect(l, t, r - l, b - t).Inflate(pad);
            }
            case ShapeItem shape:
            {
                double pad = Math.Max(1, shape.ThicknessRel * frame.Height) * 3; // an arrow's head reaches beside the line
                double l = Math.Min(shape.From.X, shape.To.X) * frame.Width, t = Math.Min(shape.From.Y, shape.To.Y) * frame.Height;
                double r = Math.Max(shape.From.X, shape.To.X) * frame.Width, b = Math.Max(shape.From.Y, shape.To.Y) * frame.Height;
                return new Avalonia.Rect(l, t, r - l, b - t).Inflate(pad);
            }
            default:
                return null;
        }
    }

    /// <summary>The text laid out at its size (its box is fitted to this by the editor).</summary>
    public static FormattedText Layout(TextItem text, System.Windows.Size frame) =>
        new(text.Text.Length == 0 ? " " : text.Text, CultureInfo.CurrentUICulture,
            IsRightToLeft(text.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, TextFonts.For(text.Font).Typeface,
            Math.Max(1, text.SizeRel * frame.Height), Brush(text.ColorHex));

    static FormattedText Layout(TextItem text, Avalonia.Size frame) => Layout(text, new System.Windows.Size(frame.Width, frame.Height));

    /// <summary>The box a text needs at its size, with room around it, as shares of the picture (at most all of it).</summary>
    public static System.Windows.Size BoxSize(TextItem text, System.Windows.Size frame)
    {
        var layout = Layout(text with { Text = text.Text.Length == 0 ? "Aa" : text.Text }, frame);
        double pad = text.SizeRel * frame.Height * 0.5;
        return new System.Windows.Size(Math.Min(1, (layout.WidthIncludingTrailingWhitespace + pad * 2) / frame.Width),
            Math.Min(1, (layout.Height + pad) / frame.Height));
    }

    static void DrawText(DrawingContext dc, TextItem text, Avalonia.Size frame)
    {
        var box = Scale(text.Box, frame);
        if (text.Effect == TextEffect.Box)
            dc.DrawRectangle(Backing, null, box, box.Height * 0.18, box.Height * 0.18);
        else if (text.Effect == TextEffect.Pill)
            dc.DrawRectangle(Backing, null, box, box.Height / 2, box.Height / 2);
        var layout = Layout(text, frame);
        layout.TextAlignment = TextAlignment.Center;
        layout.MaxTextWidth = Math.Max(1, box.Width);
        // Both directions lay out inside MaxTextWidth from the box's left edge (Arabic is placed right-to-left within it).
        var origin = new Avalonia.Point(box.Left, box.Top + (box.Height - layout.Height) / 2);
        double size = Math.Max(1, text.SizeRel * frame.Height);
        // Dark round light letters, light round dark ones.
        var edge = IsLight(text.ColorHex) ? Colors.Black : Colors.White;
        switch (text.Effect)
        {
            case TextEffect.Shadow:
                double offset = Math.Max(1, size * 0.07);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xD8, edge.R, edge.G, edge.B)).ToImmutable(), null,
                    layout.BuildGeometry(new Avalonia.Point(origin.X + offset, origin.Y + offset))!);
                break;
            case TextEffect.Outline:
                var pen = new Pen(new SolidColorBrush(edge).ToImmutable(), Math.Max(1, size * 0.14), lineJoin: PenLineJoin.Round);
                dc.DrawGeometry(null, pen, layout.BuildGeometry(origin)!);
                break;
            case TextEffect.Glow:
                Halo(dc, layout.BuildGeometry(origin)!, Colour(text.ColorHex), size, 0.6);
                break;
            case TextEffect.Neon:
            {
                // A tube of light: the colour's halo around letters lit from inside, edged in the colour.
                var colour = Colour(text.ColorHex);
                var letters = layout.BuildGeometry(origin)!;
                Halo(dc, letters, colour, size, 1);
                var tube = new Pen(new SolidColorBrush(colour).ToImmutable(), Math.Max(1, size * 0.05), lineJoin: PenLineJoin.Round);
                dc.DrawGeometry(new SolidColorBrush(Lighten(colour, 0.6)).ToImmutable(), tube, letters);
                return;
            }
        }
        dc.DrawText(layout, origin);
    }

    /// <summary>
    /// A soft light around the letters in their colour: strokes of widening width, each fainter. Drawn, not an effect,
    /// so the export's pictures match the preview exactly.
    /// </summary>
    static void Halo(DrawingContext dc, Geometry letters, Color colour, double size, double strength)
    {
        foreach (var (width, alpha) in new[] { (0.44, 0.10), (0.32, 0.16), (0.21, 0.24), (0.11, 0.34) })
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha * strength), colour.R, colour.G, colour.B)).ToImmutable(),
                Math.Max(1, size * width), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            dc.DrawGeometry(null, pen, letters);
        }
    }

    static Color Lighten(Color c, double amount) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    static Color Colour(string colorHex) => Color.TryParse(colorHex, out var colour) ? colour : Colors.White;

    static void DrawStroke(DrawingContext dc, IReadOnlyList<System.Windows.Point> stroke, string colorHex, double thicknessRel, Avalonia.Size frame)
    {
        var pen = Pen(colorHex, thicknessRel, frame);
        var points = stroke.Select(p => new Avalonia.Point(p.X * frame.Width, p.Y * frame.Height)).ToList();
        if (points.Count == 1)
        {
            dc.DrawEllipse(pen.Brush, null, points[0], pen.Thickness / 2, pen.Thickness / 2);
            return;
        }
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], false);
            foreach (var point in points.Skip(1))
                g.LineTo(point);
            g.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }

    static void DrawShape(DrawingContext dc, ShapeItem shape, Avalonia.Size frame)
    {
        var pen = Pen(shape.ColorHex, shape.ThicknessRel, frame);
        var from = new Avalonia.Point(shape.From.X * frame.Width, shape.From.Y * frame.Height);
        var to = new Avalonia.Point(shape.To.X * frame.Width, shape.To.Y * frame.Height);
        var r = new Avalonia.Rect(new Avalonia.Point(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y)), new Avalonia.Point(Math.Max(from.X, to.X), Math.Max(from.Y, to.Y)));
        switch (shape.Kind)
        {
            case ShapeKind.Rectangle:
                dc.DrawRectangle(null, pen, r);
                break;
            case ShapeKind.Ellipse:
                dc.DrawEllipse(null, pen, new Avalonia.Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            default:
                DrawArrow(dc, pen, from, to);
                break;
        }
    }

    /// <summary>A line with a filled head at <paramref name="to"/>, about five line widths long.</summary>
    static void DrawArrow(DrawingContext dc, Pen pen, Avalonia.Point from, Avalonia.Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, span = Math.Sqrt(dx * dx + dy * dy);
        if (span < 1e-6)
            return;
        dx /= span;
        dy /= span;
        double length = Math.Max(12, pen.Thickness * 5), half = length * 0.6;
        var baseCentre = new Avalonia.Point(to.X - dx * length, to.Y - dy * length);
        var a = new Avalonia.Point(baseCentre.X - dy * half, baseCentre.Y + dx * half);
        var b = new Avalonia.Point(baseCentre.X + dy * half, baseCentre.Y - dx * half);
        dc.DrawLine(pen, from, baseCentre);
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(a);
            g.LineTo(b);
            g.EndFigure(true);
        }
        dc.DrawGeometry(pen.Brush, null, head);
    }

    static Pen Pen(string colorHex, double thicknessRel, Avalonia.Size frame) =>
        new(Brush(colorHex), Math.Max(1, thicknessRel * frame.Height), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    static IBrush Brush(string colorHex) => new SolidColorBrush(Colour(colorHex)).ToImmutable();

    static Avalonia.Rect Scale(System.Windows.Rect box, Avalonia.Size frame) =>
        new(box.X * frame.Width, box.Y * frame.Height, box.Width * frame.Width, box.Height * frame.Height);

    static bool IsLight(string hex)
    {
        var c = Colour(hex);
        return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 110;
    }

    static bool IsRightToLeft(string text) => text.Any(c => c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFC');
}
