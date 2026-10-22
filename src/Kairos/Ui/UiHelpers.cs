using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using Kairos.App;

namespace Kairos.Ui;

/// <summary>{l:T key} — a localized string from <see cref="Text"/>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string key) => Key = key;
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Text.Get(Key);
}

/// <summary>Two-way bridge between an enum/int/string property and a RadioButton's IsChecked.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null)
            return Binding.DoNothing;
        string p = parameter.ToString()!;
        targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (targetType.IsEnum)
            return Enum.Parse(targetType, p, ignoreCase: true);
        if (targetType == typeof(int))
            return int.Parse(p, CultureInfo.InvariantCulture);
        if (targetType == typeof(bool))
            return bool.Parse(p);
        return p;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value is true || (value is string s && s.Length > 0) || (value is int i && i != 0);
        if (parameter as string == "invert")
            on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound value equals the parameter (string compare).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => run(parameter);
}

public sealed record Option(string Label, object Value);

/// <summary>Where a window opens.</summary>
public static class WindowPlacement
{
    /// <summary>
    /// <paramref name="window"/> set to open far off screen: never activated, no taskbar button, nothing on screen (hidden
    /// browsers, renders, checks and measurements).
    /// </summary>
    public static T OffScreen<T>(T window) where T : Window
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        // Never the foreground window, even when a control inside takes the keyboard focus (the player and the editor
        // do as they open): one such window once pulled a game being played out of focus.
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            SetWindowLongPtr(hwnd, GwlExStyle, GetWindowLongPtr(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };
        return window;
    }

    /// <summary>
    /// Puts <paramref name="to"/> where <paramref name="from"/> is (the window built again in another language): at its
    /// normal place and size, maximized when it is.
    /// </summary>
    public static void Carry(Window from, Window to)
    {
        var (bounds, maximized) = Carried(from.WindowState, new Rect(from.Left, from.Top, from.Width, from.Height), from.RestoreBounds);
        to.WindowStartupLocation = WindowStartupLocation.Manual;
        (to.Left, to.Top, to.Width, to.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        if (maximized)
            to.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// A window's normal place and size, and whether it is maximized: a maximized window's Left and Top are its normal
    /// place but its Width and Height the screen's (taken as they were, it came back unmaximized and past the screen).
    /// </summary>
    internal static (Rect Bounds, bool Maximized) Carried(WindowState state, Rect current, Rect normal) =>
        state == WindowState.Normal ? (current, false) : (normal, state == WindowState.Maximized);

    const int GwlExStyle = -20;
    const nint WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);
}
