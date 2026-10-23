using FFmpeg.AutoGen;
using Kairos.Engine.Replay;
using Kairos.Engine.Share;

namespace Kairos.Tests;

/// <summary>A clip from the ring: its packets copied into an MP4 as they are (no conversion), from a keyframe.</summary>
public static unsafe class RingMuxerTests
{
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share");

    /// <summary>Every packet of <paramref name="clip"/> into a ring (the way libobs hands them over), and its streams.</summary>
    static (ReplayRing Ring, StreamInfo Video, List<StreamInfo> Audio) Feed(string clip)
    {
        string path = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), DiskReplayPlace.FileName);
        var ring = new ReplayRing(path, capacity: 16L << 20, keepSeconds: 60, blockBytes: 1 << 20);
        AVFormatContext* input = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        StreamInfo? video = null;
        var audio = new List<StreamInfo>();
        var track = new Dictionary<int, int>();
        try
        {
            ffmpeg.avformat_open_input(&input, clip, null, null);
            ffmpeg.avformat_find_stream_info(input, null);
            for (int i = 0; i < input->nb_streams; i++)
            {
                var par = input->streams[i]->codecpar;
                var extra = new ReadOnlySpan<byte>(par->extradata, par->extradata_size).ToArray();
                var info = new StreamInfo(ffmpeg.avcodec_get_name(par->codec_id), par->width, par->height, par->sample_rate, par->ch_layout.nb_channels, extra);
                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && video == null)
                {
                    video = info;
                    track[i] = 0;
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    audio.Add(info);
                    track[i] = audio.Count;
                }
            }
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                if (track.TryGetValue(packet->stream_index, out int t))
                {
                    var tb = input->streams[packet->stream_index]->time_base;
                    long dts = packet->dts == ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                    long usec = ffmpeg.av_rescale_q(dts, tb, new AVRational { num = 1, den = 1_000_000 });
                    // Stamped as libobs stamps: ticks of 1/den, a video frame num ticks long (here 256 at 1/15360 s: more
                    // than 1, like a camera file with a frame-rate divisor), sound counted in samples.
                    int frame = t == 0 ? (int)packet->duration : 1;
                    ring.Append(new RingPacket(packet->pts * tb.num, dts * tb.num, usec, frame * tb.num, tb.den, t, (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0),
                        new ReadOnlySpan<byte>(packet->data, packet->size));
                }
                ffmpeg.av_packet_unref(packet);
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&input);
        }
        return (ring, video!, audio);
    }

    [Test]
    public static void A_clip_from_the_ring_plays_with_its_tracks_from_a_keyframe()
    {
        TestFfmpeg.Load();
        foreach (string name in new[] { "clip-h264.mp4", "clip-h264-5tracks.mp4", "clip-av1-2tracks.mp4" })
        {
            var source = ClipTranscoder.Probe(Path.Combine(Fixtures, name));
            var (ring, video, audio) = Feed(Path.Combine(Fixtures, name));
            using (ring)
            {
                var slice = ring.Take(3)!;
                string target = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N") + ".mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                RingMuxer.Write(target, video, audio, slice);
                var made = ClipTranscoder.Probe(target);
                Assert.Equal(source.VideoCodec, made.VideoCodec, $"{name}: the same video");
                Assert.Equal(source.AudioStreams, made.AudioStreams, $"{name}: every sound track");
                Assert.Equal((source.Width, source.Height), (made.Width, made.Height), $"{name}: the same size");
                Assert.True(made.Duration > 2 && made.Duration <= Math.Min(source.Duration + 0.1, 4.1), $"{name}: about the last 3 s ({made.Duration:F2} s)");
                Assert.True(Math.Abs(made.Fps - source.Fps) < 0.5, $"{name}: the same rate ({made.Fps})");
            }
        }
    }
}
