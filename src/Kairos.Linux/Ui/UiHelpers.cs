using System.Globalization;
using System.Windows.Input;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Kairos.App;

namespace Kairos.Ui;

/// <summary>{l:T key} — a localized string from <see cref="Text"/>.</summary>
public sealed class T : MarkupExtension
{
    public T() { }
    public T(string key) => Key = key;
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => Text.Get(Key);
}

public sealed class RelayCommand : ICommand
{
    static readonly List<WeakReference<RelayCommand>> All = [];
    readonly Action<object?> run;
    readonly Func<object?, bool>? canRun;

    public RelayCommand(Action<object?> run, Func<object?, bool>? canRun = null)
    {
        this.run = run;
        this.canRun = canRun;
        lock (All)
            All.Add(new WeakReference<RelayCommand>(this));
    }

    public RelayCommand(Action run) : this(_ => run()) { }

    public event EventHandler? CanExecuteChanged;

    /// <summary>Every command asks its button again (what WPF's CommandManager does on its own).</summary>
    public static void RaiseAll()
    {
        List<RelayCommand> live;
        lock (All)
        {
            All.RemoveAll(r => !r.TryGetTarget(out _));
            live = All.Select(r => r.TryGetTarget(out var c) ? c : null!).Where(c => c != null).ToList();
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => live.ForEach(c => c.RaiseCanExecuteChanged()));
    }

    static int _pending;

    /// <summary>
    /// <see cref="RaiseAll"/> once for any number of calls close together. WPF asks every command again after each bit of
    /// input and the view models count on that, so the window calls this on every change and every click.
    /// </summary>
    public static void RequeryLater()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
            return;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            Volatile.Write(ref _pending, 0);
            RaiseAll();
        }, TimeSpan.FromMilliseconds(30));
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => run(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record Option(string Label, object Value);

/// <summary>Two-way bridge between an enum/int/string property and a RadioButton's IsChecked.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null)
            return BindingOperations.DoNothing;
        string p = parameter.ToString()!;
        targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (targetType.IsEnum)
            return Enum.Parse(targetType, p, ignoreCase: true);
        if (targetType == typeof(int))
            return int.Parse(p, CultureInfo.InvariantCulture);
        return p;
    }
}

/// <summary>True for a truthy value (a non-empty string, a non-zero number); the parameter "invert" flips it.</summary>
public sealed class TruthyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool on = value is true || (value is string s && s.Length > 0) || (value is int i && i != 0) || (value is not null and not bool and not string and not int);
        return parameter as string == "invert" ? !on : on;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>True when the bound value equals the parameter (string compare).</summary>
public sealed class EqualsVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

/// <summary>The share panel's caption direction (the view model answers in WPF's FlowDirection).</summary>
public sealed class FlowConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly FlowConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is System.Windows.FlowDirection.RightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>An emoji's colour picture (Ui/Assets/Emoji, named by code points).</summary>
public sealed class EmojiImageConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly EmojiImageConverter Instance = new();
    static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> Cache = [];

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not EmojiPick pick)
            return null;
        if (!Cache.TryGetValue(pick.File, out var bitmap))
        {
            try { bitmap = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://kairos/Ui/Assets/Emoji/" + pick.File))); }
            catch (Exception e) when (e is IOException or UriFormatException) { bitmap = null; }
            Cache[pick.File] = bitmap;
        }
        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A platform's round logo.</summary>
public sealed class PlatformLogoConverter : Avalonia.Data.Converters.IValueConverter
{
    public static readonly PlatformLogoConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value == null ? null : new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri($"avares://kairos/Ui/Assets/{value.ToString()!.ToLowerInvariant()}.png")));

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
