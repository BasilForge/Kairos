using System.Globalization;
using System.Windows;
using Kairos.App;
using Kairos.Engine.Captions;
using Kairos.Engine.Edit;
using TextEffect = Kairos.Engine.Edit.TextEffect;

namespace Kairos.Ui.Edit;

public sealed partial class EditorViewModel
{
    // ---------------------------------------------------------------- the inspector

    public InspectorPage Page => _mode switch
    {
        EditMode.Draw => InspectorPage.Line,
        EditMode.Shapes => InspectorPage.Shape,
        EditMode.Blur => InspectorPage.Blur,
        EditMode.Crop => InspectorPage.Crop,
        _ => SelectedItems switch
        {
            [] => _parts.Count > 0 ? InspectorPage.Part : InspectorPage.Clip,
            var items when items.All(i => i is TextItem) => InspectorPage.Text,
            var items when items.All(i => i is StrokeItem) => InspectorPage.Line,
            var items when items.All(i => i is ShapeItem) => InspectorPage.Shape,
            var items when items.All(i => i is BlurItem) => InspectorPage.Blur,
            _ => InspectorPage.Mixed,
        },
    };

    public bool IsClipPage => Page == InspectorPage.Clip;
    public bool IsPartPage => Page == InspectorPage.Part;
    public bool IsTextPage => Page == InspectorPage.Text;
    public bool IsLinePage => Page == InspectorPage.Line;
    public bool IsShapePage => Page == InspectorPage.Shape;
    public bool IsBlurPage => Page == InspectorPage.Blur;
    public bool IsMixedPage => Page == InspectorPage.Mixed;
    public bool IsCropPage => Page == InspectorPage.Crop;

    /// <summary>The inspector's heading: what it is about.</summary>
    public string PageTitle
    {
        get
        {
            var items = SelectedItems;
            return Page switch
            {
                InspectorPage.Clip => Text.Get(IsPicture ? "editor.page.picture" : "editor.page.clip"),
                InspectorPage.Part => _parts.Count == 1 ? Text.Format("editor.page.part", _parts[0] + 1) : Text.Get("editor.page.parts"),
                InspectorPage.Text when items.All(i => i is TextItem { Group: not null }) =>
                    Text.Get(items.Count == 1 ? "editor.page.caption" : "editor.page.captions"),
                InspectorPage.Text => Text.Get(items.Count == 1 ? "editor.page.text" : "editor.page.texts"),
                InspectorPage.Line when _mode == EditMode.Draw => Text.Get("editor.page.draw"),
                InspectorPage.Line => Text.Get(items.Count == 1 ? "editor.page.line" : "editor.page.lines"),
                InspectorPage.Shape when _mode == EditMode.Shapes => Text.Get("editor.page.newShape"),
                InspectorPage.Shape => Text.Get(items.Count == 1 ? "editor.page.shape" : "editor.page.shapes"),
                InspectorPage.Blur => Text.Get("editor.page.blur"),
                InspectorPage.Crop => Text.Get("editor.page.crop"),
                _ => Text.Get("editor.page.mixed"),
            };
        }
    }

    /// <summary>The heading's icon (Segoe Fluent Icons).</summary>
    public string PageGlyph => Page switch
    {
        InspectorPage.Clip => IsPicture ? "\uE91B" : "\uE714",
        InspectorPage.Part => "\uE714",
        InspectorPage.Text when SelectedItems.All(i => i is TextItem { Group: not null }) => "\uE7F0",
        InspectorPage.Text => "\uE8D2",
        InspectorPage.Line => "\uEDFB",
        InspectorPage.Shape => "\uE879",
        InspectorPage.Blur => "\uED1A",
        InspectorPage.Crop => "\uE7A8",
        _ => "\uE762",
    };

    /// <summary>A few words beside the heading: how many are chosen, a part's time, what the mode does.</summary>
    public string PageNote
    {
        get
        {
            int count = Page == InspectorPage.Part ? _parts.Count : _ids.Count;
            return Page switch
            {
                InspectorPage.Part when _parts.Count == 1 => PartTimes(_parts[0]),
                InspectorPage.Line when _mode == EditMode.Draw => Text.Get("editor.note.nextLine"),
                InspectorPage.Shape when _mode == EditMode.Shapes => Text.Get("editor.note.drag"),
                InspectorPage.Blur when _mode == EditMode.Blur => Text.Get("editor.note.dragBlur"),
                InspectorPage.Clip or InspectorPage.Crop => "",
                _ when count > 1 => Text.Format("editor.note.chosen", count),
                _ => "",
            };
        }
    }

