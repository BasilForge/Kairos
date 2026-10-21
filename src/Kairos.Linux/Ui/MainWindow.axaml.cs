using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kairos.App;
using Kairos.Ui.Pages;

namespace Kairos.Ui;

public partial class MainWindow : Window
{
    readonly SettingsViewModel _vm;
    readonly ClipsViewModel _clips;
    readonly ShareViewModel _share;
    readonly LinuxShareHost _shareHost;
    readonly ConnectionsViewModel _connections;
    ShareOverlay? _shareLayer;
    ClipsPage? _clipsPage;
    readonly Dictionary<string, Func<Control>> _pages;
    string? _current;

    public MainWindow() => throw new NotSupportedException("The settings window needs its view model.");

    public MainWindow(SettingsViewModel vm)
    {
        _vm = vm;
        _connections = new ConnectionsViewModel(new LinuxAccountsHost());
        _shareHost = new LinuxShareHost(vm.RememberShare);
        _share = new ShareViewModel(_shareHost, () => vm.SharePrefs);
        _clips = new ClipsViewModel(() => _vm.EffectiveFolder, () => _vm.HkSave, () => _vm.HkScreenshot);
        InitializeComponent();
        DataContext = vm;
        LibraryBar.DataContext = _clips; // the Library's chosen clips
        _share.PropertyChanged += (_, _) => RelayCommand.RequeryLater();
        _share.PropertyChanged += (_, _) =>
        {
            if (_share.IsOpen)
                GC.KeepAlive(ShareLayer);
            ShareHost.IsVisible = _share.IsOpen;
        };
        _clips.ShareRequested += clip => _share.Open(clip.Path, clip.Thumbnail);
        _share.EditRequested += path => OpenEditor(path, Engine.ClipOrganizer.IsPicture(path), Edit.EditorMode.Share);
        Closed += (_, _) => _share.Close();
        Closed += (_, _) => ShareSites.Shutdown();
        _vm.ShareRequested += path => ShowInFolder(path);
        Title = Text.Get("app");
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://kairos/Assets/icons/kairos-256.png")));
        Wordmark.Source = new Bitmap(AssetLoader.Open(new Uri($"avares://kairos/Ui/Assets/wordmark-{(Text.IsArabic ? "ar" : "en")}.png")));
        Wordmark.Height = Text.IsArabic ? 28 : 22;
        FlowDirection = Text.IsRightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;

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
                _ = _connections.Refresh();
                return new ConnectionsPage { DataContext = _connections };
            },
            ["general"] = () => new GeneralPage(),
        };
        foreach (var (button, page) in new (RadioButton, string)[]
                 {
                     (NavStatus, "status"), (NavClips, "clips"), (NavCapture, "capture"), (NavQuality, "quality"),
                     (NavAudio, "audio"), (NavHotkeys, "hotkeys"), (NavVoice, "voice"), (NavConnections, "connections"), (NavGeneral, "general"),
                 })
        {
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                    ShowPage(page);
            };
            Avalonia.Automation.AutomationProperties.SetName(button, button.Content as string ?? page);
        }
        // Screenshots in the window's own viewer, videos in its own player
        _clips.PictureRequested += ShowPicture;
        _vm.PictureRequested += path => ShowPicture(PictureItem(path));
        PictureLayer.ShareRequested += clip => _share.Open(clip.Path, clip.Thumbnail);
        PictureLayer.ShowRequested += clip => _clips.ShowCommand.Execute(clip);
        PictureLayer.DeleteRequested += clip => _clips.DeleteCommand.Execute(clip);
        PictureLayer.Neighbor = _clips.NextPicture;
        _clips.VideoRequested += clip =>
        {
            try { VideoLayer.Show(clip); }
            catch (Exception e) { Engine.ObsLog.Error("viewer: " + e); }
        };
        VideoLayer.ShareRequested += clip => _share.Open(clip.Path, clip.Thumbnail);
        VideoLayer.ShowRequested += clip => _clips.ShowCommand.Execute(clip);
        VideoLayer.DeleteRequested += clip => _clips.DeleteCommand.Execute(clip);
        Closed += (_, _) => VideoLayer.Hide();
        VideoLayer.EditRequested += clip => OpenEditor(clip.Path, isPicture: false, Edit.EditorMode.Library);
        PictureLayer.EditRequested += picture => OpenEditor(picture.Path, isPicture: true, Edit.EditorMode.Library);
        _jobs = new Edit.EditJobs(RenderEdit, path => App.RecycleBin.Send(path, App.RecycleBin.UserFlags));
        Closed += (_, _) => _jobs.CancelAll();
        // An update closes this window only when nothing here would be lost.
        _vm.InUse = () => _editor != null || _share.IsOpen || _jobs.Busy;
        // A clip or screenshot saved while the Library is open shows at once, marked new.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.LastClipPath) or null)
                OnLastClip(_vm.LastClipPath);
            if (e.PropertyName is nameof(SettingsViewModel.LastShotPath) or null)
                OnLastShot(_vm.LastShotPath);
        };
        vm.NavigateRequested += ShowPage;
        // WPF asks every command whether it can run again after each bit of input; Avalonia only when told.
        vm.PropertyChanged += (_, _) => RelayCommand.RequeryLater();
        AddHandler(PointerReleasedEvent, (_, _) => RelayCommand.RequeryLater(), handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, _) => RelayCommand.RequeryLater(), handledEventsToo: true);
        ShowPage("status");

        // Chrome: drag by the top strip, resize by the edges, double-click to maximize.
        DragBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            if (e.ClickCount == 2)
                ToggleMaximize();
            else
                BeginMoveDrag(e);
        };
        void Edge(Border edge, WindowEdge which) => edge.PointerPressed += (_, e) =>
        {
            if (WindowState == WindowState.Normal && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginResizeDrag(which, e);
        };
        Edge(EdgeLeft, WindowEdge.West);
        Edge(EdgeRight, WindowEdge.East);
        Edge(EdgeTop, WindowEdge.North);
        Edge(EdgeBottom, WindowEdge.South);
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();
    }

    static void ShowInFolder(string path)
    {
        if (File.Exists(path))
            System.Diagnostics.Process.Start(ShellPaths.ShowInFolder(path));
    }

    /// <summary>The share panel, built on first use.</summary>
    ShareOverlay ShareLayer
    {
        get
        {
            if (_shareLayer == null)
            {
                _shareLayer = new ShareOverlay { DataContext = _share };
                ShareHost.Content = _shareLayer;
                _shareHost.Frame += () => _shareLayer.PreviewPicture.InvalidateVisual();
            }
            return _shareLayer;
        }
    }

    /// <summary>Opens the share panel for a clip (the tray's "Share last clip" lands here).</summary>
    public ShareViewModel ShowShare(string clipPath)
    {
        ShowPage("clips");
        var item = _clips.Items.FirstOrDefault(c => string.Equals(c.Path, clipPath, StringComparison.Ordinal));
        _share.Open(clipPath, item?.Thumbnail);
        return _share;
    }

    Edit.EditorView? _editor;
    readonly Edit.EditJobs _jobs;

    /// <summary>
    /// Opens the editor on a clip or screenshot. A clip it cannot read (FFmpeg or the file missing) does not open;
    /// nothing else changes.
    /// </summary>
    public Edit.EditorView? OpenEditor(string path, bool isPicture, Edit.EditorMode mode, Edit.ICaptionMaker? captions = null)
    {
        Engine.Edit.EditProject start;
        double aspect;
        int audioStreams = 0;
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
                start = Engine.Edit.EditProject.For(path, info.Duration);
                using var grabber = new Engine.Share.FrameGrabber(path);
                var frame = grabber.Grab(0, 1280, exact: true);
                still = frame == null ? null : System.Windows.Media.Imaging.BitmapSource.FromBgra(frame.Bgra, frame.Width, frame.Height);
            }
        }
        catch (Exception e) when (e is Engine.Share.ShareException or IOException or InvalidOperationException)
        {
            return null;
        }
        // Auto captions run Whisper in a hidden Kairos process of their own (kairos --captions).
        captions ??= new Edit.CaptionMaker(Engine.Captions.CaptionModel.Default, Engine.Captions.CaptionRuntime.Default, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "kairos"));
        var vm = new Edit.EditorViewModel(start, isPicture, mode, aspect, audioStreams, captions);
        Edit.EditorView view;
        try
        {
            view = new Edit.EditorView(vm);
        }
        catch (Exception e)
        {
            Engine.ObsLog.Error("editor: " + e); // the page stays where it was
            return null;
        }
        view.SetPicture(still);
        vm.Closed += CloseEditor;
        view.SaveChosen += replace => SaveFromEditor(view, vm, replace, mode);
        _editor = view;
        EditorLayer.Content = view;
        EditorLayer.IsVisible = true;
        return view;
    }

    /// <summary>Writes an edit to <paramref name="part"/> (on a background thread; drawing goes to this window's thread).</summary>
    string RenderEdit(Engine.Edit.EditProject p, string part, IProgress<double> progress, CancellationToken ct)
    {
        if (Engine.ClipOrganizer.IsPicture(p.Source))
        {
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() => Edit.PictureComposer.Save(p.Source, p, part));
            return part;
        }
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
                pngs[item.Id] = Avalonia.Threading.Dispatcher.UIThread.Invoke(() => Edit.ItemRenderer.RenderPng(item, info.Width, info.Height, overlays));
            }
            var graph = Engine.Edit.EditGraph.Build(p, info.Width, info.Height, info.Fps, info.AudioStreams, pngs);
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

    /// <summary>The process is ending: stops saves and waits briefly for them to remove their part files.</summary>
    public void FinishEdits() => _jobs.CancelAndWait(TimeSpan.FromSeconds(5));

    /// <summary>
    /// Saves the edit as the user chose (a new copy, or in place of the original) while the editor stays up with its
    /// progress; once it is written the editor goes by itself, back to the Library (which shows the new clip). A cancel
    /// or a failure goes back to editing.
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
            var back = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
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

    /// <summary>Closes the editor at once.</summary>
    internal void CloseEditorNow() => CloseEditor();

    void CloseEditor()
    {
        _editor?.Close();
        _editor = null;
        EditorLayer.Content = null;
        _vm.RefreshCaptionModel(); // the editor may have downloaded it
        EditorLayer.IsVisible = false;
    }

    /// <summary>Shows the screenshot in the window's own viewer.</summary>
    public void ShowPicture(ClipItem picture) => PictureLayer.Show(picture);

    /// <summary>The Library's card for a picture, or one made for it when the page has not listed it yet.</summary>
    ClipItem PictureItem(string path)
    {
        var known = _clips.Items.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.Ordinal));
        if (known != null)
            return known;
        var file = new FileInfo(path);
        return new ClipItem
        {
            Path = path, Name = Path.GetFileNameWithoutExtension(path), App = Engine.ClipLibrary.GroupOf(_vm.EffectiveFolder, path),
            Time = file.Exists ? file.LastWriteTime : DateTime.Now, Size = file.Exists ? file.Length : 0, Kind = Engine.ClipKind.Picture,
        };
    }

    string? _lastClip, _lastShot;

    /// <summary>A clip or recording was saved while the Library is open: only what changed on disk is added.</summary>
    void OnLastClip(string? path)
    {
        if (string.Equals(path, _lastClip, StringComparison.Ordinal))
            return;
        bool first = _lastClip == null;
        _lastClip = path;
        if (!first && _current == "clips")
            _clips.Refresh(live: true);
    }

    /// <summary>A screenshot taken while the Library is open shows at once, like a clip.</summary>
    void OnLastShot(string? path)
    {
        if (string.Equals(path, _lastShot, StringComparison.Ordinal))
            return;
        _lastShot = path;
        if (path != null && _current == "clips")
            _clips.Refresh(live: true);
    }

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void ShowPage(string name)
    {
        if (_current == name || !_pages.TryGetValue(name, out var make))
            return;
        _current = name;
        // The Clips page stays in its own host (hidden on other pages): coming back needs no rebuilding or new layout.
        bool clips = name == "clips";
        ClipsHost.IsVisible = clips;
        PageHost.IsVisible = !clips;
        LibraryBarLayer.IsVisible = clips;
        if (clips)
            ClipsHost.Content = make();
        else
            PageHost.Content = make();
        Scroller.ScrollToHome();
        var nav = name switch
        {
            "status" => NavStatus, "clips" => NavClips, "capture" => NavCapture, "quality" => NavQuality, "audio" => NavAudio,
            "hotkeys" => NavHotkeys, "voice" => NavVoice, "connections" => NavConnections, _ => NavGeneral,
        };
        if (nav.IsChecked != true)
            nav.IsChecked = true;
    }
}
