using System.Windows;
using System.Windows.Input;
using Kairos.App;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

public sealed partial class EditorViewModel
{
    // ---------------------------------------------------------------- adding things

    /// <summary>A text in the middle of the picture at the playhead, chosen and open for typing.</summary>
    void AddText()
    {
        EditMode = EditMode.None;
        var (start, end) = EditGeometry.NewItemTime(Project, _playhead);
        var size = MeasureText(Text.Get("editor.newText"));
        var text = new TextItem(Guid.NewGuid(), start, end, "", new Rect((1 - size.Width) / 2, (1 - size.Height) / 2, size.Width, size.Height),
            _textColor, _textSize, _textEffect, _textFont);
        // Making it, typing it and (left empty) removing it are one step to undo (chosen first: choosing starts a new step).
        Select(text);
        Amend("text:" + text.Id, Project.With(text));
        TypeRequested?.Invoke(text);
    }

    /// <summary>A line drawn in the Draw mode: its own item with the brush's colour and thickness, a step to undo.</summary>
    public StrokeItem AddStroke(IReadOnlyList<Point> points)
    {
        var (start, end) = EditGeometry.NewItemTime(Project, _playhead);
        var made = new StrokeItem(Guid.NewGuid(), start, end, [points], _penColor, _penThickness);
        Apply(Project.With(made));
        return made;
    }

    /// <summary>A shape or blur box dragged on the picture: added, its mode ends, and it is chosen.</summary>
    public void AddMade(EditItem item)
    {
        Apply(Project.With(item));
        _mode = EditMode.None;
        Select(item);
    }

    // ---------------------------------------------------------------- commands

    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    /// <summary>Save (Library) or Done (Share).</summary>
    public ICommand PrimaryCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand KeepEditingCommand { get; }
    /// <summary>Reset: asks, then the clip is as it was (every edit gone, one step to undo).</summary>
    public ICommand ResetCommand { get; }
    public ICommand ConfirmResetCommand { get; }
    public ICommand CancelResetCommand { get; }
    public ICommand SplitCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand LookCommand { get; }
    public ICommand CropRatioCommand { get; }
    public ICommand AddTextCommand { get; }
    public ICommand DrawCommand { get; }
    public ICommand ShapesCommand { get; }
    public ICommand BlurCommand { get; }
    public ICommand CropCommand { get; }
    public ICommand ZoomCommand { get; }
    public ICommand RemoveZoomCommand { get; }
    public ICommand ModeDoneCommand { get; }
    public ICommand PlaceCommand { get; }

    bool _askingDiscard;

    /// <summary>Back was pressed with changes: "discard them?" is showing.</summary>
    public bool AskingDiscard { get => _askingDiscard; private set { _askingDiscard = value; Raise(); } }

    bool _askingReset;

    /// <summary>Reset was pressed: "reset the clip?" is showing.</summary>
    public bool AskingReset { get => _askingReset; private set { _askingReset = value; Raise(); } }

    bool _showingShortcuts;

    /// <summary>The sheet of every shortcut is showing (the keyboard button, or ?).</summary>
    public bool ShowingShortcuts { get => _showingShortcuts; private set { _showingShortcuts = value; Raise(); } }

    public ICommand ShortcutsCommand { get; }
    public ICommand CloseShortcutsCommand { get; }