    /// <summary>One line of help under the settings (empty: none).</summary>
    public string PageHint => Page switch
    {
        InspectorPage.Clip when HasTimeline => Text.Get("editor.hint.clip"),
        InspectorPage.Part when _mode == EditMode.Zoom => Text.Get("editor.hint.zoom"),
        InspectorPage.Part => Text.Get("editor.hint.part"),
        InspectorPage.Text when SelectedItems is [TextItem] => Text.Get("editor.hint.allTexts"),
        InspectorPage.Line when _mode == EditMode.Draw => Text.Get("editor.hint.draw"),
        InspectorPage.Blur => Text.Get("editor.hint.blur"),
        InspectorPage.Mixed => Text.Get("editor.hint.mixed"),
        _ => "",
    };

    /// <summary>The brush page has a Done button (the line tools stay until it is pressed).</summary>
    public bool ShowsModeDone => _mode is EditMode.Draw or EditMode.Crop or EditMode.Zoom;

    string PartTimes(int index)
    {
        if (index >= Project.Kept.Count)
            return "";
        var part = Project.Kept[index];
        return $"{Clock(Project.ToOutput(part.Start))} – {Clock(Project.ToOutput(part.End))}";
    }

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    void RaisePage()
    {
        foreach (string name in new[] { nameof(Page), nameof(IsClipPage), nameof(IsPartPage), nameof(IsTextPage), nameof(IsLinePage),
                     nameof(IsShapePage), nameof(IsBlurPage), nameof(IsMixedPage), nameof(IsCropPage), nameof(PageTitle), nameof(PageNote), nameof(PageGlyph),
                     nameof(PageHint), nameof(ShowsModeDone), nameof(PartZoomed), nameof(ZoomShown), nameof(HasWords), nameof(Words), nameof(TextPlace), nameof(PartSpeed),
                     nameof(PartSpeedSlider), nameof(PartSpeedLabel), nameof(PartLookStrength), nameof(ClipSpeed), nameof(ClipSpeedSlider),
                     nameof(ClipSpeedLabel) })
            Raise(name);
        foreach (var track in PartTracks)
            track.Refresh();
        RefreshChoices();
    }

    void RaiseAll()
    {
        Raise(nameof(Project));
        Raise(nameof(Look));
        Raise(nameof(LookStrength));
        Raise(nameof(AllLevel));
        Raise(nameof(AllLabel));
        foreach (var track in ClipTracks)
            track.Refresh();
        Raise(nameof(SelectedItem));
        Raise(nameof(SelectedItems));
        Raise(nameof(SelectedParts));
        RaisePage();
    }

    // ---------------------------------------------------------------- palettes and choices

    static readonly string[] Palette = ["#FFFFFF", "#F48BA4", "#FFD54A", "#FF5A5A", "#3DDC84", "#4FC3F7", "#111111"];
    static readonly double[] SpeedSteps = [0.25, 0.5, 1, 1.5, 2, 3];
    public IReadOnlyList<SwatchChoice> TextSwatches { get; }
    public IReadOnlyList<SwatchChoice> PenSwatches { get; }
    public IReadOnlyList<SwatchChoice> ShapeSwatches { get; }
    public IReadOnlyList<SwatchChoice> MixedSwatches { get; }
    public IReadOnlyList<FontChoice> Fonts { get; }
    public IReadOnlyList<LookChoice> Looks { get; }
    public IReadOnlyList<SpeedChoice> Speeds { get; }

    static IReadOnlyList<SwatchChoice> Swatches(Action<string> choose, string current)
    {
        var list = Palette.Select(hex => new SwatchChoice(hex, choose)).ToList();
        list.ForEach(s => s.Refresh(current));
        return list;
    }

