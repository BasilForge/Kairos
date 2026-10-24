using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kairos.App;
using Kairos.Ui.Pages;

namespace Kairos.Ui;

public partial class MainWindow : Window
{
    readonly SettingsViewModel _vm;
    readonly ClipsViewModel _clips;
    readonly ShareViewModel _share;
    readonly ConnectionsViewModel _accounts;
    readonly Dictionary<string, Func<FrameworkElement>> _pages;
    ClipsPage? _clipsPage;
    string? _current;

    /// <param name="shareHost">Replaces the real FFmpeg/clipboard/browser link (used by --render).</param>
    /// <param name="accountsHost">Replaces the site sign-ins in Kairos's browser profile (used by --render).</param>
    public MainWindow(SettingsViewModel vm, IShareHost? shareHost = null, IAccountsHost? accountsHost = null)
    {
        _vm = vm;
        _clips = new ClipsViewModel(() => _vm.EffectiveFolder, () => _vm.HkSave, () => _vm.HkScreenshot);
        shareHost ??= new WpfShareHost(_vm.RememberShare, () => _vm.EffectiveFolder);
        _share = new ShareViewModel(shareHost, () => _vm.SharePrefs);
        _clips.Copies = new PlayableCopies(shareHost);
        _accounts = new ConnectionsViewModel(accountsHost ?? new WpfAccountsHost(this));
        SettingsApp.StartupTrace?.Invoke("window models");
        InitializeComponent();
        SettingsApp.StartupTrace?.Invoke("window xaml");
        DataContext = vm;
        LibraryBar.DataContext = _clips; // the Library's chosen clips
        // All sizes in one .ico, so Windows picks the right one for the taskbar, Alt+Tab and the title.
        Icon = AppIcon ??= System.Windows.Media.Imaging.BitmapFrame.Create(new MemoryStream(AppIconArt.Ico()),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        SettingsApp.StartupTrace?.Invoke("window icon");
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.LastClipPath) or null)
                OnLastClip(_vm.LastClipPath);
            if (e.PropertyName is nameof(SettingsViewModel.LastShotPath) or null)
                OnLastShot(_vm.LastShotPath);
        };
        _share.PropertyChanged += (_, _) =>
        {
            if (_share.IsOpen)
                GC.KeepAlive(ShareLayer); // built now if the window has not been idle yet
            ShareHost.Visibility = _share.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        };
        // The share panel's XAML is the costliest piece of the window: it is built once the window is up and idle, so the
        // window appears sooner and the panel is ready before anyone opens it.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => GC.KeepAlive(ShareLayer));
        _clips.ShareRequested += clip => _share.Open(clip.Path, clip.Thumbnail);
        _clips.PictureRequested += ShowPicture;
        PictureLayer.ShareRequested += clip => _share.Open(clip.Path, clip.Thumbnail);
        PictureLayer.ShowRequested += clip => _clips.ShowCommand.Execute(clip);
        PictureLayer.DeleteRequested += clip => _clips.DeleteCommand.Execute(clip);
        PictureLayer.Neighbor = _clips.NextPicture;
        // Videos play in Kairos's own player (with Edit)
        _clips.VideoRequested += clip => VideoLayer.Show(clip);
        VideoLayer.ShareRequested += clip => _share.Open(clip.Path, clip.Thumbnail);
        VideoLayer.ShowRequested += clip => _clips.ShowCommand.Execute(clip);
        VideoLayer.DeleteRequested += clip => _clips.DeleteCommand.Execute(clip);
        Closed += (_, _) => VideoLayer.Hide();
        VideoLayer.EditRequested += clip => OpenEditor(clip.Path, isPicture: false, Edit.EditorMode.Library);
        PictureLayer.EditRequested += picture => OpenEditor(picture.Path, isPicture: true, Edit.EditorMode.Library);
        // Share: the editor on the shared clip; its saved edit is shared from then on
        _share.EditRequested += path => OpenEditor(path, Engine.ClipOrganizer.IsPicture(path), Edit.EditorMode.Share);
        _jobs = new Edit.EditJobs(RenderEdit, path => App.RecycleBin.Send(path, App.RecycleBin.UserFlags))
        {
            Saved = path => App.CloudFolders.Saved(path, _vm.FreeLocalCopies),
        };
        Closed += (_, _) => _jobs.CancelAll();
        // An update closes this window only when nothing here would be lost.
        _vm.InUse = () => _editor != null || _share.IsOpen || _jobs.Busy;
        // The Status page's Latest card
        _vm.ShareRequested += path => ShowShare(path);
        _vm.PictureRequested += path => ShowPicture(PictureItem(path));
        Closed += (_, _) => _share.Close();
        // A key typed into a hotkey box is not run by the engine meanwhile (it listens for keys, it does not reserve them).
        Action<bool> typing = _vm.SetTypingHotkey;
        HotkeyBox.TypingChanged += typing;
        Closed += (_, _) => HotkeyBox.TypingChanged -= typing;
        // Closed with unsaved changes: "save them?" first, never dropped without a word.
        bool answered = false;
        Closing += (_, e) =>
        {
            if (!answered && _vm.AskBeforeClose())
                e.Cancel = true;
        };
        Action<bool> close = save =>
        {
            answered = true;
            Hide(); // gone at once, while the engine is told
            try
            {
                if (save)
                    _vm.SaveBeforeClose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not saved (the file held, the disk full): the window comes back with its changes, never left hidden.
                Engine.ObsLog.Warn($"settings: not saved as the window closed: {e.Message}");
                answered = false;
                Show();
                return;
            }
            Close();
        };
        _vm.CloseAnswered += close;
        Closed += (_, _) => _vm.CloseAnswered -= close;
        _vm.SetTypingHotkey(false); // nothing left over from a window that closed while typing
        Title = Text.Get("app");
        // Drawn once from Fraunces (Kai + italic ro) and Aref Ruqaa (كا + slanted يرو), six times the size shown.
        Wordmark.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri($"pack://application:,,,/Kairos;component/Ui/Assets/wordmark-{(Text.IsArabic ? "ar" : "en")}.png"));
        Wordmark.Height = Text.IsArabic ? 28 : 22;
        FlowDirection = Text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        SettingsApp.StartupTrace?.Invoke("window wiring");

        _pages = new()
        {
            ["status"] = () => new StatusPage(),
            ["clips"] = () => { _clips.Refresh(); return _clipsPage ??= new ClipsPage { DataContext = _clips }; },
            ["capture"] = () => new CapturePage(),
            ["quality"] = () => new QualityPage(),
            ["audio"] = () => new AudioPage(),
            ["hotkeys"] = () => new HotkeysPage(),
            ["voice"] = () => new SmartSavePage(),
            ["connections"] = () =>
            {
                _ = _accounts.Refresh();
                return new ConnectionsPage { DataContext = _accounts };
            },
            ["general"] = () => new GeneralPage(),
        };

        foreach (var (button, page) in new (RadioButton, string)[]
                 {
                     (NavStatus, "status"), (NavClips, "clips"), (NavCapture, "capture"), (NavQuality, "quality"),
                     (NavAudio, "audio"), (NavHotkeys, "hotkeys"), (NavVoice, "voice"), (NavConnections, "connections"), (NavGeneral, "general"),
                 })
        {
            button.Checked += (_, _) => ShowPage(page);
            System.Windows.Automation.AutomationProperties.SetName(button, button.Content as string ?? page);
        }
        ShowPage("status");
        SettingsApp.StartupTrace?.Invoke("window status page");
        vm.NavigateRequested += ShowPage;
    }

    ShareOverlay? _shareLayer;

    /// <summary>The share panel, built on first use (see the constructor).</summary>
    ShareOverlay ShareLayer
    {
        get
        {
            if (_shareLayer == null)
            {
                _shareLayer = new ShareOverlay { DataContext = _share };
                _share.CopySizeAsked += _shareLayer.ShowCopySizes;
                ShareHost.Content = _shareLayer;
            }
            return _shareLayer;
        }
    }

    /// <summary>The share panel's emoji picker, out of its popup (for --render share-emoji).</summary>
    internal FrameworkElement TakeEmojiPicker() => ShareLayer.TakeEmojiPicker();

    /// <summary>Copy's sizes, placed under its tile (for --render share-copysizes).</summary>
    internal System.Windows.Controls.ContextMenu CopySizes() => ShareLayer.CopySizes();

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        // A maximized custom-chrome window extends past the screen by the resize border; pad it back in.
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, round = 2 /* DWMWCP_ROUND */, border = 0x0041424E; // COLORREF of #4E4241 (BGR), the Stroke color
        DwmSetWindowAttribute(hwnd, 20 /* USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* BORDER_COLOR */, ref border, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Opens the share panel for a clip (the tray's "Share last clip" lands here), on the Clips page.</summary>
    public ShareViewModel ShowShare(string clipPath)
    {
        ShowPage("clips");
        var item = _clips.Items.FirstOrDefault(c => string.Equals(c.Path, clipPath, StringComparison.OrdinalIgnoreCase));
        _share.Open(clipPath, item?.Thumbnail);
        if (_share.Thumbnail == null)
            ShellThumbnail.Request(clipPath, image => Dispatcher.BeginInvoke(() => _share.Thumbnail ??= image));
        return _share;
    }

    /// <summary>Shows a screenshot in the window's own viewer.</summary>
    public void ShowPicture(ClipItem picture) => PictureLayer.Show(picture);

    Edit.EditorView? _editor;

    /// <summary>
    /// Opens the editor on a clip or screenshot (also for --render editor-*). A clip it cannot read (FFmpeg or the file
    /// missing) does not open; nothing else changes.
    /// </summary>
    /// <param name="captions">What Auto captions use (the render harness passes a stand-in).</param>
    public Edit.EditorView? OpenEditor(string path, bool isPicture, Edit.EditorMode mode, Edit.ICaptionMaker? captions = null)
    {
        Engine.Edit.EditProject start;
        double aspect;
        int audioStreams = 0;
        Engine.Audio.TrackLayout? layout = null;
        System.Windows.Media.Imaging.BitmapSource? still;
        try
        {
            if (isPicture)
            {
                still = PictureClipboard.TryLoad(path);
                if (still == null)
                    return null;
                aspect = still.PixelWidth / (double)Math.Max(1, still.PixelHeight);
                start = Engine.Edit.EditProject.For(path, 0);
            }
            else
            {
                var info = Engine.Share.ClipTranscoder.Probe(path);
                aspect = info.Width / (double)Math.Max(1, info.Height);
                audioStreams = info.AudioStreams;
                layout = Engine.Audio.TrackLayout.Of(info);
                start = Engine.Edit.EditProject.For(path, info.Duration);
                using var grabber = new Engine.Share.FrameGrabber(path);
                var frame = grabber.Grab(0, 1280, exact: true);
                still = frame == null ? null : System.Windows.Media.Imaging.BitmapSource.Create(frame.Width, frame.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, frame.Bgra, frame.Width * 4);
            }
        }
        catch (Exception e) when (e is Engine.Share.ShareException or IOException or InvalidOperationException)
        {
            return null;
        }
        // Auto captions run Whisper in a hidden Kairos process of their own (Kairos.exe --captions).
        captions ??= new Edit.CaptionMaker(Engine.Captions.CaptionModel.Default, Engine.Captions.CaptionRuntime.Default, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Kairos.exe"));
        var vm = new Edit.EditorViewModel(start, isPicture, mode, aspect, audioStreams, captions, layout);
        var view = new Edit.EditorView(vm);
        view.SetPicture(still);
        vm.Closed += CloseEditor;
        view.SaveChosen += replace => SaveFromEditor(view, vm, replace, mode);
        _editor = view;
        EditorLayer.Content = view;
        EditorLayer.Visibility = Visibility.Visible;
        return view;
    }

    readonly Edit.EditJobs _jobs;

    /// <summary>The process is ending (the window's dispatcher has stopped): stops saves and waits briefly for them to remove their part files.</summary>
    public void FinishEdits() => _jobs.CancelAndWait(TimeSpan.FromSeconds(5));

    /// <summary>Writes an edit to <paramref name="part"/> (on a background thread; WPF drawing goes to this window's thread).</summary>
    string RenderEdit(Engine.Edit.EditProject p, string part, IProgress<double> progress, CancellationToken ct)
    {
        if (Engine.ClipOrganizer.IsPicture(p.Source))
            return Dispatcher.Invoke(() => Edit.PictureComposer.Save(p.Source, p, part));
        var info = Engine.Share.ClipTranscoder.Probe(p.Source);
        string overlays = Path.Combine(Path.GetTempPath(), "Kairos", "edit-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Each drawn item as a small transparent picture (FFmpeg lays it over in its place and time), one at a time on
            // the window's thread so the window keeps answering between them.
            var pngs = new Dictionary<Guid, Engine.Edit.Overlay>();
            foreach (var item in p.Items.Where(i => i is not Engine.Edit.BlurItem && p.Visible(i).Any()))
            {
                ct.ThrowIfCancellationRequested();
                pngs[item.Id] = Dispatcher.Invoke(() => Edit.ItemRenderer.RenderPng(item, info.Width, info.Height, overlays));
            }
            var graph = Engine.Edit.EditGraph.Build(p, info.Width, info.Height, info.Fps, info.AudioStreams, pngs,
                Engine.Audio.TrackLayout.Of(info));
            var codec = info.VideoCodec switch { "av1" => Settings.VideoCodec.Av1, "hevc" => Settings.VideoCodec.Hevc, _ => Settings.VideoCodec.H264 };
            var plan = Engine.EncoderPresets.Plan(codec, _vm.Quality, info.Width, info.Height, (int)Math.Round(info.Fps), _vm.CustomCq, _vm.CustomMbps,
                nvencAvailable: true);
            return Engine.Edit.EditExporter.Export(p.Source, part, graph, plan, progress, ct).Path;
        }
        finally
        {
            try
            {
                if (Directory.Exists(overlays))
                    Directory.Delete(overlays, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Saves the edit as the user chose (a new copy, or in place of the original) while the editor stays up with its
    /// progress; once it is written the editor goes by itself, back to where it was opened: the Library (which shows the
    /// new clip), or the share panel, which shares the saved clip from then on. A cancel or a failure goes back to editing.
    /// </summary>
    void SaveFromEditor(Edit.EditorView view, Edit.EditorViewModel vm, bool replace, Edit.EditorMode mode)
    {
        var project = vm.Project;
        view.Suspend(); // the preview lets go of the clip (replacing it needs that)
        var job = _jobs.Start(project, replace, releaseSource: () =>
        {
            VideoLayer.Hide();
            PictureLayer.Hide();
            _share.Release();
        });
        vm.BeginSaving(replace, job.Cancel);
        job.ProgressChanged += vm.ReportSaving;
        job.Completed += saved =>
        {
            vm.FinishSaving();
            _clips.Refresh(live: true); // the new clip shows as new
            // A moment for the full ring and its check, then back where the editor came from (nothing to press).
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            back.Tick += (_, _) =>
            {
                back.Stop();
                if (_editor != view)
                    return;
                CloseEditor();
                if (mode == Edit.EditorMode.Share)
                    _share.UseSaved(saved, project.Source);
                else
                {
                    VideoLayer.Hide();
                    PictureLayer.Hide();
                    ShowPage("clips");
                }
            };
            back.Start();
        };
        job.Failed += why =>
        {
            view.Resume();
            if (why.Length == 0)
                vm.EndSaving(); // cancelled: back to editing
            else
                vm.FailSaving(why);
        };
    }

    /// <summary>Closes the editor at once (the --perf harness; the user leaves it with Back).</summary>
    internal void CloseEditorNow() => CloseEditor();

    void CloseEditor()
    {
        _editor?.Close();
        _editor = null;
        EditorLayer.Content = null;
        _vm.RefreshCaptionModel(); // the editor may have downloaded it
        EditorLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>A clip in Kairos's own player (also for --render video-viewer).</summary>
    public void ShowVideo(ClipItem video) => VideoLayer.Show(video);

    /// <summary>Shows the screenshot at <paramref name="path"/>, as its Library card when it is listed.</summary>
    public void ShowPicture(string path) => PictureLayer.Show(PictureItem(path));

    /// <summary>The Library's card for a picture, or one made for it when the page has not listed it yet.</summary>
    ClipItem PictureItem(string path)
    {
        var known = _clips.Items.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (known != null)
            return known;
        var file = new FileInfo(path);
        return new ClipItem
        {
            Path = path, Name = Path.GetFileNameWithoutExtension(path), App = Engine.ClipLibrary.GroupOf(_vm.EffectiveFolder, path),
            Time = file.Exists ? file.LastWriteTime : DateTime.Now, Size = file.Exists ? file.Length : 0, Kind = Engine.ClipKind.Picture,
        };
    }

    /// <summary>Kairos's mark (the sign-in window uses it too).</summary>
    public static System.Windows.Media.ImageSource? AppIcon { get; private set; }

    string? _lastClip;

    /// <summary>
    /// A clip or recording was saved (or the last one deleted) while the Clips page is open: it shows at once, marked
    /// new. Only what changed on disk is added (the scan takes a few milliseconds).
    /// </summary>
    void OnLastClip(string? path)
    {
        if (string.Equals(path, _lastClip, StringComparison.OrdinalIgnoreCase))
            return;
        bool first = _lastClip == null;
        _lastClip = path;
        if (!first && _current == "clips")
            _clips.Refresh(live: true);
    }

    string? _lastShot;

    /// <summary>A screenshot taken while the Clips page is open shows at once, like a clip.</summary>
    void OnLastShot(string? path)
    {
        if (string.Equals(path, _lastShot, StringComparison.OrdinalIgnoreCase))
            return;
        // Unlike clips, the engine reports none until one is taken, so any new one is news.
        _lastShot = path;
        if (path != null && _current == "clips")
            _clips.Refresh(live: true);
    }

    /// <summary>The Library's view model (the render harness chooses cards with it).</summary>
    internal ClipsViewModel Library => _clips;

    public void ShowPage(string page)
    {
        if (_current == page)
            return;
        _current = page;
        _vm.OnStatusPage = page == "status";
        var content = _pages[page]();
        // The Clips page is kept, laid out, in its own host: coming back shows the same cards at once (its Refresh only
        // adds or drops what changed on disk). Other pages are light and made fresh.
        bool clips = ReferenceEquals(content, _clipsPage);
        if (clips)
            ClipsHost.Content ??= content;
        else
            PageHost.Content = content;
        PageHost.Visibility = clips ? Visibility.Collapsed : Visibility.Visible;
        ClipsHost.Visibility = clips ? Visibility.Visible : Visibility.Collapsed;
        LibraryBarLayer.Visibility = ClipsHost.Visibility; // the chosen clips' bar floats over the Library only
        if (clips)
            PageHost.Content = null;
        Scroller.ScrollToTop();
        foreach (var child in Nav.Children.OfType<RadioButton>())
            if (child.Name.Equals("Nav" + page, StringComparison.OrdinalIgnoreCase))
                child.IsChecked = true;
    }
}
