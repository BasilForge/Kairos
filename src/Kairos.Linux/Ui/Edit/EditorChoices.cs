using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using System.Windows.Media.Imaging;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

/// <summary>One choice among a few (a colour, a font, a look, a speed), chosen when it is the current one.</summary>
public class Choice<T>(T value, Action<T> choose, Func<T, T, bool>? same = null) : INotifyPropertyChanged
{
    bool _selected;

    public T Value => value;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (value)
                choose(Value);
        }
    }

    public void Refresh(T current)
    {
        bool selected = same?.Invoke(current, Value) ?? EqualityComparer<T>.Default.Equals(current, Value);
        if (selected == _selected)
            return;
        _selected = selected;
        Raise(nameof(IsSelected));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One colour of a palette.</summary>
public sealed class SwatchChoice(string hex, Action<string> choose)
    : Choice<string>(hex, choose, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
{
    public string Hex => Value;
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}

/// <summary>One of the text fonts.</summary>
public sealed class FontChoice(TextFont font, Action<string> choose) : Choice<string>(font.Key, choose)
{
    public string Key => font.Key;
    public string Label => font.Label;
    public FontFamily Family => font.Family;
    public FontWeight Weight => font.Weight;
}

/// <summary>A colour look, with a small picture of the clip in it once one is made.</summary>
public sealed class LookChoice(ColourLook look, string label, Action<ColourLook> choose) : Choice<ColourLook>(look, choose)
{
    BitmapSource? _thumb;

    public ColourLook Look => Value;
    public string Label => label;

    public BitmapSource? Thumb
    {
        get => _thumb;
        set
        {
            _thumb = value;
            Raise(nameof(Thumb));
        }
    }
}

/// <summary>A speed button (0.25× … 3×).</summary>
public sealed class SpeedChoice(double speed, Action<double> choose) : Choice<double>(speed, choose, (a, b) => Math.Abs(a - b) < 1e-6)
{
    public string Label => Value.ToString("0.##", CultureInfo.InvariantCulture) + "×";
}