    void RefreshChoices()
    {
        foreach (var s in TextSwatches ?? [])
            s.Refresh(_textColor);
        foreach (var s in PenSwatches ?? [])
            s.Refresh(_penColor);
        foreach (var s in ShapeSwatches ?? [])
            s.Refresh(_shapeColor);
        string mixed = SelectedItems.Select(ColorOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? SelectedItems.Select(ColorOf).OfType<string>().First() : "";
        foreach (var s in MixedSwatches ?? [])
            s.Refresh(mixed);
        foreach (var f in Fonts ?? [])
            f.Refresh(_textFont);
        foreach (var l in Looks ?? [])
            l.Refresh(Project.Look);
        foreach (var l in PartLooks ?? [])
            l.Refresh(FirstPart?.Look ?? Project.Look);
        foreach (var s in Speeds ?? [])
            s.Refresh(PartSpeed);
        foreach (var s in ClipSpeeds ?? [])
            s.Refresh(ClipSpeed);
    }

    static string? ColorOf(EditItem item) => item switch
    {
        TextItem t => t.ColorHex,
        StrokeItem s => s.ColorHex,
        ShapeItem s => s.ColorHex,
        _ => null,
    };

    // ---------------------------------------------------------------- text settings (the chosen texts, or the next new one)

    string _textColor = "#FFFFFF", _penColor = "#F48BA4", _shapeColor = "#F48BA4";
    double _textSize = 0.06, _penThickness = 0.008, _shapeThickness = 0.008;
    TextEffect _textEffect = TextEffect.Box;
    string _textFont = TextFonts.Default;
    ShapeKind _shapeKind = ShapeKind.Arrow;

    // A setting changes only that on every chosen text: each keeps its other choices.
    public string TextColor { get => _textColor; set { _textColor = value; Raise(); RefreshChoices(); RestyleTexts("color", t => t with { ColorHex = value }); } }
    public double TextSize { get => _textSize; set { _textSize = value; Raise(); RestyleTexts("size", t => t with { SizeRel = value }); } }
    public TextEffect TextEffect { get => _textEffect; set { _textEffect = value; Raise(); RestyleTexts("effect", t => t with { Effect = value }); } }

    public string TextFont
    {
        get => _textFont;
        set
        {
            _textFont = TextFonts.For(value).Key;
            Raise();
            RefreshChoices();
            RestyleTexts("font", t => t with { Font = _textFont });
        }
    }

    /// <summary>One text chosen: its words can be typed in the inspector.</summary>
    public bool HasWords => _mode == EditMode.None && SelectedItems is [TextItem];

    public string Words
    {
        get => SelectedItems is [TextItem t] ? t.Text : "";
        set
        {
            if (SelectedItems is not [TextItem t] || value == t.Text)
                return;
            // The same kind of step as typing on the picture: a new text and its words are one step to undo.
            Amend("text:" + t.Id, Project.With(Refit(t with { Text = value }, t)));
        }
    }

    /// <summary>Where the chosen texts stand: "top", "middle" or "bottom" (centred across); setting it moves them all there.</summary>
    public string TextPlace
    {
        get
        {
            if (SelectedItems.OfType<TextItem>().FirstOrDefault() is not { } t)
                return "";
            double centre = t.Box.Y + t.Box.Height / 2;
            return centre < 0.34 ? "top" : centre > 0.66 ? "bottom" : "middle";
        }
        set
        {
            RestyleTexts("place", t => Placed(t, value));
            Raise();
        }
    }

    static TextItem Placed(TextItem t, string place)
    {
        double w = Math.Min(1, t.Box.Width), h = Math.Min(1, t.Box.Height);
        double y = place switch
        {
            "top" => Captions.BottomMargin,
            "middle" => (1 - h) / 2,
            _ => 1 - Captions.BottomMargin - h,
        };
        return t with { Box = new Rect((1 - w) / 2, Math.Clamp(y, 0, Math.Max(0, 1 - h)), w, h) };
    }

    void RestyleTexts(string what, Func<TextItem, TextItem> change)
    {
        var texts = SelectedItems.OfType<TextItem>().ToList();
        var project = Project;
        foreach (var text in texts)
        {
            var styled = Refit(change(text), text);
            if (styled != text)
                project = project.With(styled);
        }
        if (!ReferenceEquals(project, Project))
            Amend($"style:{what}:{ChoiceKey}", project);
    }

    string ChoiceKey => string.Join(",", _ids);

    // ---------------------------------------------------------------- lines and shapes (the chosen ones, or the next one)

    /// <summary>The brush: in the Draw mode it is for the next line only; with lines chosen it changes them.</summary>
    public string PenColor { get => _penColor; set { _penColor = value; Raise(); RefreshChoices(); RestyleLines(); } }
    public double PenThickness { get => _penThickness; set { _penThickness = value; Raise(); RestyleLines(); } }
    public string ShapeColor { get => _shapeColor; set { _shapeColor = value; Raise(); RefreshChoices(); RestyleShapes(); } }
    public double ShapeThickness { get => _shapeThickness; set { _shapeThickness = value; Raise(); RestyleShapes(); } }
    public ShapeKind ShapeKind { get => _shapeKind; set { _shapeKind = value; Raise(); RestyleShapes(); } }

    void RestyleLines()
    {
        if (_mode == EditMode.Draw)
            return; // the next line's brush: no line drawn before changes
        Restyle<StrokeItem>("line", s => s with { ColorHex = _penColor, ThicknessRel = _penThickness });
    }

    void RestyleShapes()
    {
        if (_mode == EditMode.Shapes)
            return;
        Restyle<ShapeItem>("shape", s => s with { ColorHex = _shapeColor, ThicknessRel = _shapeThickness, Kind = _shapeKind });
    }

    void Restyle<T>(string what, Func<T, T> change) where T : EditItem
    {
        var project = Project;
        foreach (var item in SelectedItems.OfType<T>())
        {
            var next = change(item);
            if (next != item)
                project = project.With(next);
        }
        if (!ReferenceEquals(project, Project))
            Amend($"style:{what}:{ChoiceKey}", project);
    }

    /// <summary>Several kinds chosen: one colour for all that have one.</summary>
    public string MixedColor
    {
        get => "";
        set
        {
            var project = Project;
            foreach (var item in SelectedItems)
            {
                EditItem next = item switch
                {
                    TextItem t => t with { ColorHex = value },
                    StrokeItem s => s with { ColorHex = value },
                    ShapeItem s => s with { ColorHex = value },
                    _ => item,
                };
                if (next != item)
                    project = project.With(next);
            }
            if (!ReferenceEquals(project, Project))
                Amend("style:mixed:" + ChoiceKey, project);
            RefreshChoices();
        }
    }

    /// <summary>The settings show the first chosen thing of each kind as it is (without changing it).</summary>
    void LoadStyle()
    {
        var items = SelectedItems;
        if (items.OfType<TextItem>().FirstOrDefault() is { } t)
        {
            (_textColor, _textSize, _textEffect, _textFont) = (t.ColorHex, t.SizeRel, t.Effect, TextFonts.For(t.Font).Key);
            Raise(nameof(TextColor));
            Raise(nameof(TextSize));
            Raise(nameof(TextEffect));
            Raise(nameof(TextFont));
        }
        if (items.OfType<StrokeItem>().FirstOrDefault() is { } s)
        {
            (_penColor, _penThickness) = (s.ColorHex, s.ThicknessRel);
            Raise(nameof(PenColor));
            Raise(nameof(PenThickness));
        }
        if (items.OfType<ShapeItem>().FirstOrDefault() is { } shape)
        {
            (_shapeColor, _shapeThickness, _shapeKind) = (shape.ColorHex, shape.ThicknessRel, shape.Kind);
            Raise(nameof(ShapeColor));
            Raise(nameof(ShapeThickness));
            Raise(nameof(ShapeKind));
        }
        RefreshChoices();
    }

    // ---------------------------------------------------------------- the chosen parts (speed and sound of their own)

    ClipSpan? FirstPart => _parts.Count > 0 && _parts[0] < Project.Kept.Count ? Project.Kept[_parts[0]] : null;

    public double PartSpeed
    {
        get => FirstPart?.Speed ?? 1;
        set => ChangeParts("speed", k => k with { Speed = Math.Round(value, 2) });
    }

    /// <summary>The speed slider, 0..1 for 0.25×..4× with 1× in the middle.</summary>
    public double PartSpeedSlider
    {
        get => (Math.Log2(PartSpeed) + 2) / 4;
        set => PartSpeed = Math.Pow(2, Math.Clamp(value, 0, 1) * 4 - 2);
    }

    public string PartSpeedLabel => PartSpeed.ToString("0.0#", CultureInfo.InvariantCulture) + "×";

    // ---------------------------------------------------------------- the whole clip's speed (every part at once)

    /// <summary>The speed every part shares; NaN when they differ (no tile chosen then, and the label says so).</summary>
    public double ClipSpeed
    {
        get
        {
            var speeds = Project.Kept.Select(k => Math.Round(k.Speed, 2)).Distinct().ToList();
            return speeds.Count == 1 ? speeds[0] : double.NaN;
        }
        set
        {
            double speed = Math.Round(value, 2);
            var next = Project.WithParts(Enumerable.Range(0, Project.Kept.Count), k => k with { Speed = speed });
            if (!ReferenceEquals(next, Project))
                Amend("clip:speed", next);
        }
    }

    /// <summary>The whole clip's speed slider, 0..1 for 0.25×..4× with 1× in the middle (the middle when the parts differ).</summary>
    public double ClipSpeedSlider
    {
        get => double.IsNaN(ClipSpeed) ? 0.5 : (Math.Log2(ClipSpeed) + 2) / 4;
        set => ClipSpeed = Math.Pow(2, Math.Clamp(value, 0, 1) * 4 - 2);
    }

    public string ClipSpeedLabel => double.IsNaN(ClipSpeed) ? Text.Get("editor.speed.varies") : ClipSpeed.ToString("0.0#", CultureInfo.InvariantCulture) + "×";

    public IReadOnlyList<SpeedChoice> ClipSpeeds { get; }
    /// <summary>The chosen parts' own sound tracks (a one-track clip: its whole sound), each a share of the clip's level.</summary>
    public IReadOnlyList<SoundTrackView> PartTracks { get; }

    internal double PartTrackLevel(int track) => FirstPart?.Levels[track] ?? 1;

    internal void SetPartTrackLevel(int track, double level) => ChangeParts($"track{track}", k => k with { Levels = k.Levels.With(track, level) });

    /// <summary>The chosen parts' colour look (their own, or the clip's when they have none).</summary>
    public IReadOnlyList<LookChoice> PartLooks { get; }

    public double PartLookStrength
    {
        get => FirstPart is { Look: not null } part ? part.LookStrength : Project.LookStrength;
        set => ChangeParts("look", k => k with { Look = k.Look ?? Project.Look, LookStrength = Math.Clamp(value, 0, 1) });
    }

    void ChangeParts(string what, Func<ClipSpan, ClipSpan> change)
    {
        var next = Project.WithParts(_parts, change);
        if (!ReferenceEquals(next, Project))
            Amend($"part:{what}:{string.Join(",", _parts)}", next);
    }

    internal static string Percent(double level) => Math.Round(level * 100).ToString(CultureInfo.InvariantCulture) + "%";

    // ---------------------------------------------------------------- the whole clip: colours and sound (sliders amend one step)

    public ColourLook Look => Project.Look;

    public double LookStrength
    {
        get => Project.LookStrength;
        set => Amend("look", Project with { LookStrength = Math.Clamp(value, 0, 1) });
    }

    /// <summary>The clip's own sound tracks (none for a one-track clip): the level and switch of each.</summary>
    public IReadOnlyList<SoundTrackView> ClipTracks { get; }

    internal double TrackLevel(int track) => Project.Sound.Tracks[track];

    internal void SetTrackLevel(int track, double level) =>
        Amend($"track{track}", Project with { Sound = Project.Sound with { Tracks = Project.Sound.Tracks.With(track, Level(level)) } });

    internal bool TrackMuted(int track) => Project.Sound.Tracks.Muted(track);

    internal void SetTrackMuted(int track, bool muted) =>
        Apply(Project with { Sound = Project.Sound with { Tracks = Project.Sound.Tracks.WithMuted(track, muted) } });

    /// <summary>A one-track clip's level.</summary>
    public double AllLevel { get => Project.Sound.All; set => Amend("all", Project with { Sound = Project.Sound with { All = Level(value) } }); }
    public string AllLabel => Percent(AllLevel);

    static double Level(double value) => Math.Clamp(value, 0, ClipSpan.MaxLevel);
}
