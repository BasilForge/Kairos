using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kairos.Engine.Share;

/// <summary>Copy: an MP4 every app plays (Discord, WhatsApp, Telegram...) put on the clipboard. All: X, TikTok and
/// Instagram at once.</summary>
public enum SharePlatform { Copy, X, TikTok, Instagram, All }

public sealed record CropRect(int X, int Y, int Width, int Height);

/// <summary>Output picture: final size and the source window it comes from (null = the whole frame).</summary>
public sealed record ShareFrame(int Width, int Height, CropRect? Crop);

/// <summary>What a platform accepts. MaxBytes = long.MaxValue when only the quality cap matters.</summary>
/// <param name="QualityCap">Most video bitrate worth sending to this platform.</param>
public sealed record ShareSpec(SharePlatform Platform, long MaxBytes, double MaxSeconds, int MaxShortSide, double MaxFps,
    long QualityCap = SharePlanner.QualityCapBitrate);

/// <summary>Everything the transcoder needs: which part of the clip, the picture and the video bitrate.</summary>
/// <param name="AudioBitrate">The sound re-encoded at this bitrate to fit a size; 0: copied as it is.</param>
public sealed record SharePlan(double Start, double Duration, int Width, int Height, CropRect? Crop, double Fps, long VideoBitrate,
    long AudioBitrate = 0);

/// <summary>One stop on the quality slider: the picture's short side in pixels; Original = the clip's own size.</summary>
public sealed record SizeStep(int ShortSide, bool Original);

/// <summary>Pure decisions for sharing a clip: limits per platform, segment, crop, bitrate, caption and links.</summary>
public static partial class SharePlanner
{
    /// <summary>Bitrate that already looks clean for 1080p60 H.264; more only makes the upload bigger. Other sizes get
    /// the same bits per pixel.</summary>
    public const long QualityCapBitrate = 12_000_000;
    const double FullHdPixels = 1920 * 1080;

    /// <summary>Share of the size limit the estimate may use: leaves room for MP4 overhead and encoder overshoot.</summary>
    public const double SizeMargin = 0.92;

    /// <summary>Below this many bits per pixel H.264 turns to blocks in fast games, so a smaller picture wins.</summary>
    const double MinBitsPerPixel = 0.035;

    /// <summary>
    /// The biggest picture a site takes: X 5 MB, Instagram 8 MB, TikTok 50 MB a photo (its Photos tab, seen
    /// 2026-10-15). All three take PNG and JPEG.
    /// </summary>
    public static long PictureLimit(SharePlatform platform) => platform switch
    {
        SharePlatform.X => 5_000_000,
        SharePlatform.Instagram => 8_000_000,
        SharePlatform.TikTok => 50_000_000,
        _ => long.MaxValue,
    };

    /// <summary>A screenshot over the site's limit goes as a JPEG copy; anything else goes as it is.</summary>
    public static bool PictureNeedsJpeg(SharePlatform platform, long bytes) => bytes > PictureLimit(platform);

    /// <param name="xPremium">The X account has Premium (longer and bigger videos).</param>
    /// <remarks>
    /// The clip goes at its own size (up to 4K): measured 2026-10-13 with VMAF after a simulated site re-encode, the
    /// clip's own 1440p ended a little ahead of 1080p, and 1080p at 6 Mb/s 1-2 points behind 12 Mb/s. Only X keeps
    /// 1080p, the most its uploads take (1920x1200).
    /// </remarks>
    public static ShareSpec Spec(SharePlatform platform, bool xPremium = false) => platform switch
    {
        // Copy: no size limit; the quality cap sets the size. Discord and chat apps keep the file as it is.
        SharePlatform.Copy => new(platform, long.MaxValue, double.PositiveInfinity, 2160, 60),
        // X: H.264 + AAC up to 1920x1200 at 60 fps; 2:20 and 512 MB, or with Premium 3 hours and 8 GB from a computer.
        SharePlatform.X => xPremium ? new(platform, 8_000_000_000, 3 * 3600, 1080, 60) : new(platform, 512_000_000, 140, 1080, 60),
        // TikTok accepts up to 10 minutes from the computer (files up to 4 GB); Instagram Reels up to 3.
        SharePlatform.TikTok => new(platform, long.MaxValue, 600, 2160, 60),
        SharePlatform.Instagram => new(platform, long.MaxValue, 180, 2160, 60),
        // Everywhere: one cut that every site takes (each site still gets its own picture size).
        SharePlatform.All => Strictest(platform, SiteAccounts.Sites.Select(p => Spec(p, xPremium)).ToList()),
        _ => throw new ArgumentOutOfRangeException(nameof(platform), "the file option shares the clip as it is"),
    };

