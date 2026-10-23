using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>The share panel's link to the real world: OBS's FFmpeg next to Kairos, the clipboard, the browser.</summary>
public sealed class WpfShareHost(Action<ShareSettings> remember, Func<string> clipsFolder) : IShareHost
{
    public string Folder => ShareFiles.DefaultFolder;

    /// <summary>The clips folder's Shared (the default clips folder before the engine has said which one it uses).</summary>
    public string SharedFolder => ShareFiles.SharedIn(clipsFolder() is { Length: > 0 } clips ? clips : OutputSettings.DefaultFolder);

    public MediaInfo Probe(string clip) => ClipTranscoder.Probe(clip);

    public ShareResult Transcode(string source, string target, SharePlan plan, long maxBytes, IProgress<double> progress, CancellationToken ct)
    {
        ShareFiles.Clean(Folder, DateTime.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return ClipTranscoder.Transcode(source, target, plan, maxBytes, progress, ct);
    }

    public ShareResult Cut(string source, string target, double start, double end, IProgress<double> progress, CancellationToken ct)
    {
        ShareFiles.Clean(Folder, DateTime.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return ClipCutter.Cut(source, target, start, end, progress, ct);
    }

    public void After(TimeSpan delay, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    public SharePicture? ReadPicture(string path) =>
        PictureClipboard.TryLoad(path) is { } image && File.Exists(path)
            ? new SharePicture(image, image.PixelWidth, image.PixelHeight, new FileInfo(path).Length)
            : null;

    public ShareResult MakeJpeg(string source, string target, long maxBytes) => PictureJpeg.Make(source, target, maxBytes);

    public void CopyPicture(string path) => PictureClipboard.Copy(path);

    public void CopyToClipboard(string? file, string? text)
    {
        var data = new DataObject();
        if (file != null)
            data.SetFileDropList(new StringCollection { file });
        if (!string.IsNullOrEmpty(text))
            data.SetText(text, TextDataFormat.UnicodeText);
        // Another app can hold the clipboard for a moment; a few short retries cover it.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
        }
    }

    public void Launch(string target) => App.ShellPaths.Open(target);

    public Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct)
    {
        if (ShareSites.Available)
            return ShareSites.Publish(platform, file, caption, status, ct);
        // No WebView2 runtime: the site in the normal browser with the file on the clipboard, finished by hand.
        CopyToClipboard(file, null);
        Launch(SharePlanner.OpenTarget(platform, caption));
        return Task.FromResult(new PublishOutcome(PublishResult.NeedsUser, Detail: "browser"));
    }

    public void RevealSite(SharePlatform platform) => ShareSites.Reveal(platform);

    public void ShowInFolder(string path) => App.ShellPaths.ShowInExplorer(path);

    public void Remember(ShareSettings prefs) => remember(prefs);

    // Frames for the trim bar and preview: one background reader, the newest preview first, the strip in between.
    sealed class FrameWork(string clip, double[] times, int width, bool exact, Action<int, BitmapSource?> done)
    {
        public readonly string Clip = clip;
        public readonly double[] Times = times;
        public readonly int Width = width;
        public readonly bool Exact = exact;
        public readonly Action<int, BitmapSource?> Done = done;
        public int Next;
    }

    readonly object _frameLock = new();
    readonly AutoResetEvent _frameSignal = new(false);
    FrameWork? _preview, _strip;
    bool _release;
    Thread? _frameThread;
    FrameGrabber? _grabber;
    string? _grabberClip;

    public void GrabFrames(string clip, double[] times, int width, bool exact, Action<int, BitmapSource?> done)
    {
        lock (_frameLock)
        {
            var work = new FrameWork(clip, times, width, exact, done);
            if (times.Length == 1)
                _preview = work;
            else
                _strip = work;
            _release = false;
            _frameThread ??= new Thread(FrameLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Kairos frames" };
            if (!_frameThread.IsAlive)
                _frameThread.Start();
        }
        _frameSignal.Set();
    }

    // Watching the cut: one player at a time, its pictures shown on each screen refresh.
    PreviewPlayer? _player;

    public BitmapSource? Play(string clip, double from, double to, int width, int height, Action<double> position, Action<string?> ended)
    {
        StopPlaying();
        try
        {
            ClipTranscoder.RequireLoaded();
        }
        catch (ShareException e)
        {
            ended(e.Message);
            return null;
        }
        PreviewPlayer? player = null;
        player = new PreviewPlayer(clip, from, to, width, height, mute: false, position, failure =>
        {
            if (_player == player)
                StopPlaying();
            ended(failure);
        });
        player.Volume = _volume;
        _player = player;
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        return player.Picture;
    }

    void OnRendering(object? sender, EventArgs e) => _player?.Tick();

    double _volume = 1;

    public void SetPlaybackVolume(double volume)
    {
        _volume = volume;
        if (_player != null)
            _player.Volume = volume;
    }

    public void StopPlaying()
    {
        System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        var player = _player;
        _player = null;
        player?.Dispose();
    }

    public void ReleaseFrames()
    {
        lock (_frameLock)
        {
            _preview = _strip = null;
            _release = true;
        }
        _frameSignal.Set();
    }

    void FrameLoop()
    {
        while (true)
        {
            _frameSignal.WaitOne();
            while (true)
            {
                FrameWork? work;
                int index;
                lock (_frameLock)
                {
                    if (_release)
                    {
                        _grabber?.Dispose();
                        _grabber = null;
                        _grabberClip = null;
                        _release = false;
                    }
                    if (_preview != null)
                    {
                        work = _preview;
                        index = 0;
                        _preview = null;
                    }
                    else if (_strip != null && _strip.Next < _strip.Times.Length)
                    {
                        work = _strip;
                        index = _strip.Next++;
                    }
                    else
                        break;
                }
                BitmapSource? image = null;
                try
                {
                    if (_grabberClip != work.Clip)
                    {
                        _grabber?.Dispose();
                        _grabber = null;
                        _grabber = new FrameGrabber(work.Clip);
                        _grabberClip = work.Clip;
                    }
                    if (_grabber!.Grab(work.Times[index], work.Width, work.Exact) is { } frame)
                    {
                        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                            frame.Bgra, frame.Width * 4);
                        bitmap.Freeze();
                        image = bitmap;
                    }
                }
                catch (Exception e) when (e is ShareException or InvalidOperationException or IOException)
                {
                    Engine.ObsLog.Warn("share frames: " + e.Message);
                }
                Post(() => work.Done(index, image));
            }
        }
    }

    public void Post(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

    /// <summary>Below-normal priority so a game running at the same time keeps the CPU.</summary>
    public void Run(Action work) =>
        new Thread(() => work()) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Kairos share" }.Start();
}
