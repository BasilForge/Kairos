using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

/// <summary>
/// The editor page over the window. Keys (Space, S, Delete, arrows, Esc, Ctrl+Z, Ctrl+Y) go to the view model unless a
/// text box is being typed in. The window fills the preview and the timeline.
/// </summary>
public partial class EditorView : UserControl
{
    // Segoe Fluent Icons: back, forward, undo, redo, play, pause.
    const char Back = (char)0xE72B, Forward = (char)0xE72A, Undo = (char)0xE7A7, Redo = (char)0xE7A6, PlayGlyph = (char)0xF5B0, PauseGlyph = (char)0xF8AE;

    /// <summary>The play bar is narrow: the Add buttons show their icons only (their names stay as tooltips).</summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(EditorView));

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>The zoom slider's width (known once it has been shown).</summary>
    double _zoomWidth;

    public EditorView(EditorViewModel vm)
    {
        InitializeComponent();
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
                timeline.MaxHeight = Math.Max(150, ActualHeight * 0.5 - 30);
                timeline.FillHeight = ActualHeight * 0.22; // a taller editor: taller rows, not only a bigger preview
            };
        }
        _playback = new EditorPlayback(vm, SetPicture, ShowPlaying);
        _playback.StillRead += OnStillRead;
        vm.SaveAsked += AskSave;
        WordsBox.LostKeyboardFocus += (_, _) => Model.DropEmptyTexts(); // a new text whose words were not typed there
        vm.PropertyChanged += OnCaptionsChanged;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.AskingRecaption) && vm.AskingRecaption)
                Dispatcher.BeginInvoke(() => KeepCaptionsButton.Focus(), DispatcherPriority.Input);
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
        // Arrows that point a way are mirrored for Arabic (chosen by the language: the page may be drawn before it loads).
        bool rtl = App.Text.IsRightToLeft;
        BackIcon.Text = (rtl ? Forward : Back).ToString();
        UndoIcon.Text = (rtl ? Redo : Undo).ToString();
        RedoIcon.Text = (rtl ? Undo : Redo).ToString();
        // Narrow: the Add buttons lose their names and the zoom slider goes (Ctrl+wheel still zooms), back with room.
        PlayBar.SizeChanged += (_, _) => FitPill();
        MidGroup.SizeChanged += (_, _) => Dispatcher.BeginInvoke(FitPill, DispatcherPriority.Loaded); // captions working, a name shown
        ZoomBox.Visibility = vm.HasTimeline ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Focus();
        FillShortcuts(KeysLeft, EditorShortcuts.Left);
        FillShortcuts(KeysRight, EditorShortcuts.Right);
        PreviewHost.SizeChanged += (_, _) => ShowZoom();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorViewModel.PlayheadOutput) or nameof(EditorViewModel.Project))
                ShowTime();
            if (e.PropertyName is nameof(EditorViewModel.PlayheadOutput) or nameof(EditorViewModel.Project) or nameof(EditorViewModel.EditMode))
                ShowZoom();
            if (e.PropertyName is nameof(EditorViewModel.IsClipPage) or nameof(EditorViewModel.IsPartPage)
                && (vm.IsClipPage || vm.IsPartPage) && _thumbSource != null)
                _thumbTimer.Start();
        };
        ShowTime();
    }

    public EditorViewModel Model { get; }

    /// <summary>The shortcuts sheet's column: each group's heading, then a line per shortcut with its key caps.</summary>
    void FillShortcuts(StackPanel column, IReadOnlyList<ShortcutGroup> groups)
    {
        foreach (var group in groups)
        {
            column.Children.Add(new TextBlock
            {
                Text = App.Text.Get(group.Title),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
                Margin = new Thickness(0, 10, 0, 4),
            });
            foreach (var line in group.Lines)
            {
                var row = new DockPanel { Height = 26 };
                var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                foreach (string key in line.Keys)
                    keys.Children.Add(new Border
                    {
                        Style = (Style)FindResource("KeyCap"),
                        Child = new TextBlock { Text = key.StartsWith("t:", StringComparison.Ordinal) ? App.Text.Get(key[2..]) : key, FontSize = 11, FontWeight = FontWeights.SemiBold },
                    });
                DockPanel.SetDock(keys, Dock.Right);
                row.Children.Add(keys);
                row.Children.Add(new TextBlock { Text = App.Text.Get(line.Label), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
                column.Children.Add(new Border
                {
                    BorderBrush = (System.Windows.Media.Brush)FindResource("Stroke"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = row,
                });
            }
        }
    }

    void OnShortcutsBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == ShortcutsSheet)
            Model.CloseShortcutsCommand.Execute(null);
    }

    void OnShortcutsCard(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>
    /// Names on the Add buttons and the zoom slider when the whole bar fits with them, icons only otherwise. Worked out
    /// from the buttons shown now (with their names added back when they are hidden), so it never depends on an
    /// earlier layout.
    /// </summary>
    void FitPill()
    {
        if (ZoomBox.Visibility == Visibility.Visible && ZoomBox.ActualWidth > 0)
            _zoomWidth = ZoomBox.ActualWidth + ZoomBox.Margin.Left;
        double zoom = Model.HasTimeline ? (_zoomWidth > 0 ? _zoomWidth : 96) : 0;
        double pill = 8 + ((Panel)AddPill.Child).Children.Cast<FrameworkElement>().Sum(FullWidth)
            + Chip(CaptionsBusyChip) + Chip(CaptionsNote);
        double right = RightGroup.ActualWidth - (ZoomBox.Visibility == Visibility.Visible ? _zoomWidth : 0);
        double need = LeftGroup.ActualWidth + MidGroup.Margin.Left + MidGroup.Margin.Right + pill + right + zoom + 8;
        // A little to spare when going back, so it does not flip at the edge.
        bool compact = Compact ? PlayBar.ActualWidth < need + 8 : PlayBar.ActualWidth < need;
        if (compact == Compact)
            return;
        Compact = compact;
        if (Model.HasTimeline)
            ZoomBox.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A captions chip's width while it shows (its progress, or how it went).</summary>
    static double Chip(FrameworkElement chip) => chip.Visibility == Visibility.Visible ? chip.Margin.Left + chip.ActualWidth : 0;

    /// <summary>A shown pill button's width with its name (a hidden name measured and added).</summary>
    double FullWidth(FrameworkElement e)
    {
        if (e.Visibility != Visibility.Visible)
            return 0;
        if (e is Grid grid)
            return grid.Children.Cast<FrameworkElement>().Select(FullWidth).DefaultIfEmpty(0).Max();
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = e.DesiredSize.Width;
        if (Compact && e is System.Windows.Controls.Primitives.ButtonBase { Content: string label } button)
            width += 6 + new System.Windows.Media.FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection,
                new System.Windows.Media.Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize,
                System.Windows.Media.Brushes.White, System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
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
        _playback.StillRead += OnStillRead;
    }

    /// <summary>Save was answered: a new copy (false) or in place of the original (true).</summary>
    public event Action<bool>? SaveChosen;

    void AskSave()
    {
        SaveChoice.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => NewCopyButton.Focus(), DispatcherPriority.Input);
    }

    void Choose(bool replace)
    {
        SaveChoice.Visibility = Visibility.Collapsed;
        SaveChosen?.Invoke(replace);
    }

    void OnNewCopy(object sender, RoutedEventArgs e) => Choose(replace: false);

    void OnReplace(object sender, RoutedEventArgs e) => Choose(replace: true);

    void OnSaveCancel(object sender, RoutedEventArgs e) => SaveChoice.Visibility = Visibility.Collapsed;

    void OnSaveChoiceBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SaveChoice)
            SaveChoice.Visibility = Visibility.Collapsed;
    }

    void OnSaveChoiceCard(object sender, MouseButtonEventArgs e) => e.Handled = true;

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

    void OnCaptionsNote(object sender, MouseButtonEventArgs e) => Model.DismissCaptionsNote();

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

    public void SetPicture(BitmapSource? picture) => PreviewImage.Source = picture;

    (Rect Zoom, Size Host) _zoomShown = (VideoZoom.Full, Size.Empty);

    /// <summary>
    /// A zoomed part shows its area: the picture is scaled and clipped (no pixel work), under the item canvas, so texts and
    /// drawings stay put as in the export. The Zoom tool shows the whole picture to draw on.
    /// </summary>
    void ShowZoom()
    {
        var p = Model.Project;
        var zoom = Model.IsZoomMode || !p.HasZoom || p.ToSource(Model.PlayheadOutput) is not { } source ? VideoZoom.Full : p.ZoomAt(source);
        var host = PreviewHost.RenderSize;
        if (_zoomShown == (zoom, host))
            return;
        _zoomShown = (zoom, host);
        if (zoom == VideoZoom.Full)
        {
            PreviewImage.RenderTransform = null;
            PreviewImage.Clip = null;
            return;
        }
        // The image is the picture's own size, centred in the host: its area in its own coordinates.
        var picture = EditGeometry.PictureRect(host, Model.Aspect);
        var area = new Rect(zoom.X * picture.Width, zoom.Y * picture.Height, zoom.Width * picture.Width, zoom.Height * picture.Height);
        double sx = picture.Width / area.Width, sy = picture.Height / area.Height;
        PreviewImage.Clip = new RectangleGeometry(area);
        PreviewImage.RenderTransform = new MatrixTransform(sx, 0, 0, sy, -area.X * sx, -area.Y * sy);
    }

    public void ShowPlaying(bool playing)
    {
        PlayIcon.Text = (playing ? PauseGlyph : PlayGlyph).ToString();
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, App.Text.Get(playing ? "viewer.pause" : "viewer.play"));
    }

    void ShowTime() => TimeText.Text = $"{Clock(Model.PlayheadOutput)} / {Clock(Model.Project.OutputDuration)}";

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    void OnPlay(object sender, RoutedEventArgs e) => Model.KeyCommand("Space");

    /// <summary>The focus came from the keyboard (Tab), not a click: a focused tile then keeps its arrows.</summary>
    bool _focusFromKeyboard;

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        _focusFromKeyboard = Mouse.LeftButton != MouseButtonState.Pressed && Mouse.RightButton != MouseButtonState.Pressed;
    }

    /// <summary>
    /// Arrows stay with the focused control (they move the playhead otherwise): a slider always, and a tile, button or
    /// list reached with the keyboard, so Tab and the arrows can walk through them.
    /// </summary>
    internal static bool KeepsArrows(object? focused, bool fromKeyboard) =>
        focused is Slider || (fromKeyboard && focused is System.Windows.Controls.Primitives.ButtonBase or ComboBox or ListBoxItem);

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
            Model.KeyReleased();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.FocusedElement is TextBox)
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
        if (SaveChoice.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                SaveChoice.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key; // Alt+key comes as a system key
        bool slider = KeepsArrows(Keyboard.FocusedElement, _focusFromKeyboard); // a focused slider (or tile reached by Tab) keeps its arrows
        string? key = (Keyboard.Modifiers, pressed) switch
        {
            (ModifierKeys.None, Key.Space) => "Space",
            (ModifierKeys.None, Key.S) => "S",
            (ModifierKeys.Control, Key.B) => "Ctrl+B",
            (ModifierKeys.None, Key.Q) => "Q",
            (ModifierKeys.None, Key.W) => "W",
            (ModifierKeys.None, Key.Delete or Key.Back) => "Delete",
            (ModifierKeys.Control, Key.Z) => "Ctrl+Z",
            (ModifierKeys.Control, Key.Y) => "Ctrl+Y",
            (ModifierKeys.Control, Key.A) => "Ctrl+A",
            (ModifierKeys.Control, Key.C) => "Ctrl+C",
            (ModifierKeys.Control, Key.V) => "Ctrl+V",
            (ModifierKeys.Control, Key.D) => "Ctrl+D",
            (ModifierKeys.None, Key.Enter) => "Enter",
            (ModifierKeys.None, Key.Left) when !slider => "Left",
            (ModifierKeys.None, Key.Right) when !slider => "Right",
            (ModifierKeys.Shift, Key.Left) when !slider => "Shift+Left",
            (ModifierKeys.Shift, Key.Right) when !slider => "Shift+Right",
            (ModifierKeys.Alt, Key.Left) => "Alt+Left",
            (ModifierKeys.Alt, Key.Right) => "Alt+Right",
            (ModifierKeys.Alt | ModifierKeys.Shift, Key.Left) => "Alt+Shift+Left",
            (ModifierKeys.Alt | ModifierKeys.Shift, Key.Right) => "Alt+Shift+Right",
            (ModifierKeys.None, Key.Up) when !slider => "Up",
            (ModifierKeys.None, Key.Down) when !slider => "Down",
            (ModifierKeys.None, Key.Home) => "Home",
            (ModifierKeys.None, Key.End) => "End",
            (ModifierKeys.Control, Key.OemPlus or Key.Add) => "Ctrl+Plus",
            (ModifierKeys.Control, Key.OemMinus or Key.Subtract) => "Ctrl+Minus",
            (ModifierKeys.None, Key.Oem5 or Key.OemBackslash) => "Backslash",
            (ModifierKeys.Shift, Key.OemQuestion) or (ModifierKeys.None, Key.F1) => "?",
            (ModifierKeys.None, Key.Escape) => "Escape",
            _ => null,
        };
        // With the shortcuts sheet open, only closing it.
        if (Model.ShowingShortcuts && key is not ("Escape" or "?"))
            return;
        if (key != null && Model.KeyCommand(key, e.IsRepeat))
            e.Handled = true;
    }
}
