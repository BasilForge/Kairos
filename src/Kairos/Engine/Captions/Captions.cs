using System.Text.RegularExpressions;
using System.Windows;
using Kairos.Engine.Edit;

namespace Kairos.Engine.Captions;

/// <summary>One spoken line: when it is said (source seconds) and what.</summary>
public sealed record Caption(double Start, double End, string Text);

/// <summary>Turns spoken lines into the editor's text items.</summary>
public static partial class Captions
{
    /// <summary>Longer lines become several items, each on screen for its share of the time.</summary>
    public const int MaxLine = 42;
    /// <summary>The space under a caption (share of the picture's height).</summary>
    public const double BottomMargin = 0.08;

    /// <summary>The audio streams to listen to: the clip's voice tracks (the mic, Discord) heard together; else the mix.</summary>
    public static IReadOnlyList<int> Streams(Audio.TrackLayout layout) => layout.VoiceTracks is { Count: > 0 } voices ? voices : [0];

    /// <param name="measure">The box a line needs at <paramref name="sizeRel"/> (the text and room around it), as shares of the
    /// picture's width and height.</param>
    /// <param name="effect">The text tool's style, <paramref name="font"/> its font: the lines look like the text tool says.</param>
    public static IReadOnlyList<TextItem> ToItems(IEnumerable<Caption> captions, Func<string, Size> measure, string colorHex, double sizeRel,
        TextEffect effect, string font)
    {
        var items = new List<TextItem>();
        var group = Guid.NewGuid(); // restyled together
        string? lastKey = null;
        double lastEnd = double.NegativeInfinity;
        foreach (var caption in captions)
        {
            string text = Spoken(caption.Text);
            if (text.Length == 0 || caption.End <= caption.Start)
                continue;
            // Whisper sometimes writes the same line again right after: the line stays up instead of showing twice.
            string key = Key(text);
            if (key == lastKey && caption.Start - lastEnd < RepeatGap)
            {
                items[^1] = items[^1] with { End = Math.Max(items[^1].End, caption.End) };
                lastEnd = items[^1].End;
                continue;
            }
            lastKey = key;
            lastEnd = caption.End;
            var lines = Lines(text);
            double total = lines.Sum(l => l.Length), at = caption.Start;
            for (int i = 0; i < lines.Count; i++)
            {
                double end = i == lines.Count - 1 ? caption.End : at + (caption.End - caption.Start) * lines[i].Length / total;
                items.Add(new TextItem(Guid.NewGuid(), at, end, lines[i], Box(measure(lines[i])), colorHex, sizeRel, effect, font, group));
                at = end;
            }
        }
        return items;
    }

    /// <summary>A repeat closer than this (s) to the line it repeats is the same line.</summary>
    const double RepeatGap = 1.0;

    /// <summary>The words alone (no case, no punctuation), to tell a repeated line.</summary>
    static string Key(string text) =>
        Spaces().Replace(new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray()), " ").Trim();

    /// <summary>The text without what Whisper writes for sounds ("[Music]", "(laughs)", "♪").</summary>
    static string Spoken(string text)
    {
        text = NotSpeech().Replace(text, " ").Replace('�', ' ');
        text = Spaces().Replace(text, " ").Trim();
        return text.Any(char.IsLetterOrDigit) ? text : "";
    }

    /// <summary>Words in order, in as few lines of at most <see cref="MaxLine"/> characters as fit, of even lengths
    /// (a single longer word stays whole).</summary>
    static List<string> Lines(string text)
    {
        if (text.Length <= MaxLine)
            return [text];
        int count = (int)Math.Ceiling(text.Length / (double)MaxLine);
        double target = text.Length / (double)count;
        var lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string longer = line.Length == 0 ? word : line + " " + word;
            // A new line once this one has its share, or when the word would not fit.
            bool full = lines.Count < count - 1 && Math.Abs(longer.Length - target) > Math.Abs(line.Length - target);
            if (line.Length > 0 && (longer.Length > MaxLine || full))
            {
                lines.Add(line);
                line = word;
            }
            else
                line = longer;
        }
        lines.Add(line);
        return lines;
    }

    /// <summary>Centred near the bottom.</summary>
    static Rect Box(Size size)
    {
        double w = Math.Min(1, size.Width), h = Math.Min(1, size.Height);
        return new Rect((1 - w) / 2, Math.Max(0, 1 - BottomMargin - h), w, h);
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫]")]
    private static partial Regex NotSpeech();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
