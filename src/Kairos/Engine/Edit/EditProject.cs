using System.Windows;

namespace Kairos.Engine.Edit;

/// <summary>A kept part of the source clip, in seconds, with its own speed and sound.</summary>
/// <param name="Speed">How fast it plays (0.25..4; 1 is normal).</param>
/// <param name="Look">Its own colour look (null: the clip's), at <paramref name="LookStrength"/>.</param>
public sealed record ClipSpan(double Start, double End, double Speed = 1, ColourLook? Look = null, double LookStrength = 1)
{
    public const double MinSpeed = 0.25, MaxSpeed = 4, MaxLevel = 2;

    /// <summary>Its own sound tracks as shares of the clip's levels, 0..2 (a one-track clip: its whole sound in the first).</summary>
    public TrackLevels Levels { get; init; }

    /// <summary>The area of the picture it zooms into (0..1 of the frame, the video's shape); null: the whole picture.</summary>
    public Rect? Zoom { get; init; }

    /// <summary>Plays as it was recorded.</summary>
    public bool IsPlain => Speed == 1 && Levels.IsPlain && Look == null && Zoom == null;

    public double Length => End - Start;
}

/// <summary>
/// The levels (0..2) of a clip's own sound tracks, the ones after the mix, in their order (a 1.0.1 clip: the game,
/// then the mic; a one-track clip: its whole sound in the first), and which are muted. Default: all as recorded.
/// </summary>
public readonly record struct TrackLevels
{
    /// <summary>Kairos records at most six tracks: the mix and five of its own.</summary>
    public const int Count = 5;

    // A level is kept only while it is not 1 (its bit in _set), so the same levels are always the same value.
    readonly double _l0, _l1, _l2, _l3, _l4;
    readonly int _set, _muted;

    TrackLevels(double l0, double l1, double l2, double l3, double l4, int set, int muted) =>
        (_l0, _l1, _l2, _l3, _l4, _set, _muted) = (l0, l1, l2, l3, l4, set, muted);

    public double this[int track] => (uint)track >= Count || (_set & (1 << track)) == 0 ? 1
        : track switch { 0 => _l0, 1 => _l1, 2 => _l2, 3 => _l3, _ => _l4 };

    public bool Muted(int track) => (uint)track < Count && (_muted & (1 << track)) != 0;

    /// <summary>How loud the track is heard: its level, or 0 while muted.</summary>
    public double Heard(int track) => Muted(track) ? 0 : this[track];

    public bool IsPlain => _set == 0 && _muted == 0;

    public TrackLevels With(int track, double level)
    {
        if ((uint)track >= Count)
            return this;
        Span<double> l = [_l0, _l1, _l2, _l3, _l4];
        bool plain = level == 1;
        l[track] = plain ? 0 : level;
        return new(l[0], l[1], l[2], l[3], l[4], plain ? _set & ~(1 << track) : _set | (1 << track), _muted);
    }

    public TrackLevels WithMuted(int track, bool muted) => (uint)track >= Count ? this
        : new(_l0, _l1, _l2, _l3, _l4, _set, muted ? _muted | (1 << track) : _muted & ~(1 << track));

    /// <summary>Every level within 0..<paramref name="max"/>.</summary>
    public TrackLevels Limited(double max)
    {
        var limited = this;
        for (int i = 0; i < Count; i++)
            limited = limited.With(i, Math.Clamp(this[i], 0, max));
        return limited;
    }
}

public enum ShapeKind { Arrow, Ellipse, Rectangle }

public enum ColourLook { None, Vivid, Cinematic, Warm, Cool, Sharp, Mono, Teal, Retro, Golden, Night, Pop, Grain }

/// <summary>
/// Something drawn on the picture for a while. Times are source seconds (it stays on the content when parts are cut);
/// geometry is 0..1 of the picture, so it does not depend on the size it is shown or saved at.
/// </summary>
public abstract record EditItem(Guid Id, double Start, double End);

/// <summary>How a text stands out from the picture.</summary>
public enum TextEffect { Plain, Box, Shadow, Outline, Glow, Neon, Pill }

/// <param name="SizeRel">Text height as a share of the picture height.</param>
/// <param name="Effect">Box: a dark box behind it; Shadow: a shadow below and to the right; Outline: an edge round the letters.</param>
/// <param name="Font">The font's key (the editor's font list); an unknown key draws in the default font.</param>
/// <param name="Group">Lines made together by auto captions share one: their style changes together.</param>
public sealed record TextItem(Guid Id, double Start, double End, string Text, Rect Box, string ColorHex, double SizeRel, TextEffect Effect,
    string Font = "segoe", Guid? Group = null) : EditItem(Id, Start, End);

/// <summary>A pen drawing: the strokes drawn while the Pen tool stayed chosen, one item with one time.</summary>
/// <param name="Strokes">Each stroke's points (the pen lifted between strokes).</param>
/// <param name="ThicknessRel">Line width as a share of the picture height.</param>
public sealed record StrokeItem(Guid Id, double Start, double End, IReadOnlyList<IReadOnlyList<Point>> Strokes, string ColorHex,
    double ThicknessRel) : EditItem(Id, Start, End)
{
    public IEnumerable<Point> AllPoints => Strokes.SelectMany(s => s);
}

