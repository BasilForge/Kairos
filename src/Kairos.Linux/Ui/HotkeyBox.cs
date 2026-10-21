using System.Windows.Forms;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kairos.App;

namespace Kairos.Ui;

/// <summary>A read-only box that records the next key combination pressed while it has focus.</summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>True when a hotkey box gets focus (keys pressed now are being typed), false when it loses it.</summary>
    public static event Action<bool>? TypingChanged;

    public static readonly StyledProperty<string> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyBox, string>(nameof(Hotkey), "", defaultBindingMode: BindingMode.TwoWay);

    public string Hotkey { get => GetValue(HotkeyProperty); set => SetValue(HotkeyProperty, value); }

    // A TextBox is styled by its own type; this one looks like any other.
    protected override Type StyleKeyOverride => typeof(TextBox);

    public HotkeyBox()
    {
        IsReadOnly = true;
        FlowDirection = Avalonia.Media.FlowDirection.LeftToRight;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Hand);
        GotFocus += Focused;
        LostFocus += Unfocused;
        Refresh();
    }

    static HotkeyBox() => HotkeyProperty.Changed.AddClassHandler<HotkeyBox>((box, _) => box.Refresh());

    void Focused(object? sender, RoutedEventArgs e)
    {
        Text = App.Text.Get("hotkeys.press");
        TypingChanged?.Invoke(true);
    }

    void Unfocused(object? sender, RoutedEventArgs e)
    {
        Refresh();
        TypingChanged?.Invoke(false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key;
        if (key is Key.Back or Key.Delete)
        {
            Hotkey = "";
            Release();
            return;
        }
        if (key == Key.Escape)
        {
            Release();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab or Key.None)
            return;
        if (!Enum.TryParse<Keys>(key.ToString(), ignoreCase: true, out var vk) || !Enum.IsDefined(vk))
            return;

        var mods = e.KeyModifiers;
        var h = new App.Hotkey(vk, mods.HasFlag(KeyModifiers.Control), mods.HasFlag(KeyModifiers.Alt), mods.HasFlag(KeyModifiers.Shift), mods.HasFlag(KeyModifiers.Meta));
        string formatted = HotkeyParser.Format(h);
        if (!HotkeyParser.TryParse(formatted, out _))
            return;
        // Kairos's own keys are let go while a box has focus, so a key held now is another app's: it would not work.
        if (!HotkeyManager.IsFree(h))
        {
            Refuse();
            return;
        }
        Hotkey = formatted;
        Release();
    }

    void Release() => TopLevel.GetTopLevel(this)?.Focus();

    /// <summary>Says so inside the box for two seconds, keeps the old key, and waits for another.</summary>
    void Refuse()
    {
        Text = App.Text.Get("hotkeys.takenBox");
        _refused ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refused.Stop();
        _refused.Tick -= OnRefusedShown;
        _refused.Tick += OnRefusedShown;
        _refused.Start();
    }

    void OnRefusedShown(object? sender, EventArgs e)
    {
        _refused!.Stop();
        if (IsFocused)
            Text = App.Text.Get("hotkeys.press");
    }

    DispatcherTimer? _refused;

    void Refresh()
    {
        if (!IsFocused)
            Text = string.IsNullOrEmpty(Hotkey) ? App.Text.Get("hotkeys.none") : Hotkey;
    }
}
