using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Kairos.Ui.Edit;

/// <summary>
/// Any colour: a field of saturation and brightness in a hue, the hue bar beside it, and the colour's code to type or
/// paste. <see cref="Colour"/> is "#RRGGBB", changed as the user drags (the text or line follows at once).
/// </summary>
public partial class ColourPicker : UserControl
{
    public static readonly StyledProperty<string> ColourProperty =
        AvaloniaProperty.Register<ColourPicker, string>(nameof(Colour), "#FFFFFF", defaultBindingMode: BindingMode.TwoWay);

    double _h, _s, _v = 1;
    bool _own, _dragField, _dragHue;

    static ColourPicker() => ColourProperty.Changed.AddClassHandler<ColourPicker>((picker, _) => picker.Show());

    public ColourPicker()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Show();
        SizeChanged += (_, _) => Place();
        Field.PointerPressed += (_, e) => { _dragField = true; e.Pointer.Capture(Field); FromField(e.GetPosition(Field)); };
        Field.PointerMoved += (_, e) => { if (_dragField) FromField(e.GetPosition(Field)); };
        Field.PointerReleased += (_, e) => { _dragField = false; e.Pointer.Capture(null); };
        HueBar.PointerPressed += (_, e) => { _dragHue = true; e.Pointer.Capture(HueBar); FromHue(e.GetPosition(HueBar)); };
        HueBar.PointerMoved += (_, e) => { if (_dragHue) FromHue(e.GetPosition(HueBar)); };
        HueBar.PointerReleased += (_, e) => { _dragHue = false; e.Pointer.Capture(null); };
        HexBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                OnHexChanged();
        };
    }

    public string Colour
    {
        get => GetValue(ColourProperty);
        set => SetValue(ColourProperty, value);
    }

    /// <summary>The marks, the field's hue, the preview and the code follow a colour set from outside.</summary>
    void Show()
    {
        if (_own)
            return;
        if (ColourMath.TryParse(Colour, out string hex))
        {
            var (h, s, v) = ColourMath.ToHsv(hex);
            if (s > 0)
                _h = h; // a grey has no hue of its own: the field keeps the last one, so it does not jump
            _s = s;
            _v = v;
            _own = true;
            try
            {
                if (HexBox.Text != hex[1..])
                    HexBox.Text = hex[1..];
            }
            finally
            {
                _own = false;
            }
        }
        Place();
    }

    void Place()
    {
        HueFill.Fill = Brush(ColourMath.ToHex(_h, 1, 1));
        Preview.Fill = Brush(ColourMath.ToHex(_h, _s, _v));
        FieldMark.Margin = new Thickness(_s * Field.Bounds.Width - 7, (1 - _v) * Field.Bounds.Height - 7, 0, 0);
        HueMark.Margin = new Thickness(0, _h / 360 * HueBar.Bounds.Height - 3, 0, 0);
    }

    /// <summary>The colour from the marks' places: out through <see cref="Colour"/>, and the code with it.</summary>
    void Pick()
    {
        string hex = ColourMath.ToHex(_h, _s, _v);
        _own = true;
        try
        {
            Colour = hex;
            HexBox.Text = hex[1..];
        }
        finally
        {
            _own = false;
        }
        Place();
    }

    void FromField(Avalonia.Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, Field.Bounds.Width), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, Field.Bounds.Height), 0, 1);
        Pick();
    }

    void FromHue(Avalonia.Point p)
    {
        _h = Math.Clamp(p.Y / Math.Max(1, HueBar.Bounds.Height), 0, 0.9999) * 360;
        Pick();
    }

    void OnHexChanged()
    {
        string typed = (HexBox.Text ?? "").ToUpperInvariant();
        if (_own || !ColourMath.TryParse(typed, out string hex) || hex == Colour)
            return;
        var (h, s, v) = ColourMath.ToHsv(hex);
        if (s > 0)
            _h = h;
        _s = s;
        _v = v;
        _own = true;
        try
        {
            Colour = hex;
        }
        finally
        {
            _own = false;
        }
        Place();
    }

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}
