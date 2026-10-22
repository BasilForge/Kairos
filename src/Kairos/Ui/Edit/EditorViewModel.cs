using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Kairos.App;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

public enum EditorMode { Library, Share }

/// <summary>What a press on the picture does: choose and move things (None), or draw lines, a shape, a blur box, a crop.</summary>
public enum EditMode { None, Draw, Shapes, Blur, Crop, Zoom }

/// <summary>A row of the timeline: the clip's parts, then one row per kind of item (shown only when it has some). Captions
/// and typed texts are all texts: one row.</summary>
public enum TimelineRow { Video, Text, Draw, Shapes }

/// <summary>What the inspector shows: the whole clip, the chosen parts or items, or the brush of a mode.</summary>
public enum InspectorPage { Clip, Part, Text, Line, Shape, Blur, Mixed, Crop }

/// <summary>
/// The editor's state: the edit with its undo history, what is chosen (items, or parts of the clip), the mode a press on
/// the picture is in, and the playhead. The inspector shows the chosen things' own settings and a change applies to all
/// of them, only what was changed; with nothing chosen it shows the whole clip. The primary button saves: it asks how
/// (<see cref="SaveAsked"/>) and the editor stays, showing the progress, until the edit is written.
/// </summary>
public sealed partial class EditorViewModel : INotifyPropertyChanged
{
    readonly EditHistory _history;
    readonly double _aspect;
    readonly List<Guid> _ids = [];
    readonly List<int> _parts = [];
    /// <summary>Where a Shift-click's range starts: the item or part last chosen by itself.</summary>
    Guid? _anchor;
    int _partAnchor = -1;
    /// <summary>What Ctrl+C copied (pasted with Ctrl+V).</summary>
    IReadOnlyList<EditItem> _clipboard = [];
    EditMode _mode;
    double _playhead, _zoom = 1;
    string? _lastAmend;

