using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kairos.App;

/// <summary>
/// <see cref="HotkeyWatcher"/>'s keyboard hotkeys as Windows matches them (RegisterHotKey on a message-only window of
/// Kairos's UI thread, made, changed and let go there): Kairos wakes only for its own combos.
/// </summary>
sealed class WindowsHotkeys : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>A hotkey's id and the moment its press happened (Windows' message time, as Environment.TickCount).</summary>
    readonly Action<string, int> _heard;
    /// <summary>The registered hotkeys' ids, by atom - 1.</summary>
    readonly List<string> _ids = [];

    public WindowsHotkeys(Action<string, int> heard)
    {
        _heard = heard;
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
    }

    /// <summary>Registers these keyboard hotkeys in place of the earlier ones; the ids whose keys another app holds.</summary>
    public List<string> Register(IReadOnlyDictionary<string, Hotkey> keys)
    {
        Release();
        var refused = new List<string>();
        foreach (var (id, hotkey) in keys)
        {
            if (HotkeyParser.IsMouse(hotkey.Key))
                continue;
            if (RegisterHotKey(Handle, _ids.Count + 1, Modifiers(hotkey), (uint)hotkey.Key))
                _ids.Add(id);
            else
                refused.Add(id);
        }
        return refused;
    }

    /// <summary>Lets every key go (a hotkey box takes them while the user types one).</summary>
    public void Release()
    {
        for (int atom = 1; atom <= _ids.Count; atom++)
            UnregisterHotKey(Handle, atom);
        _ids.Clear();
    }

    static uint Modifiers(Hotkey hotkey) => MOD_NOREPEAT
        | (hotkey.Ctrl ? MOD_CONTROL : 0) | (hotkey.Alt ? MOD_ALT : 0) | (hotkey.Shift ? MOD_SHIFT : 0) | (hotkey.Win ? MOD_WIN : 0);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam is int atom && atom >= 1 && atom <= _ids.Count)
            _heard(_ids[atom - 1], GetMessageTime());
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Release();
        DestroyHandle();
    }

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    static extern int GetMessageTime();
}
