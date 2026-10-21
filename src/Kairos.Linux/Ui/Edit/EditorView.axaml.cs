using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Windows.Media.Imaging;

namespace Kairos.Ui.Edit;

/// <summary>
/// The editor page over the window. Keys (Space, S, Delete, arrows, Esc, Ctrl+Z, Ctrl+Y) go to the view model unless a
/// text box is being typed in. The window fills the preview and the timeline.
/// </summary>
public partial class EditorView : UserControl
{
    // Icon codes: back, forward, undo, redo, play, pause, volume.
    const string Back = "E72B", Forward = "E72A", Undo = "E7A7", Redo = "E7A6", PlayGlyph = "F5B0", PauseGlyph = "F8AE", Volume = "E767";

    /// <summary>The play bar is narrow: the Add buttons show their icons only (their names stay as tooltips).</summary>
    public static readonly StyledProperty<bool> CompactProperty = AvaloniaProperty.Register<EditorView, bool>(nameof(Compact));

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>The zoom slider's width (known once it has been shown).</summary>
    double _zoomWidth;

    public EditorView(EditorViewModel vm)
    {
        InitializeComponent();
        Focusable = true;
        DataContext = vm;
        Model = vm;
        PreviewHost.Children.Add(new EditCanvas(vm)); // over the picture, the same size
        if (vm.HasTimeline)
        {
            // The timeline grows with its rows up to about half the page; the picture gives way (nothing scrolls).
            var timeline = new EditTimeline(vm);
            TimelineHost.Content = timeline;
            SizeChanged += (_, _) =>
            {
                timeline.MaxHeight = Math.Max(150, Bounds.Height * 0.5 - 30);
                timeline.FillHeight = Bounds.Height * 0.22; // a taller editor: taller rows, not only a bigger preview
            };
        }
        _playback = new EditorPlayback(vm, SetPicture, ShowPlaying);
        Hook(_playback);
        vm.SaveAsked += AskSave;
        vm.PropertyChanged += OnCaptionsChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.AskingRecaption) && vm.AskingRecaption)
                Dispatcher.UIThread.Post(() => KeepCaptionsButton.Focus(), DispatcherPriority.Input);
        };
        _noteTimer.Tick += (_, _) =>
        {
            _noteTimer.Stop();
            Model.DismissCaptionsNote();
        };
        _thumbTimer.Tick += (_, _) =>
        {
            _thumbTimer.Stop();
            MakeThumbs();
        };
        if (!vm.HasSeparateTracks)
            PartGameIcon.Glyph = Volume; // one track: the part's whole sound
        // Arrows that point a way are mirrored for Arabic (chosen by the language: the page may be drawn before it loads).
        bool rtl = App.Text.IsRightToLeft;
        BackIcon.Glyph = rtl ? Forward : Back;
        UndoIcon.Glyph = rtl ? Redo : Undo;
        RedoIcon.Glyph = rtl ? Undo : Redo;
        // Narrow: the Add buttons lose their names and the zoom slider goes (Ctrl+wheel still zooms), back with room.
        PlayBar.SizeChanged += (_, _) => FitPill();
        MidGroup.SizeChanged += (_, _) => Dispatcher.UIThread.Post(FitPill, DispatcherPriority.Loaded); // captions working, a name shown
        ZoomBox.IsVisible = vm.HasTimeline;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        AddHandler(PointerPressedEvent, (_, _) => _focusFromKeyboard = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUpTunnel, RoutingStrategies.Tunnel);
        FillShortcuts(KeysLeft, EditorShortcuts.Left);
        FillShortcuts(KeysRight, EditorShortcuts.Right);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.PlayheadOutput) or nameof(EditorViewModel.Project))
                ShowTime();
            if (e.PropertyName is nameof(EditorViewModel.IsClipPage) or nameof(EditorViewModel.IsPartPage)
                && (vm.IsClipPage || vm.IsPartPage) && _thumbSource != null)
                _thumbTimer.Start();
        };
        ShowTime();
    }

    public EditorViewModel Model { get; }

    static IBrush Find(string key) => Application.Current?.TryFindResource(key, out var found) == true && found is IBrush b ? b : Brushes.Gray;

    /// <summary>The shortcuts sheet's column: each group's heading, then a line per shortcut with its key caps.</summary>
    void FillShortcuts(StackPanel column, IReadOnlyList<ShortcutGroup> groups)
    {
        foreach (var group in groups)
        {
            column.Children.Add(new TextBlock
            {
                Text = App.Text.Get(group.Title),
                FontSize = 11.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = Find("Accent"),
                Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (var line in group.Lines)
            {
                var row = new DockPanel { Height = 26 };
                var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                foreach (string key in line.Keys)
                {
                    var cap = new Border
                    {
                        Child = new TextBlock { Text = key.StartsWith("t:", StringComparison.Ordinal) ? App.Text.Get(key[2..]) : key, FontSize = 11, FontWeight = FontWeight.SemiBold },
                    };
                    cap.Classes.Add("KeyCap");
                    keys.Children.Add(cap);
                }
                DockPanel.SetDock(keys, Dock.Right);
                row.Children.Add(keys);
                row.Children.Add(new TextBlock { Text = App.Text.Get(line.Label), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                column.Children.Add(new Border
                {
                    BorderBrush = Find("Stroke"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = row,
                });
            }
        }
    }

    void OnShortcutsBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == ShortcutsSheet)
            Model.CloseShortcutsCommand.Execute(null);
    }

    void OnShortcutsCard(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>
    /// Names on the Add buttons and the zoom slider when the whole bar fits with them, icons only otherwise. Worked out
    /// from the buttons shown now (with their names added back when they are hidden), so it never depends on an
    /// earlier layout.
    /// </summary>
    void FitPill()
    {
        if (ZoomBox.IsVisible && ZoomBox.Bounds.Width > 0)
            _zoomWidth = ZoomBox.Bounds.Width + ZoomBox.Margin.Left;
        double zoom = Model.HasTimeline ? (_zoomWidth > 0 ? _zoomWidth : 96) : 0;
        double pill = 8 + ((Panel)AddPill.Child!).Children.Sum(FullWidth) + Chip(CaptionsBusyChip) + Chip(CaptionsNote);
        double right = RightGroup.Bounds.Width - (ZoomBox.IsVisible ? _zoomWidth : 0);
        double need = LeftGroup.Bounds.Width + MidGroup.Margin.Left + MidGroup.Margin.Right + pill + right + zoom + 8;
        // A little to spare when going back, so it does not flip at the edge.
        bool compact = Compact ? PlayBar.Bounds.Width < need + 8 : PlayBar.Bounds.Width < need;
        if (compact == Compact)
            return;
        Compact = compact;
        if (Model.HasTimeline)
            ZoomBox.IsVisible = !compact;
    }

    /// <summary>A captions chip's width while it shows (its progress, or how it went).</summary>
    static double Chip(Control chip) => chip.IsVisible ? chip.Margin.Left + chip.Bounds.Width : 0;

    /// <summary>A shown pill button's width with its name (a hidden name measured and added).</summary>
    double FullWidth(Control e)
    {
        if (!e.IsVisible)
            return 0;
        if (e is Grid grid)
            return grid.Children.Select(FullWidth).DefaultIfEmpty(0).Max();
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = e.DesiredSize.Width;
        if (Compact && e is Button { Content: string label } button)
            width += 6 + new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection,
                new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize, Brushes.White).WidthIncludingTrailingWhitespace;
        return width;
    }

    EditorPlayback _playback;

    /// <summary>The preview (the measuring harness watches its still pictures).</summary>
    internal EditorPlayback Playback => _playback;

    /// <summary>Saving: the preview stops and lets go of the clip (a replace needs that).</summary>
    public void Suspend() => _playback.Dispose();

    /// <summary>Back to editing: the preview comes back at the playhead.</summary>
    public void Resume()
    {
        _playback = new EditorPlayback(Model, SetPicture, ShowPlaying);
        Hook(_playback);
    }

    void Hook(EditorPlayback playback)
    {
        playback.StillRead += OnStillRead;
        playback.Frame += () => PreviewImage.InvalidateVisual();
    }

    /// <summary>Save was answered: a new copy (false) or in place of the original (true).</summary>
    public event Action<bool>? SaveChosen;

    void AskSave()
    {
        SaveChoice.IsVisible = true;
        Dispatcher.UIThread.Post(() => NewCopyButton.Focus(), DispatcherPriority.Input);
    }

    void Choose(bool replace)
    {
        SaveChoice.IsVisible = false;
        SaveChosen?.Invoke(replace);
    }

    void OnNewCopy(object? sender, RoutedEventArgs e) => Choose(replace: false);

    void OnReplace(object? sender, RoutedEventArgs e) => Choose(replace: true);

    void OnSaveCancel(object? sender, RoutedEventArgs e) => SaveChoice.IsVisible = false;

    void OnSaveChoiceBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == SaveChoice)
            SaveChoice.IsVisible = false;
    }

    void OnSaveChoiceCard(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>How auto captions went shows in the button's place for a moment (or until clicked).</summary>
    void OnCaptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorViewModel.HasCaptionsNote))
            return;
        _noteTimer.Stop();
        if (Model.HasCaptionsNote)
            _noteTimer.Start();
    }

    void OnCaptionsNote(object? sender, PointerReleasedEventArgs e) => Model.DismissCaptionsNote();

    // ---------------------------------------------------------------- the look tiles' pictures

    readonly DispatcherTimer _thumbTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    byte[]? _thumbSource;
    int _thumbRun;

    /// <summary>A new still: the look tiles show it through each look (soon after, while they are on screen).</summary>
    void OnStillRead(byte[] small)
    {
        _thumbSource = small;
        if (Model.IsClipPage || Model.IsPartPage)
        {
            _thumbTimer.Stop();
            _thumbTimer.Start();
        }
    }

    async void MakeThumbs()
    {
        if (_thumbSource is not { } small)
            return;
        int run = ++_thumbRun;
        var looks = Model.Looks;
        var made = await LookThumbs.Make(small, looks.Select(l => l.Look).ToList());
        if (run != _thumbRun)
            return; // a newer still came meanwhile
        for (int i = 0; i < looks.Count && i < made.Count; i++)
        {
            looks[i].Thumb = made[i];
            Model.PartLooks[i].Thumb = made[i]; // the same looks, on the parts' page
        }
    }

    public void Close()
    {
        _noteTimer.Stop();
        _thumbTimer.Stop();
        _thumbRun++;
        Model.CancelCaptions();
        _playback.Dispose();
    }

    /// <summary>Where the window puts the editing surface over the picture.</summary>
    public Grid Preview => PreviewHost;

    public void SetPicture(BitmapSource? picture)
    {
        PreviewImage.Source = picture;
        PreviewImage.InvalidateVisual(); // the same bitmap, new pixels
    }

    public void ShowPlaying(bool playing)
    {
        PlayIcon.Glyph = playing ? PauseGlyph : PlayGlyph;
        Avalonia.Automation.AutomationProperties.SetName(PlayButton, App.Text.Get(playing ? "viewer.pause" : "viewer.play"));
    }

    void ShowTime() => TimeText.Text = $"{Clock(Model.PlayheadOutput)} / {Clock(Model.Project.OutputDuration)}";

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    void OnPlay(object? sender, RoutedEventArgs e) => Model.KeyCommand("Space");

    /// <summary>The focus came from the keyboard (Tab), not a click: a focused tile then keeps its arrows.</summary>
    bool _focusFromKeyboard;

    /// <summary>
    /// Arrows stay with the focused control (they move the playhead otherwise): a slider always, and a tile, button or
    /// list reached with the keyboard, so Tab and the arrows can walk through them.
    /// </summary>
    internal static bool KeepsArrows(object? focused, bool fromKeyboard) =>
        focused is Slider || (fromKeyboard && focused is Button or ComboBox or ListBoxItem);

    void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            Model.KeyReleased();
    }

    void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
            _focusFromKeyboard = true;
        object? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox)
            return; // typing a text, or its words in the inspector
        if (Model.AskingDiscard)
        {
            if (e.Key == Key.Escape)
            {
                Model.KeepEditingCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        if (Model.AskingReset)
        {
            if (e.Key == Key.Escape)
            {
                Model.CancelResetCommand.Execute(null); // keeping the edits is the safe answer
                e.Handled = true;
            }
            return;
        }
        if (Model.AskingRecaption)
        {
            if (e.Key == Key.Escape)
            {
                Model.KeepCaptionsCommand.Execute(null); // keeping them is the safe answer
                e.Handled = true;
            }
            return;
        }
        if (Model.ShowingIntro)
        {
            if (e.Key == Key.Escape)
            {
                Model.NotNowCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
        if (SaveChoice.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                SaveChoice.IsVisible = false;
                e.Handled = true;
            }
            return;
        }
        bool slider = KeepsArrows(focused, _focusFromKeyboard); // a focused slider (or tile reached by Tab) keeps its arrows
        string? key = (e.KeyModifiers, e.Key) switch
        {
            (KeyModifiers.None, Key.Space) => "Space",
            (KeyModifiers.None, Key.S) => "S",
            (KeyModifiers.Control, Key.B) => "Ctrl+B",
            (KeyModifiers.None, Key.Q) => "Q",
            (KeyModifiers.None, Key.W) => "W",
            (KeyModifiers.None, Key.Delete or Key.Back) => "Delete",
            (KeyModifiers.Control, Key.Z) => "Ctrl+Z",
            (KeyModifiers.Control, Key.Y) => "Ctrl+Y",
            (KeyModifiers.Control, Key.A) => "Ctrl+A",
            (KeyModifiers.Control, Key.C) => "Ctrl+C",
            (KeyModifiers.Control, Key.V) => "Ctrl+V",
            (KeyModifiers.Control, Key.D) => "Ctrl+D",
            (KeyModifiers.None, Key.Enter) => "Enter",
            (KeyModifiers.None, Key.Left) when !slider => "Left",
            (KeyModifiers.None, Key.Right) when !slider => "Right",
            (KeyModifiers.Shift, Key.Left) when !slider => "Shift+Left",
            (KeyModifiers.Shift, Key.Right) when !slider => "Shift+Right",
            (KeyModifiers.Alt, Key.Left) => "Alt+Left",
            (KeyModifiers.Alt, Key.Right) => "Alt+Right",
            (KeyModifiers.Alt | KeyModifiers.Shift, Key.Left) => "Alt+Shift+Left",
            (KeyModifiers.Alt | KeyModifiers.Shift, Key.Right) => "Alt+Shift+Right",
            (KeyModifiers.None, Key.Up) when !slider => "Up",
            (KeyModifiers.None, Key.Down) when !slider => "Down",
            (KeyModifiers.None, Key.Home) => "Home",
            (KeyModifiers.None, Key.End) => "End",
            (KeyModifiers.Control, Key.OemPlus or Key.Add) => "Ctrl+Plus",
            (KeyModifiers.Control, Key.OemMinus or Key.Subtract) => "Ctrl+Minus",
            (KeyModifiers.None, Key.OemPipe or Key.OemBackslash) => "Backslash",
            (KeyModifiers.Shift, Key.OemQuestion) or (KeyModifiers.None, Key.F1) => "?",
            (KeyModifiers.None, Key.Escape) => "Escape",
            _ => null,
        };
        // With the shortcuts sheet open, only closing it.
        if (Model.ShowingShortcuts && key is not ("Escape" or "?"))
            return;
        if (key != null && Model.KeyCommand(key, false))
            e.Handled = true;
    }
}
