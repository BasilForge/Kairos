using System.Globalization;
using System.Text;
using Kairos.Engine.Audio;

namespace Kairos.Engine.Edit;

/// <summary>A drawn item's transparent picture, only as big as the item, and where its top-left goes in the frame.</summary>
public sealed record Overlay(string Path, int X, int Y);

/// <summary>
/// An edit as FFmpeg filter-graph text for the export. Video (<c>[vin]</c> → <c>[vout]</c>): the colour look, blurred
/// boxes and drawn items (transparent PNGs read by <c>movie</c>) laid over in their source times, then the kept pieces
/// trimmed, re-timed for their speed and joined, at the clip's frame rate. Audio (<c>[aN]</c> → <c>[aout]</c>): each of
/// the clip's own tracks at its level (or the mix of a one-track clip), cut and re-timed the same way (tempo, not pitch).
/// </summary>
/// <param name="AudioStreams">The audio streams (0-based) the graph reads, as inputs <c>[aN]</c>.</param>
public sealed record EditGraph(string Video, string Audio, IReadOnlyList<int> AudioStreams)
{
    /// <summary>The first source moment the edit keeps: the export starts reading near it, not at the clip's start.</summary>
    public double From { get; init; }

    /// <summary>The last source moment the edit keeps: the export stops reading soon after it.</summary>
    public double To { get; init; } = double.PositiveInfinity;

    /// <summary>
    /// The sound tracks the graph gives, in the MP4's order: <c>[aout]</c> (the mix), and for Kairos's clips also each
    /// of the clip's own tracks, so an edited clip keeps the clip's layout and can be edited again.
    /// </summary>
    public IReadOnlyList<string> AudioOutputs { get; init; } = ["aout"];

    /// <summary>The names the saved tracks carry (as the recorder writes them), one per <see cref="AudioOutputs"/>.</summary>
    public IReadOnlyList<string> AudioNames { get; init; } = ["Mix"];

    /// <summary>
    /// The area shown at each source moment, for the graph's <c>crop@zoom</c> (the export sets it on every picture); null
    /// when no part zooms (the graph then has no crop).
    /// </summary>
    public Func<double, System.Windows.Rect>? ZoomAt { get; init; }

    /// <summary>The preview's blur (radius 20 on a 720-line picture), kept at the clip's size.</summary>
    const double BlurPerLine = 20 / 720.0;

    /// <param name="layout">The clip's sound tracks (none: read from <paramref name="audioStreams"/> as a clip without names).</param>
    public static EditGraph Build(EditProject p, int width, int height, double fps, int audioStreams,
        IReadOnlyDictionary<Guid, Overlay> overlayPngs, TrackLayout? layout = null)
    {
        var pieces = p.Pieces();
        layout ??= TrackLayout.Of([], audioStreams);
        string audio = AudioGraph(p, pieces, audioStreams, layout, out var streams, out var outputs);
        return new EditGraph(VideoGraph(p, pieces, width, height, fps, overlayPngs), audio, streams)
        {
            From = pieces.Min(piece => piece.Span.Start),
            To = pieces.Max(piece => piece.Span.End),
            AudioOutputs = outputs,
            AudioNames = outputs.Count <= 1 ? ["Mix"] : ["Mix", .. layout.PartitionTracks.Select(t => layout.Labels[t])],
            ZoomAt = p.HasZoom ? p.ZoomAt : null,
        };
    }

