using Avalonia.Media;

namespace Kairos.Ui.Edit;

/// <param name="Key">What a text item keeps (<see cref="Engine.Edit.TextItem.Font"/>).</param>
public sealed record TextFont(string Key, string Label, FontFamily Family, FontWeight Weight)
{
    public Typeface Typeface { get; } = new(Family, FontStyle.Normal, Weight);
}

/// <summary>
/// The text tool's fonts: the system's sans-serif, and nine free fonts that write Arabic and English (SIL Open Font License,
/// from Google Fonts), built into the program.
/// </summary>
public static class TextFonts
{
    /// <summary>The key a text item keeps for the plain system font (Segoe UI on Windows, where the project began).</summary>
    public const string Default = "segoe";

    public static IReadOnlyList<TextFont> All { get; } =
    [
        new(Default, "Sans", new FontFamily("Inter, Noto Sans, Cantarell, Ubuntu, sans-serif"), FontWeight.SemiBold),
        Shipped("almarai", "Almarai", FontWeight.ExtraBold),
        Shipped("lalezar", "Lalezar", FontWeight.Normal),
        Shipped("rakkas", "Rakkas", FontWeight.Normal),
        Shipped("ruqaa", "Aref Ruqaa", FontWeight.Bold),
        Shipped("cairo", "Cairo", FontWeight.Bold),
        Shipped("tajawal", "Tajawal", FontWeight.Bold),
        Shipped("changa", "Changa", FontWeight.SemiBold),
        Shipped("reemkufi", "Reem Kufi", FontWeight.Normal),
        Shipped("lemonada", "Lemonada", FontWeight.SemiBold),
    ];

    /// <summary>The font for <paramref name="key"/>; an unknown one is the default font.</summary>
    public static TextFont For(string? key) => All.FirstOrDefault(f => f.Key == key) ?? All[0];

    static TextFont Shipped(string key, string family, FontWeight weight) =>
        new(key, family, new FontFamily("avares://kairos/Ui/Assets/Fonts#" + family), weight);
}