    /// <summary>
    /// Whether a clip in <paramref name="videoCodec"/> (FFmpeg's name) is handed on as it is when copied or dragged out:
    /// HEVC plays neither in Discord nor on many phones or a Windows without its extension, so it goes as H.264 (the
    /// owner, 2026-10-21). AV1 plays in Discord and on today's phones.
    /// </summary>
    public static bool PlaysEverywhere(string videoCodec) => videoCodec != "hevc";

    /// <summary>The sound's bitrate a plan starts from: the clip's own, 192 kbps when it does not say, none without sound.</summary>
    public static long AudioBitrateOf(MediaInfo info) => info.AudioCodec == null ? 0 : info.AudioBitrate > 0 ? info.AudioBitrate : 192_000;

    /// <summary>A copy of <paramref name="info"/>'s <paramref name="start"/>..+<paramref name="duration"/> at Copy's size (0: its own).</summary>
    public static SharePlan CopyPlan(int mb, MediaInfo info, double start, double duration)
    {
        var spec = CopySpec(mb);
        long audio = AudioBitrateOf(info);
        var size = DefaultSize(spec, duration, info.Width, info.Height, info.Fps, audio, false);
        return Plan(spec, start, duration, info.Width, info.Height, info.Fps, audio, false, 0.5, size.ShortSide);
    }

    /// <summary>Copy's sizes in MB, each with what it is for: the clip's own (0), and what Discord takes free, with Nitro Basic and with Nitro.</summary>
    public static IReadOnlyList<(int Mb, string For)> CopyTiers { get; } = [(0, ""), (10, "Discord"), (50, "Nitro Basic"), (500, "Nitro")];

    public static IReadOnlyList<int> CopySizes { get; } = [.. CopyTiers.Select(t => t.Mb)];

    /// <summary>Copy made to fit <paramref name="mb"/> MB (0: no limit, the quality cap sets the size).</summary>
    public static ShareSpec CopySpec(int mb) =>
        mb > 0 ? Spec(SharePlatform.Copy) with { MaxBytes = mb * 1_000_000L } : Spec(SharePlatform.Copy);

    /// <summary>The sound's bitrates when a size makes it smaller, biggest first.</summary>
    static readonly long[] AudioSteps = [192_000, 128_000, 96_000, 64_000];

    /// <summary>
    /// The sound's bitrate for a size: 0 (copied as it is) when the source takes no more than a tenth of what the size
    /// leaves each second; otherwise the biggest step that does, never below 64 kb/s.
    /// </summary>
    public static long AudioFor(ShareSpec spec, double duration, long sourceBitrate)
    {
        if (spec.MaxBytes == long.MaxValue || duration <= 0)
            return 0;
        double share = spec.MaxBytes * SizeMargin * 8 / duration * 0.10;
        if (sourceBitrate > 0 && sourceBitrate <= share)
            return 0;
        long step = AudioSteps.FirstOrDefault(s => s <= share, AudioSteps[^1]);
        return sourceBitrate > 0 && step >= sourceBitrate ? 0 : step;
    }

    /// <summary>
    /// The longest part that fits the size at all: the smallest picture at 30 fps and the lowest sound
    /// (+∞ with no size limit).
    /// </summary>
    public static double SecondsThatFit(ShareSpec spec, int srcWidth, int srcHeight, bool vertical)
    {
        if (spec.MaxBytes == long.MaxValue)
            return double.PositiveInfinity;
        var smallest = SizeSteps(SourceShortSide(srcWidth, srcHeight, vertical), spec.MaxShortSide)[0];
        var frame = Frame(srcWidth, srcHeight, vertical, 0.5, smallest.ShortSide);
        double minVideo = (double)frame.Width * frame.Height * 30 * MinBitsPerPixel;
        return spec.MaxBytes * SizeMargin * 8 / (minVideo + AudioSteps[^1]);
    }

    static ShareSpec Strictest(SharePlatform platform, List<ShareSpec> specs) => new(platform, specs.Min(s => s.MaxBytes),
        specs.Min(s => s.MaxSeconds), specs.Min(s => s.MaxShortSide), specs.Min(s => s.MaxFps));

    /// <summary>Shortest part that can be shared.</summary>
    public const double MinTrimSeconds = 1;