    static string VideoGraph(EditProject p, IReadOnlyList<(ClipSpan Span, double Factor)> pieces, int width, int height, double fps,
        IReadOnlyDictionary<Guid, Overlay> overlayPngs)
    {
        var g = new StringBuilder();
        string current = "vin";
        int n = 0;
        string Next() => $"v{n++}";

        if (pieces.All(piece => piece.Span.Look == null))
        {
            if (ColourLooks.Filter(p.Look, p.LookStrength) is { } look)
            {
                string next = Next();
                g.Append($"[{current}]{look}[{next}];");
                current = next;
            }
        }
        else
        {
            // Parts with their own looks: each look on only while its parts play (before texts and drawings, which keep
            // their colours).
            foreach (var group in pieces.GroupBy(piece => p.LookAt((piece.Span.Start + piece.Span.End) / 2)))
            {
                if (ColourLooks.Filter(group.Key.Look, group.Key.Strength) is not { } look)
                    continue;
                string enable = Enable(group.Select(piece => piece.Span))!;
                string next = Next();
                g.Append($"[{current}]{string.Join(",", look.Split(',').Select(f => $"{f}:enable='{enable}'"))}[{next}];");
                current = next;
            }
        }

        // A zoom takes the picture with its blurred boxes, never the texts and drawings over it (as the editor shows it).
        var items = p.HasZoom ? p.Items.OrderBy(item => item is BlurItem ? 0 : 1).ToList() : p.Items;
        bool zoomed = false;
        foreach (var item in items)
        {
            if (p.HasZoom && !zoomed && item is not BlurItem)
                Zoom();
            string? enable = Enable(p.Visible(item));
            if (enable == null)
                continue; // all of it is cut out
            if (item is BlurItem blur)
            {
                var (x, y, w, h) = Pixels(blur.Box, width, height);
                string baseLabel = Next(), source = Next(), blurred = Next(), next = Next();
                g.Append($"[{current}]split[{baseLabel}][{source}];");
                // FFmpeg's boxblur wants a radius under half of a plane's shorter side, and the colour planes are half
                // size (4:2:0); the planar format keeps NV12 (graphics-card decode) colours from being blurred together.
                int radius = Math.Max(1, (int)Math.Round(BlurPerLine * height));
                int side = Math.Min(w, h);
                int luma = Math.Clamp(side / 2 - 1, 0, radius), chroma = Math.Clamp((side + 1) / 2 / 2 - 1, 0, radius);
                g.Append($"[{source}]crop={w}:{h}:{x}:{y},format=yuv420p,");
                g.Append($"boxblur=luma_radius={luma}:luma_power=2:chroma_radius={chroma}:chroma_power=2[{blurred}];");
                g.Append($"[{baseLabel}][{blurred}]overlay={x}:{y}:enable='{enable}'[{next}];");
                current = next;
            }
            else if (overlayPngs.TryGetValue(item.Id, out var png))
            {
                string picture = Next(), next = Next();
                g.Append($"movie={MoviePath(png.Path)}[{picture}];");
                g.Append($"[{current}][{picture}]overlay={png.X}:{png.Y}:enable='{enable}'[{next}];");
                current = next;
            }
        }

        if (p.HasZoom && !zoomed)
            Zoom();

        // Its area is set on every picture by the export (avfilter_graph_send_command); the size stays the clip's.
        void Zoom()
        {
            string next = Next();
            g.Append($"[{current}]crop@zoom=w=iw:h=ih:x=0:y=0,scale={width}:{height}[{next}];");
            current = next;
            zoomed = true;
        }

        string Piece((ClipSpan Span, double Factor) piece) =>
            $"trim=start={N(piece.Span.Start)}:end={N(piece.Span.End)},setpts=(PTS-STARTPTS)/{N(piece.Factor)}";

        if (pieces.Count == 1)
        {
            g.Append($"[{current}]{Piece(pieces[0])},fps={N(fps)}[vout]");
            return g.ToString();
        }
        var outs = Enumerable.Range(0, pieces.Count).Select(_ => Next()).ToList();
        g.Append($"[{current}]split={pieces.Count}{string.Concat(outs.Select(o => $"[{o}]"))};");
        var parts = new List<string>();
        for (int i = 0; i < pieces.Count; i++)
        {
            string part = Next();
            g.Append($"[{outs[i]}]{Piece(pieces[i])}[{part}];");
            parts.Add(part);
        }
        g.Append($"{string.Concat(parts.Select(o => $"[{o}]"))}concat=n={pieces.Count}:v=1:a=0,fps={N(fps)}[vout]");
        return g.ToString();
    }