    /// <summary>
    /// A key from the window (see the shortcuts sheet): Space, S or Ctrl+B, Delete, arrows (Shift: finely; Alt: move the
    /// chosen), Up/Down, Home/End, Q/W, Ctrl+A/C/V/D, Enter, Ctrl+Plus/Minus, Backslash, ?, Escape, Ctrl+Z or Ctrl+Y.
    /// True when it was used.
    /// </summary>
    /// <param name="repeat">The key is held (Windows repeats it): the playhead moves, the player waits for <see cref="KeyReleased"/>.</param>
    public bool KeyCommand(string key, bool repeat = false)
    {
        if (_saving)
            return false; // nothing changes while the edit is being written
        _keyRepeat = repeat;
        if (_showingShortcuts && key is "Escape" or "?")
        {
            ShowingShortcuts = false;
            return true;
        }
        switch (key)
        {
            case "?":
                ShowingShortcuts = true;
                return true;
            case "Ctrl+B" when HasTimeline:
                Split();
                return true;
            case "Ctrl+A":
                SelectAll();
                return true;
            case "Q" or "W" when HasTimeline:
                TrimToPlayhead(start: key == "Q");
                return true;
            case "Alt+Left" or "Alt+Right" or "Alt+Shift+Left" or "Alt+Shift+Right" when HasTimeline:
                Nudge((key.EndsWith("Right", StringComparison.Ordinal) ? 1 : -1) * (key.Contains("Shift", StringComparison.Ordinal) ? SeekStep : FineStep));
                return true;
            case "Up" or "Down" when HasTimeline:
                Jump(key == "Down");
                return true;
            case "Home" or "End" when HasTimeline:
                BeginSeek();
                PlayheadOutput = key == "End" ? Project.OutputDuration : 0;
                EndSeek();
                return true;
            case "Ctrl+C":
                if (SelectedItems.Count > 0)
                    _clipboard = SelectedItems;
                return true;
            case "Ctrl+V":
                Paste();
                return true;
            case "Ctrl+D":
                Duplicate();
                return true;
            case "Enter":
                return TypeChosen();
            case "Ctrl+Plus" when HasTimeline:
                TimelineZoom *= 1.25;
                return true;
            case "Ctrl+Minus" when HasTimeline:
                TimelineZoom /= 1.25;
                return true;
            case "Backslash" when HasTimeline:
                TimelineZoom = 1;
                return true;
            case "Space":
                PlayToggleRequested?.Invoke();
                return true;
            case "S" when HasTimeline:
                Split();
                return true;
            case "Delete":
                DeleteChosen();
                return true;
            case "Left" or "Right" or "Shift+Left" or "Shift+Right" when HasTimeline:
                BeginSeek();
                double step = key.StartsWith("Shift", StringComparison.Ordinal) ? FineStep : SeekStep;
                PlayheadOutput += key.EndsWith("Right", StringComparison.Ordinal) ? step : -step;
                EndKeySeek();
                return true;
            case "Escape":
                // The shortcuts sheet first (above), then out of a mode, then nothing chosen, then leave (asked with changes).
                if (_mode != EditMode.None)
                    EditMode = EditMode.None;
                else if (_ids.Count > 0 || _parts.Count > 0)
                    ClearChoice();
                else
                    CloseCommand.Execute(null);
                return true;
            case "Ctrl+Z":
                UndoCommand.Execute(null);
                return true;
            case "Ctrl+Y":
                RedoCommand.Execute(null);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Q / W: the chosen items start (or end) at the playhead; with no item chosen, the part there loses what is before
    /// (or after) it. One step to undo.
    /// </summary>
    void TrimToPlayhead(bool start)
    {
        if (Project.ToSource(_playhead) is not { } source)
            return;
        if (_ids.Count > 0)
        {
            var project = Project;
            foreach (var item in SelectedItems)
            {
                if (start && source < item.End - TimelineMath.MinItemSeconds)
                    project = project.With(item with { Start = source });
                else if (!start && source > item.Start + TimelineMath.MinItemSeconds)
                    project = project.With(item with { End = source });
            }
            Apply(project);
            return;
        }
        var next = Project.TrimPartAt(_playhead, start);
        if (ReferenceEquals(next, Project))
            return;
        _parts.Clear();
        Apply(next);
        ChoiceChanged();
        BeginSeek();
        PlayheadOutput = Project.ToOutput(source);
        EndSeek();
    }

    /// <summary>Alt+arrows: the chosen items a little earlier or later (one step to undo per press).</summary>
    void Nudge(double deltaOutput)
    {
        var project = Project;
        foreach (var item in SelectedItems)
            project = project.With(TimelineMath.MoveBar(Project, item, deltaOutput));
        Apply(project);
    }

    /// <summary>Up / Down: the playhead to the cut, item start or end before or after it (Home and End are the ends).</summary>
    void Jump(bool forward)
    {
        var points = new List<double> { 0, Project.OutputDuration };
        double at = 0;
        foreach (var (span, factor) in Project.Pieces())
            points.Add(at += span.Length / factor);
        foreach (var bar in TimelineMath.ItemBars(Project))
        {
            points.Add(bar.Start);
            points.Add(bar.End);
        }
        double? target = forward
            ? points.Where(p => p > _playhead + 1e-6).DefaultIfEmpty(double.NaN).Min()
            : points.Where(p => p < _playhead - 1e-6).DefaultIfEmpty(double.NaN).Max();
        if (target is not { } t || double.IsNaN(t))
            return;
        BeginSeek();
        PlayheadOutput = t;
        EndKeySeek();
    }

    bool _keyRepeat, _seekHeld;

    /// <summary>A key's seek ends at once, unless the key is held: then when it is let go (one restart, not one per repeat).</summary>
    void EndKeySeek()
    {
        if (_keyRepeat)
            _seekHeld = true;
        else
            EndSeek();
    }

    /// <summary>A key was let go: a seek held by a repeating arrow ends (the clip plays on from there if it was playing).</summary>
    public void KeyReleased()
    {
        if (!_seekHeld)
            return;
        _seekHeld = false;
        EndSeek();
    }

    /// <summary>Ctrl+V: what Ctrl+C copied, at the playhead (as far apart as before), chosen.</summary>
    void Paste()
    {
        if (_clipboard.Count == 0)
            return;
        double at = Project.ToSource(_playhead) ?? 0;
        AddCopies(_clipboard, at - _clipboard.Min(i => i.Start));
    }

    /// <summary>Ctrl+D: copies of the chosen items right after them, chosen.</summary>
    void Duplicate()
    {
        var items = SelectedItems;
        if (items.Count > 0)
            AddCopies(items, items.Max(i => i.End) - items.Min(i => i.Start));
    }

    /// <summary>
    /// New copies <paramref name="shift"/> seconds later (kept inside the clip). A screenshot has no time: the copies sit a
    /// little lower and to the right instead. A copied caption becomes a text of its own (making captions again keeps it).
    /// </summary>
    void AddCopies(IReadOnlyList<EditItem> items, double shift)
    {
        var copies = new List<EditItem>();
        var project = Project;
        foreach (var item in items)
        {
            double length = item.End - item.Start;
            double start = Math.Clamp(item.Start + shift, 0, Math.Max(0, Project.Duration - length));
            EditItem copy = item with { Id = Guid.NewGuid(), Start = start, End = start + length };
            if (copy is TextItem text)
                copy = text with { Group = null };
            if (Project.Duration <= 0)
                copy = EditGeometry.Move(copy, new Vector(0.03, 0.03));
            copies.Add(copy);
            project = project.With(copy);
        }
        Apply(project);
        SetChoice(copies);
    }

    /// <summary>Enter: types in the one chosen text (the playhead goes to it if it is not on screen).</summary>
    bool TypeChosen()
    {
        if (SelectedItems is not [TextItem text])
            return false;
        if (!EditGeometry.ShowsAt(Project, text, Project.ToSource(_playhead) ?? 0))
        {
            BeginSeek();
            PlayheadOutput = Project.ToOutput(text.Start);
            EndSeek();
        }
        TypeRequested?.Invoke(text);
        return true;
    }

    /// <summary>Splits the part at the playhead; the part starting there is chosen (its speed and sound show).</summary>
    void Split()
    {
        var next = Project.SplitAt(_playhead);
        if (ReferenceEquals(next, Project))
            return;
        Apply(next);
        SelectPart(Project.PartAt(_playhead));
    }

    /// <summary>Deletes the chosen items, or the chosen parts (the last part stays).</summary>
    void DeleteChosen()
    {
        if (_ids.Count > 0)
        {
            var project = Project;
            foreach (var id in _ids)
                project = project.Without(id);
            _ids.Clear();
            Apply(project);
            ChoiceChanged();
            return;
        }
        if (_parts.Count > 0 && _parts.Count < Project.Kept.Count)
        {
            var next = Project.WithoutParts(_parts);
            _parts.Clear();
            Apply(next);
            ChoiceChanged();
        }
    }
}
