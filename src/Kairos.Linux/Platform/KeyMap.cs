using System.Windows.Forms;
using Kairos.App;

namespace Kairos.Platform;

/// <summary>Kairos's hotkey keys (the Windows virtual-key names its settings store) as X11 keycodes and modifier masks.</summary>
static class KeyMap
{
    public static bool TryCode(IntPtr display, Hotkey hotkey, out int keycode, out uint mods)
    {
        keycode = 0;
        mods = (hotkey.Ctrl ? Xlib.Control : 0) | (hotkey.Alt ? Xlib.Mod1 : 0) | (hotkey.Shift ? Xlib.Shift : 0) | (hotkey.Win ? Xlib.Mod4 : 0);
        if (Keysym(hotkey.Key) is not { } sym)
            return false;
        keycode = Xlib.XKeysymToKeycode(display, sym);
        return keycode != 0;
    }

    /// <summary>The X11 keysym of a key (X11/keysymdef.h), or null for a key with no equivalent.</summary>
    public static nuint? Keysym(Keys key)
    {
        int k = (int)key;
        if (key is >= Keys.A and <= Keys.Z) return (nuint)('a' + (k - (int)Keys.A));
        if (key is >= Keys.D0 and <= Keys.D9) return (nuint)('0' + (k - (int)Keys.D0));
        if (key is >= Keys.F1 and <= Keys.F24) return (nuint)(0xFFBE + (k - (int)Keys.F1));
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9) return (nuint)(0xFFB0 + (k - (int)Keys.NumPad0));
        return key switch
        {
            Keys.Space => 0x20, Keys.Back => 0xFF08, Keys.Tab => 0xFF09, Keys.Return => 0xFF0D, Keys.Pause => 0xFF13,
            Keys.Escape => 0xFF1B, Keys.Home => 0xFF50, Keys.Left => 0xFF51, Keys.Up => 0xFF52, Keys.Right => 0xFF53,
            Keys.Down => 0xFF54, Keys.PageUp => 0xFF55, Keys.PageDown => 0xFF56, Keys.End => 0xFF57, Keys.PrintScreen => 0xFF61,
            Keys.Insert => 0xFF63, Keys.Delete => 0xFFFF, Keys.Multiply => 0xFFAA, Keys.Add => 0xFFAB, Keys.Subtract => 0xFFAD,
            Keys.Decimal => 0xFFAE, Keys.Divide => 0xFFAF, Keys.Scroll => 0xFF14,
            Keys.OemSemicolon => 0x3B, Keys.Oemplus => 0x3D, Keys.Oemcomma => 0x2C, Keys.OemMinus => 0x2D, Keys.OemPeriod => 0x2E,
            Keys.OemQuestion => 0x2F, Keys.Oemtilde => 0x60, Keys.OemOpenBrackets => 0x5B, Keys.OemPipe => 0x5C,
            Keys.OemCloseBrackets => 0x5D, Keys.OemQuotes => 0x27,
            Keys.MediaPlayPause => 0x1008FF14, Keys.MediaStop => 0x1008FF15, Keys.MediaPreviousTrack => 0x1008FF16,
            Keys.MediaNextTrack => 0x1008FF17, Keys.VolumeMute => 0x1008FF12, Keys.VolumeDown => 0x1008FF11, Keys.VolumeUp => 0x1008FF13,
            _ => null,
        };
    }
}
