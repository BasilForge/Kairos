using System.Diagnostics;
using System.Windows.Media.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kairos.App;
using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>The share panel's link to the real world: FFmpeg next to Kairos, the clipboard, the file manager, the sharing browser.</summary>
public sealed class LinuxShareHost(Action<ShareSettings> remember) : IShareHost
{
    public string Folder => ShareFiles.DefaultFolder;

    /// <summary>The playing picture changed (the panel redraws it).</summary>
    public event Action? Frame;

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
        var timer = new DispatcherTimer { Interval = delay };
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

    /// <summary>The file (as a file, so a paste in a file manager or a chat window gets it) and/or text on the clipboard.</summary>
    public void CopyToClipboard(string? file, string? text) => _ = CopyAsync(file, text);

    async Task CopyAsync(string? file, string? text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault();
        if (window?.Clipboard is not { } clipboard)
        {
            Engine.ObsLog.Warn("share clipboard: none");
            return;
        }
        try
        {
            var data = new DataTransfer();
            if (file != null && await window.StorageProvider.TryGetFileFromPathAsync(new Uri(file)) is { } item)
                data.Add(DataTransferItem.CreateFile(item));
            if (!string.IsNullOrEmpty(text))
                data.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(data);
            Engine.ObsLog.Info("share clipboard: set " + file);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UriFormatException)
        {
            Engine.ObsLog.Warn("share clipboard: " + e.Message);
        }
    }

    public void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct)
    {
        if (ShareSites.Available)
            return ShareSites.Publish(platform, file, caption, status, ct);
        // No Chromium-family browser: the site in the normal browser with the file on the clipboard, finished by hand.
        CopyToClipboard(file, null);
        Launch(SharePlanner.OpenTarget(platform, caption));
        return Task.FromResult(new PublishOutcome(PublishResult.NeedsUser, Detail: "browser"));
    }

    public void RevealSite(SharePlatform platform) => ShareSites.Reveal(platform);

    public void ShowInFolder(string path)
    {
        if (File.Exists(path))
            Process.Start(Kairos.App.ShellPaths.ShowInFolder(path));
    }

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
    readonly Edit.PlayerSession _session = new();
    bool _hooked;
    double _volume = 1;

    public BitmapSource? Play(string clip, double from, double to, int width, int height, Action<double> position, Action<string?> ended)
    {
        if (!_hooked)
        {
            _session.Frame += () => Frame?.Invoke();
            _hooked = true;
        }
        _session.Volume = _volume;
        return _session.Start(clip, from, to, width, height, position, ended);
    }

    public void SetPlaybackVolume(double volume)
    {
        _volume = volume;
        _session.Volume = volume;
    }

    public void StopPlaying() => _session.Stop();

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
                        image = BitmapSource.FromBgra(frame.Bgra, frame.Width, frame.Height);
                }
                catch (Exception e) when (e is ShareException or InvalidOperationException or IOException)
                {
                    Engine.ObsLog.Warn("share frames: " + e.Message);
                }
                Post(() => work.Done(index, image));
            }
        }
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>Below-normal priority so a game running at the same time keeps the CPU.</summary>
    public void Run(Action work) =>
        new Thread(() => work()) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Kairos share" }.Start();
}