    /// <summary>The first cut: as much as the platform takes, up to the end of the clip (that is where the moment is).</summary>
    public static (double Start, double End) DefaultTrim(double clipSeconds, double maxSeconds) =>
        ClampTrim(0, clipSeconds, clipSeconds, maxSeconds);


    /// <summary>
    /// Keeps a cut inside the clip, at least a second long and no longer than the platform takes. When it is too
    /// long the end stays where the user put it and the start moves up.
    /// </summary>
    public static (double Start, double End) ClampTrim(double start, double end, double clipSeconds, double maxSeconds)
    {
        double min = Math.Min(MinTrimSeconds, clipSeconds);
        start = Math.Clamp(start, 0, clipSeconds);
        end = Math.Clamp(end, 0, clipSeconds);
        if (end - start < min)
        {
            // Too short: grow at the end, or at the start when the end is already the clip's end.
            end = Math.Min(clipSeconds, start + min);
            start = end - min;
        }
        if (end - start > maxSeconds)
            start = end - maxSeconds;
        return (start, end);
    }

    /// <summary>Picture sizes (short side) the quality slider stops at, besides the clip's own.</summary>
    static readonly int[] StandardSides = [480, 720, 1080, 1440, 2160];

    /// <summary>
    /// The quality slider, smallest first: the standard sizes up to <paramref name="maxShortSide"/> with the clip's own
    /// size among them (when the platform takes it).
    /// </summary>
    public static IReadOnlyList<SizeStep> SizeSteps(int sourceShortSide, int maxShortSide)
    {
        var steps = StandardSides.Where(s => s <= maxShortSide && s != sourceShortSide).Select(s => new SizeStep(s, false)).ToList();
        if (sourceShortSide <= maxShortSide)
            steps.Add(new SizeStep(sourceShortSide, true));
        return steps.OrderBy(s => s.ShortSide).ToList();
    }

    /// <summary>Short side of the picture before any scaling: the frame's height, or the 9:16 window's width.</summary>
    public static int SourceShortSide(int srcWidth, int srcHeight, bool vertical)
    {
        var frame = Frame(srcWidth, srcHeight, vertical, 0.5, 0);
        return Math.Min(frame.Width, frame.Height);
    }

    /// <summary>
    /// Where the slider starts: the clip's own size, or the platform's top when the clip is bigger, stepping down
    /// while a size limit would leave too few bits for a clean picture at full frame rate.
    /// </summary>
    public static SizeStep DefaultSize(ShareSpec spec, double duration, int srcWidth, int srcHeight, double srcFps, long audioBitrate, bool vertical)
    {
        var steps = SizeSteps(SourceShortSide(srcWidth, srcHeight, vertical), spec.MaxShortSide);
        int top = steps.ToList().FindIndex(s => s.Original);
        if (top < 0)
            top = steps.Count - 1;
        double fast = FastFps(spec, srcFps);
        for (int i = top; i >= 0; i--)
        {
            var plan = Plan(spec, 0, duration, srcWidth, srcHeight, srcFps, audioBitrate, vertical, 0.5, steps[i].ShortSide);
            if (plan.Fps == fast)
                return steps[i];
        }
        return steps[0];
    }

    /// <summary>
    /// Landscape: the whole frame scaled so its short side is <paramref name="shortSide"/>.
    /// Vertical: a 9:16 window slid by <paramref name="cropPosition"/> (0 = left, 1 = right), scaled so its width is
    /// <paramref name="shortSide"/>. 0 keeps the size; every size and offset is even (4:2:0 video).
    /// </summary>
    public static ShareFrame Frame(int srcWidth, int srcHeight, bool vertical, double cropPosition, int shortSide)
    {
        CropRect? crop = null;
        int w = srcWidth, h = srcHeight;
        if (vertical)
        {
            bool wide = srcHeight * 9 / 16 <= srcWidth;
            int cw = (wide ? srcHeight * 9 / 16 : srcWidth) & ~1;
            int ch = (wide ? srcHeight : Math.Min(srcHeight, cw * 16 / 9)) & ~1;
            int x = (int)Math.Round((srcWidth - cw) * Math.Clamp(cropPosition, 0, 1)) & ~1;
            int y = ((srcHeight - ch) / 2) & ~1;
            crop = new CropRect(x, y, cw, ch);
            w = cw;
            h = ch;
        }

        int side = Math.Min(w, h);
        if (shortSide > 0 && shortSide != side)
        {
            double scale = (double)shortSide / side;
            w = (int)Math.Round(w * scale);
            h = (int)Math.Round(h * scale);
        }
        return new ShareFrame(w & ~1, h & ~1, crop);
    }

