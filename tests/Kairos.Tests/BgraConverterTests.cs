using FFmpeg.AutoGen;
using Kairos.Engine.Share;

namespace Kairos.Tests;

/// <summary>
/// The pictures the player and the stills show, in the colours the clip was recorded in (2026-10-21: the
/// Library's player looked worse than MPC). Kairos records BT.709; swscale assumed BT.601 unless told.
/// </summary>
public static unsafe class BgraConverterTests
{
    /// <summary>A plain YUV 4:2:0 picture of one colour, converted; the (R, G, B) of its middle.</summary>
    static (int R, int G, int B) Convert(AVColorSpace space, AVColorRange range, byte y, byte u, byte v, int height = 16)
    {
        TestFfmpeg.Load();
        const int width = 16;
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = width;
            frame->height = height;
            ClipTranscoder.Check(ffmpeg.av_frame_get_buffer(frame, 0), "a test picture");
            for (int row = 0; row < height; row++)
                new Span<byte>(frame->data[0] + row * frame->linesize[0], width).Fill(y);
            for (int row = 0; row < height / 2; row++)
            {
                new Span<byte>(frame->data[1] + row * frame->linesize[1], width / 2).Fill(u);
                new Span<byte>(frame->data[2] + row * frame->linesize[2], width / 2).Fill(v);
            }
            frame->colorspace = space;
            frame->color_range = range;
            var bgra = new byte[width * height * 4];
            using var converter = new BgraConverter();
            fixed (byte* dst = bgra)
                converter.Convert(frame, width, height, SwsFlags.SWS_BILINEAR, dst, width * 4);
            int at = ((height / 2) * width + width / 2) * 4;
            return (bgra[at + 2], bgra[at + 1], bgra[at]);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    static void IsRed((int R, int G, int B) c, string what) => Assert.True(c.R >= 245 && c.G <= 10 && c.B <= 10, $"{what}: {c}");
    static void IsGreen((int R, int G, int B) c, string what) => Assert.True(c.G >= 245 && c.R <= 10 && c.B <= 10, $"{what}: {c}");

    [Test]
    public static void A_clip_recorded_in_BT709_shows_its_own_colours()
    {
        IsRed(Convert(AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_MPEG, 63, 102, 240), "BT.709 red");
        IsGreen(Convert(AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_MPEG, 173, 42, 26), "BT.709 green");
        IsRed(Convert(AVColorSpace.AVCOL_SPC_BT709, AVColorRange.AVCOL_RANGE_JPEG, 54, 99, 255), "BT.709 full-range red");
    }

    [Test]
    public static void Other_clips_keep_theirs_and_an_untagged_one_is_read_as_players_read_it()
    {
        IsRed(Convert(AVColorSpace.AVCOL_SPC_BT470BG, AVColorRange.AVCOL_RANGE_MPEG, 81, 90, 240), "BT.601 red");
        IsRed(Convert(AVColorSpace.AVCOL_SPC_UNSPECIFIED, AVColorRange.AVCOL_RANGE_UNSPECIFIED, 63, 102, 240, height: 720), "untagged HD: BT.709");
        IsRed(Convert(AVColorSpace.AVCOL_SPC_UNSPECIFIED, AVColorRange.AVCOL_RANGE_UNSPECIFIED, 81, 90, 240, height: 480), "untagged SD: BT.601");
    }
}