    /// <param name="captions">What the Captions button uses (none: no button).</param>
    /// <param name="layout">The clip's sound tracks (none: read from <paramref name="audioStreams"/> as a clip without names).</param>
    public EditorViewModel(EditProject start, bool isPicture, EditorMode mode, double aspect, int audioStreams, ICaptionMaker? captions = null,
        Engine.Audio.TrackLayout? layout = null)
    {
        Layout = layout ?? Engine.Audio.TrackLayout.Of([], audioStreams);
        _captions = captions;
        _history = new EditHistory(start);
        _history.Changed += () =>
        {
            Prune(); // after an undo, what is gone is no longer chosen
            LoadStyle();
            RaiseAll();
            CommandManager.InvalidateRequerySuggested();
        };
        IsPicture = isPicture;
        Mode = mode;
        _aspect = aspect;
        HasSeparateTracks = Layout.PartitionTracks.Count > 0;
        // The clip's own tracks by name, for the clip and for a part; a one-track clip's part: its whole sound.
        var own = HasSeparateTracks
            ? Layout.PartitionTracks.Select((t, j) => (j, SoundNames.Name(Layout.Roles[t], Layout.Labels[t]), SoundNames.Glyph(Layout.Roles[t]))).ToList()
            : [];
        ClipTracks = [.. own.Select(t => new SoundTrackView(this, t.j, t.Item2, t.Item3, part: false))];
        PartTracks = HasSeparateTracks ? [.. own.Select(t => new SoundTrackView(this, t.j, t.Item2, t.Item3, part: true))]
            : [new SoundTrackView(this, 0, Text.Get("editor.allSound"), SoundNames.Glyph(Engine.Audio.TrackRole.Mix), part: true)];
        HasSound = audioStreams > 0;
        TextSwatches = Swatches(hex => TextColor = hex, _textColor);
        PenSwatches = Swatches(hex => PenColor = hex, _penColor);
        ShapeSwatches = Swatches(hex => ShapeColor = hex, _shapeColor);
        MixedSwatches = Swatches(hex => MixedColor = hex, "");
        Fonts = TextFonts.All.Select(f => new FontChoice(f, key => TextFont = key)).ToList();
        Looks = Enum.GetValues<ColourLook>().Select(l => new LookChoice(l, Text.Get($"editor.look.{l.ToString().ToLowerInvariant()}"),
            look => LookCommand!.Execute(look))).ToList();
        PartLooks = Enum.GetValues<ColourLook>().Select(l => new LookChoice(l, Text.Get($"editor.look.{l.ToString().ToLowerInvariant()}"),
            look => Apply(Project.WithParts(_parts, k => k with { Look = look, LookStrength = 1 })))).ToList();
        Speeds = SpeedSteps.Select(s => new SpeedChoice(s, v => Apply(Project.WithParts(_parts, k => k with { Speed = v })))).ToList();
        ClipSpeeds = SpeedSteps.Select(s => new SpeedChoice(s, v => ClipSpeed = v)).ToList();
        RefreshChoices();

        UndoCommand = new RelayCommand(_ => { _lastAmend = null; _history.Undo(); }, _ => _history.CanUndo);
        RedoCommand = new RelayCommand(_ => { _lastAmend = null; _history.Redo(); }, _ => _history.CanRedo);
        // Save asks how (a new copy or in place of the original), from the Library and the share panel alike.
        PrimaryCommand = new RelayCommand(_ => SaveAsked?.Invoke(), _ => !Project.IsUnchanged && !_saving);
        CancelSaveCommand = new RelayCommand(_ => _cancelSave?.Invoke(), _ => _saving && !_saveDone && _saveError.Length == 0);
        BackToEditingCommand = new RelayCommand(_ => EndSaving());
        CloseCommand = new RelayCommand(_ =>
        {
            if (_saving)
                return; // the edit is being written: the editor stays until it is
            // Back or Esc: changes are only thrown away when the user says so.
            if (Project.IsUnchanged)
                Closed?.Invoke();
            else
                AskingDiscard = true;
        });
        DiscardCommand = new RelayCommand(_ =>
        {
            AskingDiscard = false;
            Closed?.Invoke();
        });
        KeepEditingCommand = new RelayCommand(_ => AskingDiscard = false);
        ResetCommand = new RelayCommand(_ => AskingReset = true, _ => !Project.IsUnchanged && !_saving);
        ConfirmResetCommand = new RelayCommand(_ =>
        {
            AskingReset = false;
            Apply(EditProject.For(Project.Source, Project.Duration)); // one step: Ctrl+Z brings every edit back
        });
        CancelResetCommand = new RelayCommand(_ => AskingReset = false);
        SplitCommand = new RelayCommand(_ => Split(), _ => HasTimeline);
        DeleteCommand = new RelayCommand(_ => DeleteChosen(), _ => _ids.Count > 0 || (_parts.Count > 0 && Project.Kept.Count > _parts.Count));
        LookCommand = new RelayCommand(p => Apply(Project.WithLook((ColourLook)p!))); // every part takes it
        ShortcutsCommand = new RelayCommand(_ => ShowingShortcuts = !ShowingShortcuts);
        CloseShortcutsCommand = new RelayCommand(_ => ShowingShortcuts = false);
        CropRatioCommand = new RelayCommand(p => Crop(p as string ?? "free"));
        AddTextCommand = new RelayCommand(_ => AddText());
        DrawCommand = new RelayCommand(_ => Toggle(EditMode.Draw));
        ShapesCommand = new RelayCommand(_ => Toggle(EditMode.Shapes));
        BlurCommand = new RelayCommand(_ => Toggle(EditMode.Blur));
        CropCommand = new RelayCommand(_ => Toggle(EditMode.Crop), _ => IsPicture);
        ZoomCommand = new RelayCommand(_ => Toggle(EditMode.Zoom), _ => CanZoom);
        RemoveZoomCommand = new RelayCommand(_ => Apply(Project.WithParts(_parts, k => k with { Zoom = null })), _ => PartZoomed);
        ModeDoneCommand = new RelayCommand(_ => EditMode = EditMode.None);
        PlaceCommand = new RelayCommand(p => TextPlace = p as string ?? "bottom");
        CaptionsCommand = new RelayCommand(_ =>
        {
            // Every press asks first: what it does (and the download when the model is not here), or, with captions there
            // already, whether to make them again (a press by mistake must not throw edits away).
            if (Project.Items.Any(i => i is TextItem { Group: not null }))
                AskingRecaption = true;
            else
                ShowingIntro = true;
        }, _ => CanCaption && !CaptionsBusy);
        RecaptionCommand = new RelayCommand(_ =>
        {
            AskingRecaption = false;
            if (_captions!.Ready)
                CaptionsTask = MakeCaptions(); // that was the question: no second one
            else
                ShowingIntro = true; // the engine or model was removed since: the download is asked for
        });
        KeepCaptionsCommand = new RelayCommand(_ => AskingRecaption = false);
        IntroStartCommand = new RelayCommand(_ =>
        {
            ShowingIntro = false;
            CaptionsTask = MakeCaptions();
        });
        NotNowCommand = new RelayCommand(_ => ShowingIntro = false);
        CancelCaptionsCommand = new RelayCommand(_ => CancelCaptions());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Save was pressed; the window asks "new copy or replace", then saves with <see cref="BeginSaving"/>.</summary>
    public event Action? SaveAsked;
    public event Action? Closed;
    /// <summary>Space: the window plays or pauses the preview.</summary>
    public event Action? PlayToggleRequested;
    /// <summary>The user started moving the playhead (timeline click or drag, arrow key); playing waits for <see cref="SeekEnded"/>.</summary>
    public event Action? SeekStarted;
    public event Action? SeekEnded;
    /// <summary>A new text was added: the picture opens it for typing.</summary>
    public event Action<TextItem>? TypeRequested;

    /// <summary>How far an arrow key moves the playhead (s), and with Shift.</summary>
    const double SeekStep = 1, FineStep = 0.1;

    public void BeginSeek() => SeekStarted?.Invoke();

    public void EndSeek() => SeekEnded?.Invoke();

    public bool IsPicture { get; }
    /// <summary>The picture's width / height (where the preview places things).</summary>
    public double Aspect => _aspect;
    public EditorMode Mode { get; }
    public bool HasTimeline => !IsPicture;
    public bool HasSound { get; }
    /// <summary>Kairos's clips: game (track 2) and mic (track 3) each with its own slider; else one slider for all.</summary>
    public bool HasSeparateTracks { get; }

    /// <summary>The clip's sound tracks: the mix, and those an edit can set apart.</summary>
    public Engine.Audio.TrackLayout Layout { get; }
    public bool HasOneTrack => HasSound && !HasSeparateTracks;
    /// <summary>Screenshots can be cropped (clips keep their shape).</summary>
    public bool CanCrop => IsPicture;
    public string PrimaryLabel => Text.Get("editor.save");
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Project.Source);