    static string AudioGraph(EditProject p, IReadOnlyList<(ClipSpan Span, double Factor)> pieces, int audioStreams, TrackLayout layout,
        out IReadOnlyList<int> streams, out IReadOnlyList<string> outputs)
    {
        outputs = ["aout"];
        if (audioStreams <= 0)
        {
            streams = [];
            outputs = [];
            return "";
        }
        var g = new StringBuilder();
        var s = p.Sound;
        int n = pieces.Count;
        // Each track split into one copy per piece (a single piece reads the track itself).
        IReadOnlyList<string> Copies(string track, string prefix)
        {
            if (n == 1)
                return [track];
            var copies = Enumerable.Range(0, n).Select(i => $"{prefix}{i}").ToList();
            g.Append($"[{track}]asplit={n}{string.Concat(copies.Select(c => $"[{c}]"))};");
            return copies;
        }
        string Trim(ClipSpan span) => $"atrim=start={N(span.Start)}:end={N(span.End)},asetpts=PTS-STARTPTS";
        string Tempo(double factor) => Math.Abs(factor - 1) < 1e-9 ? "" : "," + TempoChain(factor);
        var outs = Enumerable.Range(0, n).Select(i => n == 1 ? "aout" : $"ap{i}").ToList();

        var tracks = layout.PartitionTracks;
        if (tracks.Count > 0 && s.Tracks.IsPlain && pieces.All(piece => piece.Span.Levels.IsPlain))
        {
            // Nothing changed in the sound: every track, the mix first, cut and retimed as it was recorded (no mixing).
            streams = [0, .. tracks];
            int k = tracks.Count;
            outputs = ["aout", .. Enumerable.Range(0, k).Select(Output)];
            var all = streams.Select((t, j) => (Copies($"a{t}", j == 0 ? "s" : Prefix(j - 1)), j == 0 ? "aout" : Output(j - 1), j == 0 ? "s" : Prefix(j - 1))).ToList();
            foreach (var (copies, output, prefix) in all)
            {
                var pieceOuts = Enumerable.Range(0, n).Select(i => n == 1 ? output : $"{prefix}q{i}").ToList();
                for (int i = 0; i < n; i++)
                    g.Append($"[{copies[i]}]{Trim(pieces[i].Span)}{Tempo(pieces[i].Factor)}[{pieceOuts[i]}];");
                if (n > 1)
                    g.Append($"{string.Concat(pieceOuts.Select(o => $"[{o}]"))}concat=n={n}:v=0:a=1[{output}];");
            }
            return g.ToString().TrimEnd(';');
        }
        if (tracks.Count > 0)
        {
            // Kairos's clips: track 1 is the mix, the others add up to it (1.0.1: the game and the mic); each piece at
            // the clip's level of each track times its own. The edit keeps that layout: the mix, and each track on its
            // own, cut and set the same way.
            streams = tracks;
            int k = tracks.Count;
            outputs = ["aout", .. Enumerable.Range(0, k).Select(Output)];
            var copies = tracks.Select((t, j) => Copies($"a{t}", Prefix(j))).ToList();
            var trackOuts = Enumerable.Range(0, k).Select(j => Enumerable.Range(0, n).Select(i => n == 1 ? Output(j) : $"{Prefix(j)}q{i}").ToList()).ToList();
            for (int i = 0; i < n; i++)
            {
                var span = pieces[i].Span;
                string tempo = Tempo(pieces[i].Factor);
                for (int j = 0; j < k; j++)
                    g.Append($"[{copies[j][i]}]{Trim(span)},volume={N(s.Tracks.Heard(j) * span.Levels[j])},asplit=2[{Prefix(j)}p{i}][{Prefix(j)}o{i}];");
                g.Append($"{string.Concat(Enumerable.Range(0, k).Select(j => $"[{Prefix(j)}p{i}]"))}amix=inputs={k}:normalize=0{tempo}[{outs[i]}];");
                for (int j = 0; j < k; j++)
                    g.Append($"[{Prefix(j)}o{i}]anull{tempo}[{trackOuts[j][i]}];");
            }
            if (n > 1)
            {
                g.Append($"{string.Concat(outs.Select(o => $"[{o}]"))}concat=n={n}:v=0:a=1[aout];");
                for (int j = 0; j < k; j++)
                    g.Append($"{string.Concat(trackOuts[j].Select(o => $"[{o}]"))}concat=n={n}:v=0:a=1[{Output(j)}];");
            }
            return g.ToString().TrimEnd(';');
        }

        // One track (or a mix and one more): the mix, at the one-track level.
        streams = [0];
        var mix = Copies("a0", "s");
        for (int i = 0; i < n; i++)
        {
            var span = pieces[i].Span;
            g.Append($"[{mix[i]}]{Trim(span)},volume={N(s.All * span.Levels[0])}{Tempo(pieces[i].Factor)}[{outs[i]}];");
        }
        if (n == 1)
            return g.ToString().TrimEnd(';');
        g.Append($"{string.Concat(outs.Select(o => $"[{o}]"))}concat=n={n}:v=0:a=1[aout]");
        return g.ToString();
    }

    /// <summary>A track's labels in the graph: its pieces', and its output's.</summary>
    static string Prefix(int track) => $"t{track}";

    static string Output(int track) => $"at{track}";

    /// <summary>atempo takes 0.5..100 per filter: slower than half speed is a chain of halves.</summary>
    internal static string TempoChain(double factor)
    {
        var parts = new List<string>();
        double left = factor;
        while (left < 0.5 - 1e-9)
        {
            parts.Add("atempo=0.5");
            left /= 0.5;
        }
        parts.Add($"atempo={N(left)}");
        return string.Join(",", parts);
    }

    static string? Enable(IEnumerable<ClipSpan> visible)
    {
        var parts = visible.Select(v => $"between(t,{N(v.Start)},{N(v.End)})").ToList();
        return parts.Count == 0 ? null : string.Join("+", parts);
    }

    /// <summary>A box in whole pixels, even sizes (what the encoders want), inside the picture.</summary>
    static (int X, int Y, int W, int H) Pixels(System.Windows.Rect box, int width, int height)
    {
        int x = Math.Clamp((int)Math.Round(box.X * width), 0, width - 2);
        int y = Math.Clamp((int)Math.Round(box.Y * height), 0, height - 2);
        int w = Math.Clamp((int)Math.Round(box.Width * width), 2, width - x) & ~1;
        int h = Math.Clamp((int)Math.Round(box.Height * height), 2, height - y) & ~1;
        return (x, y, Math.Max(w, 2), Math.Max(h, 2));
    }

    /// <summary>
    /// A file for the <c>movie</c> source: forward slashes, the drive colon escaped for the option parser, quoted for
    /// the graph parser (the files are Kairos's own temporary ones, so they never contain a quote).
    /// </summary>
    static string MoviePath(string path) => "'" + path.Replace('\\', '/').Replace(":", "\\:") + "'";

    static string N(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
