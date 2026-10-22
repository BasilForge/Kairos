using FFmpeg.AutoGen;

namespace Kairos.Engine.Share;

/// <summary>
/// The sound codecs Kairos opens with OBS's FFmpeg: a decoder for a track of the clip, the matching sound-graph input,
/// and the AAC encoder every MP4 it writes carries (48 kHz stereo).
/// </summary>
static unsafe class SoundCodecs
{
    /// <summary>The decoder for <paramref name="stream"/>, opened (its timestamps in the stream's time base).</summary>
    public static AVCodecContext* OpenDecoder(AVStream* stream)
    {
        AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec == null)
            throw new ShareException("The clip's sound cannot be read.");
        AVCodecContext* decoder = ffmpeg.avcodec_alloc_context3(codec);
        try
        {
            ClipTranscoder.Check(ffmpeg.avcodec_parameters_to_context(decoder, stream->codecpar), "Preparing the sound decoder");
            decoder->pkt_timebase = stream->time_base;
            ClipTranscoder.Check(ffmpeg.avcodec_open2(decoder, codec, null), "Opening the sound decoder");
            return decoder;
        }
        catch
        {
            ffmpeg.avcodec_free_context(&decoder);
            throw;
        }
    }

    /// <summary>An abuffer's arguments for what <paramref name="decoder"/> gives, timed in <paramref name="timeBase"/>.</summary>
    public static string BufferArgs(AVCodecContext* decoder, AVRational timeBase)
    {
        byte* layout = stackalloc byte[64];
        ffmpeg.av_channel_layout_describe(&decoder->ch_layout, layout, 64);
        return $"time_base={timeBase.num}/{timeBase.den}:sample_rate={decoder->sample_rate}" +
               $":sample_fmt={ffmpeg.av_get_sample_fmt_name(decoder->sample_fmt)}:channel_layout={new string((sbyte*)layout)}";
    }

    /// <summary>What a sound graph ends with before <see cref="OpenAac"/>'s encoder.</summary>
    public const string AacFormat = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";

    /// <summary>The AAC encoder, opened: 48 kHz stereo at <paramref name="bitrate"/>, its header kept for the MP4.</summary>
    public static AVCodecContext* OpenAac(long bitrate)
    {
        AVCodec* aac = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        AVCodecContext* encoder = ffmpeg.avcodec_alloc_context3(aac);
        encoder->sample_rate = 48_000;
        encoder->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        ffmpeg.av_channel_layout_default(&encoder->ch_layout, 2);
        encoder->bit_rate = bitrate;
        encoder->time_base = new AVRational { num = 1, den = 48_000 };
        encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        int opened = ffmpeg.avcodec_open2(encoder, aac, null);
        if (opened < 0)
            ffmpeg.avcodec_free_context(&encoder);
        ClipTranscoder.Check(opened, "Opening the AAC encoder");
        return encoder;
    }
}