    public EditProject Project => _history.Current;

    /// <summary>Where the preview is, in seconds of the edited clip.</summary>
    public double PlayheadOutput
    {
        get => _playhead;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, Project.OutputDuration));
            if (Math.Abs(clamped - _playhead) < 1e-9)
                return;
            _playhead = clamped;
            Raise();
        }
    }

    /// <summary>How much of the clip the timeline shows: 1 the whole clip, up to 20 times closer.</summary>
    public double TimelineZoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 1, MaxZoom);
            Raise();
        }
    }

    public const double MaxZoom = 20;

    // ---------------------------------------------------------------- mode

    /// <summary>What a press on the picture does now; a mode starts with nothing chosen.</summary>
    public EditMode EditMode
    {
        get => _mode;
        set
        {
            if ((value == EditMode.Crop && !IsPicture) || (value == EditMode.Zoom && !CanZoom))
                value = EditMode.None;
            if (_mode == value)
                return;
            _mode = value;
            _lastAmend = null;
            if (value == EditMode.Zoom)
                ChooseParts(); // the chosen parts, or the one playing
            else if (value != EditMode.None)
                ClearChoice();
            Raise();
            Raise(nameof(IsDrawMode));
            Raise(nameof(IsShapesMode));
            Raise(nameof(IsBlurMode));
            Raise(nameof(IsCropMode));
            Raise(nameof(IsZoomMode));
            RaisePage(); // ZoomShown too
        }
    }

    /// <summary>Zoom: the chosen parts keep their choice; with none, the one playing is chosen.</summary>
    void ChooseParts()
    {
        _ids.Clear();
        if (_parts.Count == 0 && Project.PartAt(PlayheadOutput) is >= 0 and var playing)
            _parts.Add(playing);
        ChoiceChanged();
    }

    /// <summary>Zoom (videos; pictures crop): the chosen parts zoom into an area drawn on the picture.</summary>
    public bool CanZoom => HasTimeline;
    public bool IsZoomMode => _mode == EditMode.Zoom;

    /// <summary>The chosen parts zoom into <paramref name="drawn"/> (0..1 of the picture), made the video's shape.</summary>
    public void ZoomTo(System.Windows.Rect drawn) => Apply(Project.WithParts(_parts, k => k with { Zoom = VideoZoom.Fit(drawn) }));

    /// <summary>Some chosen part zooms: Remove zoom shows.</summary>
    public bool PartZoomed => _parts.Any(i => i < Project.Kept.Count && Project.Kept[i].Zoom != null);

    /// <summary>While the Zoom tool is on: the first chosen part's area, drawn over the whole picture; null otherwise.</summary>
    public System.Windows.Rect? ZoomShown => IsZoomMode ? FirstPart?.Zoom : null;

    public bool IsDrawMode => _mode == EditMode.Draw;
    public bool IsShapesMode => _mode == EditMode.Shapes;
    public bool IsBlurMode => _mode == EditMode.Blur;
    public bool IsCropMode => _mode == EditMode.Crop;

    void Toggle(EditMode mode) => EditMode = _mode == mode ? EditMode.None : mode;

    // ---------------------------------------------------------------- what is chosen

    /// <summary>The chosen items as they are now, in the order they were chosen.</summary>
    public IReadOnlyList<EditItem> SelectedItems =>
        _ids.Select(id => Project.Items.FirstOrDefault(i => i.Id == id)).OfType<EditItem>().ToList();

    /// <summary>The chosen parts (indices into the kept parts).</summary>
    public IReadOnlyList<int> SelectedParts => _parts;

    /// <summary>The one chosen item (the picture shows its corners), or null with none or several.</summary>
    public EditItem? SelectedItem
    {
        get => _ids.Count == 1 ? Project.Items.FirstOrDefault(i => i.Id == _ids[0]) : null;
        set
        {
            if (value == null)
                ClearChoice();
            else
                Select(value);
        }
    }

    public bool IsChosen(EditItem item) => _ids.Contains(item.Id);

    public bool IsPartChosen(int index) => _parts.Contains(index);

    /// <summary>Chooses an item; with <paramref name="add"/> (Shift or Ctrl) it joins the chosen ones, or leaves them.</summary>
    public void Select(EditItem item, bool add = false)
    {
        _parts.Clear();
        if (!add)
            _ids.Clear();
        if (add && _ids.Remove(item.Id))
        {
            // taken out again
        }
        else
        {
            _ids.Add(item.Id);
            _anchor = item.Id;
        }
        if (_mode != EditMode.Crop)
            _mode = EditMode.None;
        ChoiceChanged();
    }

    /// <summary>Chooses a part of the clip (its speed and sound show); with <paramref name="add"/> it joins the others.</summary>
    public void SelectPart(int index, bool add = false)
    {
        if (index < 0 || index >= Project.Kept.Count)
            return;
        _ids.Clear();
        if (!add)
            _parts.Clear();
        if (!(add && _parts.Remove(index)))
        {
            _parts.Add(index);
            _partAnchor = index;
        }
        if (_mode != EditMode.Zoom) // Zoom works on parts: choosing another keeps it
            _mode = EditMode.None;
        ChoiceChanged();
    }

    /// <summary>Shift-click: every item of the row from the one chosen before to this one (another row: this one alone).</summary>
    public void SelectRange(EditItem item)
    {
        if (_anchor is not { } anchorId || Project.Items.FirstOrDefault(i => i.Id == anchorId) is not { } anchor || RowOf(anchor) != RowOf(item))
        {
            Select(item);
            return;
        }
        double from = Math.Min(anchor.Start, item.Start), to = Math.Max(anchor.Start, item.Start);
        _parts.Clear();
        _ids.Clear();
        _ids.AddRange(Project.Items.Where(i => RowOf(i) == RowOf(item) && i.Start >= from - 1e-9 && i.Start <= to + 1e-9).OrderBy(i => i.Start)
            .Select(i => i.Id));
        _mode = EditMode.None;
        ChoiceChanged();
    }

    /// <summary>Shift-click on a part: every part from the one chosen before to this one.</summary>
    public void SelectPartRange(int index)
    {
        if (_partAnchor < 0 || _partAnchor >= Project.Kept.Count || index < 0 || index >= Project.Kept.Count)
        {
            SelectPart(index);
            return;
        }
        _ids.Clear();
        _parts.Clear();
        _parts.AddRange(Enumerable.Range(Math.Min(_partAnchor, index), Math.Abs(_partAnchor - index) + 1));
        _mode = EditMode.None;
        ChoiceChanged();
    }

    /// <summary>Exactly these items are chosen (a box dragged over the timeline, pasted copies).</summary>
    public void SetChoice(IEnumerable<EditItem> items)
    {
        _parts.Clear();
        _ids.Clear();
        _ids.AddRange(items.Select(i => i.Id).Distinct());
        _mode = EditMode.None;
        ChoiceChanged();
    }

    /// <summary>Ctrl+A: every item (with none, every part).</summary>
    public void SelectAll()
    {
        if (Project.Items.Count > 0)
            SetChoice(Project.Items);
        else if (HasTimeline)
            SelectRow(TimelineRow.Video);
    }

    /// <summary>Chooses all of a row: every part, or every item of its kind; with <paramref name="add"/> (Ctrl) the row joins what is chosen.</summary>
    public void SelectRow(TimelineRow row, bool add = false)
    {
        if (!add || row == TimelineRow.Video)
            _ids.Clear();
        if (!add || row != TimelineRow.Video)
            _parts.Clear();
        if (row == TimelineRow.Video)
            _parts.AddRange(Enumerable.Range(0, Project.Kept.Count).Where(i => !_parts.Contains(i)));
        else
            _ids.AddRange(Project.Items.Where(i => RowOf(i) == row && !_ids.Contains(i.Id)).OrderBy(i => i.Start).Select(i => i.Id));
        _mode = EditMode.None;
        ChoiceChanged();
    }

    public void ClearChoice()
    {
        if (_ids.Count == 0 && _parts.Count == 0)
            return;
        _ids.Clear();
        _parts.Clear();
        ChoiceChanged();
    }

    /// <summary>Whether all of a row is chosen (its name shows it).</summary>
    public bool IsRowChosen(TimelineRow row)
    {
        if (row == TimelineRow.Video)
            return Project.Kept.Count > 1 && _parts.Count == Project.Kept.Count;
        var items = Project.Items.Where(i => RowOf(i) == row).ToList();
        return items.Count > 0 && _ids.Count == items.Count && items.All(i => _ids.Contains(i.Id));
    }

    public static TimelineRow RowOf(EditItem item) => item switch
    {
        TextItem => TimelineRow.Text,
        StrokeItem => TimelineRow.Draw,
        _ => TimelineRow.Shapes,
    };

    /// <summary>The timeline's rows: the clip, then a row for each kind of item, there from the start (an empty row stays empty, not hidden).</summary>
    public IReadOnlyList<TimelineRow> Rows { get; } = Enum.GetValues<TimelineRow>();

    void Prune()
    {
        _ids.RemoveAll(id => Project.Items.All(i => i.Id != id));
        _parts.RemoveAll(i => i >= Project.Kept.Count);
    }

    void ChoiceChanged()
    {
        _lastAmend = null; // a change to other things is another step to undo
        LoadStyle();
        Raise(nameof(SelectedItem));
        Raise(nameof(SelectedItems));
        Raise(nameof(SelectedParts));
        Raise(nameof(EditMode));
        Raise(nameof(IsDrawMode));
        Raise(nameof(IsShapesMode));
        Raise(nameof(IsBlurMode));
        Raise(nameof(IsCropMode));
        Raise(nameof(IsZoomMode));
        RaisePage();
        CommandManager.InvalidateRequerySuggested();
    }


    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