public sealed record ShapeItem(Guid Id, double Start, double End, ShapeKind Kind, Point From, Point To, string ColorHex, double ThicknessRel)
    : EditItem(Id, Start, End);

/// <summary>A box whose content is blurred (names, chat...).</summary>
public sealed record BlurItem(Guid Id, double Start, double End, Rect Box) : EditItem(Id, Start, End);

/// <summary>
/// The whole clip's sound levels (0..2): each of its own tracks (a 1.0.1 clip: game and mic), or <paramref name="All"/>,
/// a one-track clip's.
/// </summary>
public sealed record SoundLevels(double All = 1)
{
    /// <summary>The clip's own tracks after the mix: their levels and mutes.</summary>
    public TrackLevels Tracks { get; init; }
}

/// <summary>
/// Everything an edit changes, as one immutable value: the kept parts (each with its speed and sound), what is drawn
/// and when, the clip's sound and colours and (pictures) the crop. The preview and the export both read it; each change
/// makes a new one (undo keeps the old).
/// </summary>
public sealed record EditProject(string Source, double Duration, IReadOnlyList<ClipSpan> Kept, IReadOnlyList<EditItem> Items,
    SoundLevels Sound, ColourLook Look, double LookStrength, Rect? Crop)
{
    /// <summary>A cut nearer than this to another (or to an end) is ignored: less than a frame at 60 fps.</summary>
    public const double MinPiece = 1 / 60.0;

    public static EditProject For(string source, double duration) =>
        new(source, duration, [new ClipSpan(0, duration)], [], new SoundLevels(), ColourLook.None, 1, null);

    public bool IsUnchanged =>
        Kept.Count == 1 && Kept[0].Start <= 0 && Kept[0].End >= Duration && Kept[0].IsPlain && Items.Count == 0
        && Sound == new SoundLevels() && Look == ColourLook.None && Crop == null;

    /// <summary>The kept parts with their speeds, in play order.</summary>
    public IReadOnlyList<(ClipSpan Span, double Factor)> Pieces() => Kept.Select(k => (k, k.Speed)).ToList();

    public double OutputDuration => Pieces().Sum(p => p.Span.Length / p.Factor);

    /// <summary>When a source moment plays in the edit (a cut-out moment: where the next kept part starts).</summary>
    public double ToOutput(double source)
    {
        double output = 0;
        foreach (var (span, factor) in Pieces())
        {
            if (source < span.Start)
                return output;
            if (source <= span.End)
                return output + (source - span.Start) / factor;
            output += span.Length / factor;
        }
        return output;
    }

    /// <summary>The source moment that plays at an edit time, or null outside the edit.</summary>
    public double? ToSource(double output)
    {
        if (output < 0)
            return null;
        double at = 0;
        var pieces = Pieces();
        for (int i = 0; i < pieces.Count; i++)
        {
            var (span, factor) = pieces[i];
            double length = span.Length / factor;
            bool last = i == pieces.Count - 1;
            if (output < at + length || (last && output <= at + length + 1e-9))
                return Math.Min(span.End, span.Start + (output - at) * factor);
            at += length;
        }
        return null;
    }

    /// <summary>Cuts the kept part playing at <paramref name="output"/> in two (ignored within a frame of a cut).</summary>
    public EditProject SplitAt(double output)
    {
        return ToSource(output) is { } s ? SplitAtSource(s) : this;
    }

    /// <summary>Cuts the kept part holding source moment <paramref name="s"/> in two; both halves keep its speed and sound.</summary>
    EditProject SplitAtSource(double s)
    {
        int i = IndexAt(s);
        if (i < 0)
            return this;
        var span = Kept[i];
        if (s - span.Start < MinPiece || span.End - s < MinPiece)
            return this;
        var kept = Kept.ToList();
        kept[i] = span with { End = s };
        kept.Insert(i + 1, span with { Start = s });
        return this with { Kept = kept };
    }

    /// <summary>Some part zooms into the picture.</summary>
    public bool HasZoom => Kept.Any(k => k.Zoom != null);

    /// <summary>
    /// The area shown at a source moment (0..1 of the frame): a zoomed part's own, eased from the part before (the
    /// whole picture when it has no zoom) over its first <see cref="VideoZoom.EaseSeconds"/> as seen, and back to the
    /// whole picture over its last ones when the part after has no zoom; the whole picture elsewhere.
    /// </summary>
    public Rect ZoomAt(double source)
    {
        int i = IndexAt(source);
        if (i < 0 || Kept[i].Zoom is not { } zoom)
            return VideoZoom.Full;
        var span = Kept[i];
        Rect before = i > 0 ? Kept[i - 1].Zoom ?? VideoZoom.Full : VideoZoom.Full;
        bool outToFull = i == Kept.Count - 1 || Kept[i + 1].Zoom == null;
        Rect shown = before == zoom ? zoom : VideoZoom.Lerp(before, zoom, VideoZoom.Ease((source - span.Start) / span.Speed));
        return outToFull ? VideoZoom.Lerp(VideoZoom.Full, shown, VideoZoom.Ease((span.End - source) / span.Speed)) : shown;
    }

    /// <summary>The colour look at a source moment: its part's own, or the clip's.</summary>
    public (ColourLook Look, double Strength) LookAt(double source)
    {
        int i = IndexAt(source);
        return i >= 0 && Kept[i].Look is { } own ? (own, Kept[i].LookStrength) : (Look, LookStrength);
    }

    /// <summary>One look for the whole clip: every part takes it (their own looks go).</summary>
    public EditProject WithLook(ColourLook look) =>
        this with { Look = look, LookStrength = 1, Kept = Kept.Select(k => k with { Look = null, LookStrength = 1 }).ToList() };

    /// <summary>
    /// Q (<paramref name="start"/>): the part playing at <paramref name="output"/> loses what is before it; W: what is after
    /// it. Nothing within a frame of the part's ends.
    /// </summary>
    public EditProject TrimPartAt(double output, bool start)
    {
        if (ToSource(output) is not { } s)
            return this;
        int i = IndexAt(s);
        if (i < 0)
            return this;
        var part = Kept[i];
        if (s - part.Start < MinPiece || part.End - s < MinPiece)
            return this;
        var kept = Kept.ToList();
        kept[i] = start ? part with { Start = s } : part with { End = s };
        return this with { Kept = kept };
    }

    /// <summary>The kept part playing at <paramref name="output"/> (its index), or -1 outside the edit.</summary>
    public int PartAt(double output) => ToSource(output) is { } s ? IndexAt(s) : -1;

    /// <summary>The parts at <paramref name="indices"/> changed (speed and levels kept in their ranges).</summary>
    public EditProject WithParts(IEnumerable<int> indices, Func<ClipSpan, ClipSpan> change)
    {
        var kept = Kept.ToList();
        bool changed = false;
        foreach (int i in indices.Distinct())
        {
            if (i < 0 || i >= kept.Count)
                continue;
            var next = Limit(change(kept[i]));
            if (next == kept[i])
                continue;
            kept[i] = next;
            changed = true;
        }
        return changed ? this with { Kept = kept } : this;
    }

    static ClipSpan Limit(ClipSpan k) => k with
    {
        Speed = Math.Clamp(k.Speed, ClipSpan.MinSpeed, ClipSpan.MaxSpeed),
        Levels = k.Levels.Limited(ClipSpan.MaxLevel),
    };

    /// <summary>Removes the kept part playing at <paramref name="output"/>; the last one stays.</summary>
    public EditProject DeleteSegmentAt(double output)
    {
        if (Kept.Count <= 1 || ToSource(output) is not { } s)
            return this;
        int i = IndexAt(s);
        if (i < 0)
            return this;
        var kept = Kept.ToList();
        kept.RemoveAt(i);
        return this with { Kept = kept };
    }

    /// <summary>Removes the parts at <paramref name="indices"/>; nothing when that would leave no part.</summary>
    public EditProject WithoutParts(IEnumerable<int> indices)
    {
        var gone = indices.Where(i => i >= 0 && i < Kept.Count).ToHashSet();
        if (gone.Count == 0 || gone.Count >= Kept.Count)
            return this;
        return this with { Kept = Kept.Where((_, i) => !gone.Contains(i)).ToList() };
    }

    int IndexAt(double source)
    {
        for (int i = 0; i < Kept.Count; i++)
            if (source >= Kept[i].Start && (source < Kept[i].End || (i == Kept.Count - 1 && source <= Kept[i].End)))
                return i;
        return -1;
    }

    /// <summary>
    /// Plays edit time <paramref name="outputFrom"/>..<paramref name="outputTo"/> at <paramref name="factor"/>: the parts
    /// are split at both ends and those between get the speed (1: normal again).
    /// </summary>
    public EditProject SetSpeed(double outputFrom, double outputTo, double factor)
    {
        if (ToSource(outputFrom) is not { } a || ToSource(outputTo) is not { } b || b - a < MinPiece)
            return this;
        var split = SplitAtSource(a).SplitAtSource(b);
        var inside = Enumerable.Range(0, split.Kept.Count).Where(i => split.Kept[i].Start >= a - 1e-9 && split.Kept[i].End <= b + 1e-9);
        return split.WithParts(inside, k => k with { Speed = factor });
    }

    /// <summary>Adds the item, or replaces the one with its id.</summary>
    public EditProject With(EditItem item)
    {
        var items = Items.ToList();
        int i = items.FindIndex(x => x.Id == item.Id);
        if (i >= 0)
            items[i] = item;
        else
            items.Add(item);
        return this with { Items = items };
    }

    public EditProject Without(Guid id) => this with { Items = Items.Where(x => x.Id != id).ToList() };

    /// <summary>The parts of an item's time that are kept, in source seconds (none when it is all cut out).</summary>
    public IEnumerable<ClipSpan> Visible(EditItem item)
    {
        foreach (var kept in Kept)
        {
            double a = Math.Max(kept.Start, item.Start), b = Math.Min(kept.End, item.End);
            if (b - a > 1e-9)
                yield return new ClipSpan(a, b);
        }
    }
}