    static double FastFps(ShareSpec spec, double srcFps) => Math.Min(srcFps > 0 ? srcFps : 60, spec.MaxFps);

    /// <param name="start">Seconds into the clip where the shared part starts.</param>
    /// <param name="duration">Length of the shared part (already within the platform's limit).</param>
    /// <param name="shortSide">The picture's short side from the quality slider (0 = the clip's own size).</param>
    public static SharePlan Plan(ShareSpec spec, double start, double duration, int srcWidth, int srcHeight, double srcFps, long audioBitrate,
        bool vertical, double cropPosition, int shortSide)
    {
        ShareFrame frame = Frame(srcWidth, srcHeight, vertical, cropPosition, shortSide);
        double pixels = (double)frame.Width * frame.Height;
        long videoBitrate = (long)(spec.QualityCap * (pixels / FullHdPixels));
        long audio = AudioFor(spec, duration, audioBitrate);
        if (spec.MaxBytes != long.MaxValue && duration > 0)
        {
            double totalBits = spec.MaxBytes * SizeMargin * 8;
            videoBitrate = Math.Min(videoBitrate, (long)(totalBits / duration) - (audio > 0 ? audio : audioBitrate));
        }
        videoBitrate = Math.Max(videoBitrate, 100_000);

        // Full frame rate while the picture stays clean, else 30 fps so each frame gets more bits.
        double fast = FastFps(spec, srcFps);
        double fps = videoBitrate / (pixels * fast) >= MinBitsPerPixel ? fast : Math.Min(fast, 30);
        return new SharePlan(start, duration, frame.Width, frame.Height, frame.Crop, fps, videoBitrate, audio);
    }

    /// <summary>The app name Kairos put in front of the date, or the whole name for a renamed file.</summary>
    public static string GameFromFile(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        var m = SavedName().Match(stem);
        return m.Success ? m.Groups[1].Value : stem;
    }

    /// <summary>The game as a hashtag followed by the user's own hashtags.</summary>
    public static string Caption(string game, string savedTags)
    {
        string tag = GameTag(game);
        var parts = new List<string>();
        if (tag.Length > 0)
            parts.Add(tag);
        foreach (string t in Hashtags(savedTags))
        {
            if (!parts.Contains(t, StringComparer.OrdinalIgnoreCase))
                parts.Add(t);
        }
        return string.Join(' ', parts);
    }

    /// <summary>The hashtags worth suggesting next time: the user's own, not this clip's game.</summary>
    public static string RememberTags(string caption, string game)
    {
        string gameTag = GameTag(game);
        var kept = new List<string>();
        foreach (string t in Hashtags(caption))
        {
            if (!t.Equals(gameTag, StringComparison.OrdinalIgnoreCase) && !kept.Contains(t, StringComparer.OrdinalIgnoreCase))
                kept.Add(t);
        }
        return string.Join(' ', kept);
    }

    /// <summary>What to open after the file is ready: the X composer with the caption, or the platform's upload page.</summary>
    public static string OpenTarget(SharePlatform platform, string caption) => platform switch
    {
        SharePlatform.X => "https://x.com/intent/post?text=" + Uri.EscapeDataString(caption),
        SharePlatform.TikTok => "https://www.tiktok.com/tiktokstudio/upload",
        SharePlatform.Instagram => "https://www.instagram.com/",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    static string GameTag(string game)
    {
        if (string.IsNullOrWhiteSpace(game) || game.Equals("Desktop", StringComparison.OrdinalIgnoreCase))
            return "";
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in game.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                sb.Append(c);
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            words.Add(sb.ToString());
        if (words.Count == 0)
            return "";

        // Latin names read well as PascalCase (#EldenRing); other scripts keep their words apart (#لعبة_تجريبية).
        bool latin = words.All(w => w.All(c => c < 0x250));
        return "#" + (latin ? string.Concat(words.Select(Capitalize)) : string.Join('_', words));
    }

    static string Capitalize(string word)
    {
        bool mixed = word.Any(char.IsUpper) && word.Any(char.IsLower);
        if (mixed)
            return word; // TikTok, YouTube: the name's own capitals
        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }

    static IEnumerable<string> Hashtags(string text) =>
        Hashtag().Matches(text ?? "").Select(m => m.Value);

    [GeneratedRegex(@"#[\p{L}\p{Mn}\p{Nd}_]+")]
    private static partial Regex Hashtag();

    [GeneratedRegex(@"^(.+?) \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}(?: \(\d+\))?$")]
    private static partial Regex SavedName();
}
