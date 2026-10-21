using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Kairos.Ui.Edit;

/// <summary>
/// A row of colours: the palette's dots, then a rainbow dot for any other colour, which opens the picker. The chosen
/// colour (<see cref="Colour"/>, "#RRGGBB") is ringed; one not in the palette rings the rainbow dot and shows in it.
/// </summary>
public partial class ColourRow : UserControl
{
    public static readonly StyledProperty<IEnumerable<SwatchChoice>?> SwatchesProperty = AvaloniaProperty.Register<ColourRow, IEnumerable<SwatchChoice>?>(nameof(Swatches));

    public static readonly StyledProperty<string> ColourProperty =
        AvaloniaProperty.Register<ColourRow, string>(nameof(Colour), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> GroupNameProperty = AvaloniaProperty.Register<ColourRow, string>(nameof(GroupName), "colour");

    public static readonly StyledProperty<IBrush> CustomBrushProperty = AvaloniaProperty.Register<ColourRow, IBrush>(nameof(CustomBrush), Brushes.White);

    static ColourRow()
    {
        SwatchesProperty.Changed.AddClassHandler<ColourRow>((row, _) => row.Follow());
        ColourProperty.Changed.AddClassHandler<ColourRow>((row, _) => row.Follow());
    }

    public ColourRow()
    {
        InitializeComponent();
        Follow();
    }

    public IEnumerable<SwatchChoice>? Swatches
    {
        get => GetValue(SwatchesProperty);
        set => SetValue(SwatchesProperty, value);
    }

    public string Colour
    {
        get => GetValue(ColourProperty);
        set => SetValue(ColourProperty, value);
    }

    /// <summary>The radio group (one per row: a text's, a line's, a shape's).</summary>
    public string GroupName
    {
        get => GetValue(GroupNameProperty);
        set => SetValue(GroupNameProperty, value);
    }

    /// <summary>The colour shown in the rainbow dot: the one chosen in the picker.</summary>
    public IBrush CustomBrush => GetValue(CustomBrushProperty);

    /// <summary>The rainbow dot is the chosen one when the colour is none of the palette's, and shows that colour.</summary>
    void Follow()
    {
        bool custom = ColourMath.TryParse(Colour, out string hex)
            && !(Swatches ?? []).Any(s => string.Equals(s.Hex, hex, StringComparison.OrdinalIgnoreCase));
        if (custom)
            SetValue(CustomBrushProperty, new SolidColorBrush(Color.Parse(hex)).ToImmutable());
        if (Custom != null)
            Custom.IsChecked = custom;
    }

    void OnCustom(object? sender, RoutedEventArgs e)
    {
        Custom.IsChecked = true;
        PickerPopup.IsOpen = true;
    }
}
