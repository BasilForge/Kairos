using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kairos.App;
using WinKeys = System.Windows.Forms.Keys;

namespace Kairos.Ui;

/// <summary>
/// A read-only box that records the next key combination pressed while it has focus, or the mouse's middle or side
/// button (with keys held too). Any works: Kairos takes no key from another app (<see cref="App.HotkeyWatcher"/>).
/// </summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>True when a hotkey box gets focus (keys pressed now are being typed), false when it loses it.</summary>
    public static event Action<bool>? TypingChanged;

    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        FlowDirection = FlowDirection.LeftToRight;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = App.Text.Get("hotkeys.press");
        TypingChanged?.Invoke(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Refresh();
        TypingChanged?.Invoke(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete)
        {
            Hotkey = "";
            Keyboard.ClearFocus();
            return;
        }
        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.Tab)
            return;

        Take((WinKeys)KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>The middle or a side mouse button pressed while the box has focus: that button (the left one only focuses).</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        var button = e.ChangedButton switch
        {
            MouseButton.Middle => WinKeys.MButton,
            MouseButton.XButton1 => WinKeys.XButton1,
            MouseButton.XButton2 => WinKeys.XButton2,
            _ => WinKeys.None,
        };
        if (button == WinKeys.None || !IsKeyboardFocused)
        {
            base.OnPreviewMouseDown(e);
            return;
        }
        e.Handled = true;
        Take(button);
    }

    /// <summary>Keeps <paramref name="key"/> with the modifiers held now, and lets the box go.</summary>
    void Take(WinKeys key)
    {
        var mods = Keyboard.Modifiers;
        var h = new App.Hotkey(key, mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift),
            mods.HasFlag(ModifierKeys.Windows));
        string formatted = HotkeyParser.Format(h);
        if (!HotkeyParser.TryParse(formatted, out _))
            return;
        Hotkey = formatted;
        Keyboard.ClearFocus();
    }

    void Refresh()
    {
        if (!IsKeyboardFocused)
            Text = string.IsNullOrEmpty(Hotkey) ? App.Text.Get("hotkeys.none") : Hotkey;
    }
}
