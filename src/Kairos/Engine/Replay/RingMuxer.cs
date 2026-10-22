using FFmpeg.AutoGen;
using Kairos.Engine.Share;

namespace Kairos.Engine.Replay;

/// <summary>An encoder's stream as the MP4 needs it: its codec ("h264", "hevc", "av1", "aac"), size or sound, header bytes.</summary>
public sealed record StreamInfo(string Codec, int Width, int Height, int SampleRate, int Channels, byte[] Extra);

/// <summary>
/// Writes a ring's clip (<see cref="RingSlice"/>) as an MP4: the packets copied as they are (no conversion), the time
/// starting at its first video keyframe, Rec.709 limited colour like the canvas, the index at the end (one pass, no
/// second write of the file). Via a .part file, so a half-written clip never looks like one.
/// </summary>
public static unsafe class RingMuxer
{
    static readonly AVRational Usec = new() { num = 1, den = 1_000_000 };

    /// <summary>Samples in an AAC packet: a sound packet's length.</summary>
    const int AacFrame = 1024;

    /// <summary>Writes the clip; the path it went to ("name (2).mp4" when that name was taken).</summary>
    public static string Write(string path, StreamInfo video, IReadOnlyList<StreamInfo> audio, RingSlice slice)
    {
        ClipTranscoder.RequireLoaded();
        string temp = $"{path}.{Guid.NewGuid():N}.part";
        AVFormatContext* output = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            ClipTranscoder.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", temp), "Preparing the clip");
            var streams = new List<IntPtr> { (IntPtr)AddStream(output, video, AVMediaType.AVMEDIA_TYPE_VIDEO) };
            foreach (var a in audio)
                streams.Add((IntPtr)AddStream(output, a, AVMediaType.AVMEDIA_TYPE_AUDIO));
            ClipTranscoder.Check(ffmpeg.avio_open(&output->pb, temp, ffmpeg.AVIO_FLAG_WRITE), "Creating the clip");
            ClipTranscoder.Check(ffmpeg.avformat_write_header(output, null), "Writing the clip");

            long t0 = slice.Packets[0].DtsUsec;
            foreach (var (p, data) in slice.ReadAll())
            {
                if (p.Track >= streams.Count)
                    continue;
                var stream = (AVStream*)streams[p.Track];
                // libobs counts in ticks of 1/den; a video frame is num ticks (more than 1 with a frame-rate divisor).
                var tb = new AVRational { num = 1, den = p.TbDen };
                long shift = ffmpeg.av_rescale_q(t0, Usec, tb);
                ClipTranscoder.Check(ffmpeg.av_new_packet(packet, data.Length), "Writing the clip");
                data.Span.CopyTo(new Span<byte>(packet->data, data.Length));
                packet->pts = p.Pts - shift;
                packet->dts = p.Dts - shift;
                packet->duration = p.Track == 0 ? Math.Max(1, p.TbNum) : AacFrame; // the last one counts too
                packet->flags = p.Key ? ffmpeg.AV_PKT_FLAG_KEY : 0;
                packet->stream_index = stream->index;
                packet->pos = -1;
                ffmpeg.av_packet_rescale_ts(packet, tb, stream->time_base);
                ClipTranscoder.Check(ffmpeg.av_interleaved_write_frame(output, packet), "Writing the clip");
            }
            ClipTranscoder.Check(ffmpeg.av_write_trailer(output), "Finishing the clip");
            ffmpeg.avio_closep(&output->pb);
            return ClipTranscoder.Place(temp, path);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (output != null)
            {
                if (output->pb != null)
                    ffmpeg.avio_closep(&output->pb);
                ffmpeg.avformat_free_context(output);
            }
            ClipTranscoder.TryDelete(temp);
        }
    }

    static AVStream* AddStream(AVFormatContext* output, StreamInfo info, AVMediaType type)
    {
        AVStream* stream = ffmpeg.avformat_new_stream(output, null);
        AVCodecParameters* par = stream->codecpar;
        AVCodecDescriptor* codec = ffmpeg.avcodec_descriptor_get_by_name(info.Codec);
        if (codec == null)
            throw new ShareException($"Unknown codec {info.Codec}");
        par->codec_type = type;
        par->codec_id = codec->id;
        if (info.Extra.Length > 0)
        {
            par->extradata = (byte*)ffmpeg.av_mallocz((ulong)(info.Extra.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            info.Extra.CopyTo(new Span<byte>(par->extradata, info.Extra.Length));
            par->extradata_size = info.Extra.Length;
        }
        if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
        {
            if (par->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
                par->codec_tag = 0x31637668; // 'hvc1', as libobs's own replay writes it (players expect it)
            par->width = info.Width;
            par->height = info.Height;
            par->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            par->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            par->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            par->color_space = AVColorSpace.AVCOL_SPC_BT709;
        }
        else
        {
            par->sample_rate = info.SampleRate;
            ffmpeg.av_channel_layout_default(&par->ch_layout, info.Channels);
            par->frame_size = AacFrame;
        }
        return stream;
    }
}
