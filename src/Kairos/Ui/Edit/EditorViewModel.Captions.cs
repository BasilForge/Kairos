using System.Windows;
using System.Windows.Input;
using Kairos.App;
using Kairos.Engine.Captions;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

public sealed partial class EditorViewModel
{
    // ---------------------------------------------------------------- auto captions

    readonly ICaptionMaker? _captions;
    CancellationTokenSource? _captionsRun;
    bool _showingIntro, _captionsBusy;
    double _captionsProgress;
    string _captionsStatus = "";

    /// <summary>A clip with sound (pictures have nothing to hear).</summary>
    public bool CanCaption => _captions != null && !IsPicture && HasSound;
    /// <summary>The explainer's button downloads first when the model is not on this PC.</summary>
    public bool NeedsDownload => _captions is { Ready: false };
    public string IntroButton => Text.Get(NeedsDownload ? "editor.captions.download" : "editor.captions.start");

    bool _askingRecaption;

    /// <summary>Captions are there already: "make them again?" is showing (the new ones would take their place).</summary>
    public bool AskingRecaption { get => _askingRecaption; private set { _askingRecaption = value; Raise(); } }

    public ICommand RecaptionCommand { get; }
    public ICommand KeepCaptionsCommand { get; }

    /// <summary>The explainer is showing: what it does, and "download it?" when the model is not on this PC.</summary>
    public bool ShowingIntro
    {
        get => _showingIntro;
        private set
        {
            _showingIntro = value;
            Raise();
            Raise(nameof(NeedsDownload));
            Raise(nameof(IntroButton));
        }
    }
    public string DownloadQuestion => Text.Format("editor.captions.ask", Math.Max(1, (_captions?.DownloadBytes ?? 0) / (1024 * 1024)));

    public bool CaptionsBusy
    {
        get => _captionsBusy;
        private set
        {
            _captionsBusy = value;
            Raise();
            RaiseCaptionsButton();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>The Captions button shows (not working and nothing to tell)…</summary>
    public bool ShowsCaptionsButton => CanCaption && !_captionsBusy && _captionsStatus.Length == 0;
    /// <summary>…or how it went, in its place for a moment (<see cref="DismissCaptionsNote"/>).</summary>
    public bool HasCaptionsNote => !_captionsBusy && _captionsStatus.Length > 0;

    void RaiseCaptionsButton()
    {
        Raise(nameof(ShowsCaptionsButton));
        Raise(nameof(HasCaptionsNote));
    }

    /// <summary>0..1 while downloading or listening.</summary>
    public double CaptionsProgress { get => _captionsProgress; private set { _captionsProgress = value; Raise(); } }

    /// <summary>What it is doing, or how it went (empty: nothing to say).</summary>
    public string CaptionsStatus
    {
        get => _captionsStatus;
        private set
        {
            _captionsStatus = value;
            Raise();
            RaiseCaptionsButton();
        }
    }

    /// <summary>The button comes back.</summary>
    public void DismissCaptionsNote()
    {
        if (!_captionsBusy)
            CaptionsStatus = "";
    }

    /// <summary>The last run (tests wait on it).</summary>
    public Task CaptionsTask { get; private set; } = Task.CompletedTask;

    public ICommand CaptionsCommand { get; }
    public ICommand IntroStartCommand { get; }
    public ICommand NotNowCommand { get; }
    public ICommand CancelCaptionsCommand { get; }

    /// <summary>Stops a download or a listen (the editor closing, or Cancel).</summary>
    public void CancelCaptions()
    {
        ShowingIntro = false;
        _captionsRun?.Cancel();
    }

    /// <summary>
    /// Downloads the model if agreed, listens to the clip and adds every line as a caption in place of any made before
    /// (one step to undo; typed texts stay); then all the captions are chosen, so one change restyles them all.
    /// </summary>
    async Task MakeCaptions()
    {
        var run = _captionsRun = new CancellationTokenSource();
        CaptionsBusy = true;
        CaptionsProgress = 0;
        try
        {
            if (!_captions!.Ready)
            {
                CaptionsStatus = Text.Format("editor.captions.downloading", 0);
                await _captions!.Download(Reporter("editor.captions.downloading"), run.Token);
                CaptionsProgress = 0;
            }
            CaptionsStatus = Text.Format("editor.captions.listening", 0);
            var lines = await _captions!.Listen(Project.Source, Captions.Streams(Layout), Reporter("editor.captions.listening"), run.Token);
            var items = Captions.ToItems(lines, MeasureText, _textColor, _textSize, _textEffect, _textFont);
            if (items.Count == 0)
            {
                CaptionsStatus = Text.Get("editor.captions.none");
                return;
            }
            var next = Project with { Items = Project.Items.Where(i => i is not TextItem { Group: not null }).ToList() };
            foreach (var item in items)
                next = next.With(item);
            Apply(next);
            _ids.Clear();
            _parts.Clear();
            _ids.AddRange(items.Select(i => i.Id));
            _mode = EditMode.None;
            ChoiceChanged();
            PlayheadOutput = Project.ToOutput(items[0].Start); // the first line on screen
            CaptionsStatus = Text.Format("editor.captions.added", items.Count);
        }
        catch (OperationCanceledException)
        {
            CaptionsStatus = "";
        }
        catch (Exception e) when (e is CaptionException or Engine.Share.ShareException or IOException or UnauthorizedAccessException
                                      or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            CaptionsStatus = Text.Format("editor.captions.failed", e.Message);
        }
        finally
        {
            if (_captionsRun == run)
            {
                _captionsRun = null;
                CaptionsBusy = false;
            }
            run.Dispose();
        }
    }

    IProgress<double> Reporter(string key) => new Progress<double>(value =>
    {
        if (!CaptionsBusy)
            return;
        CaptionsProgress = value;
        CaptionsStatus = Text.Format(key, (int)(value * 100));
    });

    /// <summary>The box a text needs in the picture, with the text settings' size and font.</summary>
    Size MeasureText(string text) =>
        ItemRenderer.BoxSize(new TextItem(Guid.Empty, 0, 0, text, default, _textColor, _textSize, _textEffect, _textFont), new Size(_aspect * 1000, 1000));

    /// <summary>
    /// A restyled text keeps its box when only the colour, style or place changed; new words, a new size or a new font
    /// fit the box to the text, centred where it was and standing on the same line.
    /// </summary>
    TextItem Refit(TextItem styled, TextItem old)
    {
        if (styled.SizeRel == old.SizeRel && styled.Font == old.Font && styled.Text == old.Text)
            return styled;
        var size = ItemRenderer.BoxSize(styled, new Size(_aspect * 1000, 1000));
        double x = Math.Clamp(styled.Box.X + styled.Box.Width / 2 - size.Width / 2, 0, Math.Max(0, 1 - size.Width));
        double y = Math.Clamp(styled.Box.Bottom - size.Height, 0, Math.Max(0, 1 - size.Height));
        return styled with { Box = new Rect(x, y, size.Width, size.Height) };
    }
}
