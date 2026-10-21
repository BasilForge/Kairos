using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Kairos.Ui;

/// <summary>
/// An outline icon (Lucide, 24 x 24 grid) drawn in the text colour around it. <see cref="Glyph"/> is the Windows glyph code
/// the XAML was written with ("E7BA"), or an icon name ("triangle-alert"); size follows FontSize like the font glyph did.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Glyph));
    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<Icon>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(GlyphProperty, ForegroundProperty);
        AffectsMeasure<Icon>(FontSizeProperty);
    }

    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The picture is a little larger than the font glyph it replaces: an icon font's em box has air around the drawing.</summary>
    double Side => FontSize * 1.15;

    protected override Size MeasureOverride(Size availableSize) => new(Side, Side);

    public override void Render(DrawingContext context)
    {
        if (Glyph is not { Length: > 0 } glyph)
            return;
        // The shared view models give a glyph as the Windows icon-font character itself ("\uE7A8"), the XAML as its code ("E7A8").
        if (glyph.Length == 1 && glyph[0] is >= '\uE000' and <= '\uF8FF')
            glyph = ((int)glyph[0]).ToString("X4");
        string name = IconMap.Glyphs.TryGetValue(glyph.ToUpperInvariant(), out var mapped) ? mapped : glyph;
        if (!this.TryFindResource("icon-" + name, ActualThemeVariant, out var found) || found is not Geometry geometry)
            return;
        double scale = Side / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, new Pen(Foreground ?? Brushes.White, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }

    /// <summary>Draws an icon straight into a drawing context (the timeline paints its row and bar marks this way), <paramref name="side"/> px square.</summary>
    public static void Draw(DrawingContext context, string glyph, double side, IBrush brush, Point at)
    {
        if (glyph.Length == 1 && glyph[0] is >= '\uE000' and <= '\uF8FF')
            glyph = ((int)glyph[0]).ToString("X4");
        string name = IconMap.Glyphs.TryGetValue(glyph.ToUpperInvariant(), out var mapped) ? mapped : glyph;
        if (Application.Current?.TryFindResource("icon-" + name, Avalonia.Styling.ThemeVariant.Dark, out var found) != true || found is not Geometry geometry)
            return;
        double scale = side / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
            context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }
}
