using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Kairos.Engine.Share;

/// <summary>
/// Decodes a clip for the share panel's player. From a point on it hands out pictures at a fixed size (BGRA, converted
/// only when asked, so a late one costs little) and a sound track (the first: the full mix), or several mixed at their
/// gains, as 16-bit stereo at 48 kHz, trimmed to start exactly at that point. Times are seconds into the clip, the
/// trim's clock.
/// Pictures and sound are read apart (the file is opened twice, each reading only its own track), so the caller can
/// keep the sound ahead however the file stores them: many recorders write a second or more of pictures, then that
/// second's sound. The caller paces it. Not thread-safe.
/// </summary>
public sealed unsafe class ClipPlayback : IDisposable
{
    public const int SampleRate = 48_000;
    /// <summary>Bytes per sound sample: two 16-bit channels.</summary>
    public const int BytesPerFrame = 4;

    AVFormatContext* _format;
    AVCodecContext* _video;
    AVBufferRef* _device;
    AVFrame* _picture;
    AVPacket* _packet;
    readonly BgraConverter _converter = new();
    readonly int _videoIndex;
    readonly AVRational _videoBase;
    /// <summary>Clip time 0: the video stream's first timestamp, in seconds.</summary>
    readonly double _origin;
    readonly double _frameSeconds;
    readonly int _width, _height;
    double _from;
    bool _videoEof, _videoDone;

    /// <summary>The one sound track played as it is (null: none, or several mixed).</summary>
    readonly SoundReader? _single;
    /// <summary>The tracks mixed at the gains of <see cref="_mix"/> (none when one plays as it is).</summary>
    readonly MixTrack[] _tracks = [];
    readonly TrackMix? _mix;
    float[] _sum = [];
    byte[] _mixed = [];
    int _mixedLength;
    double _mixAt;

    /// <param name="width">Picture width in pixels (even).</param>
    /// <param name="height">Picture height in pixels (even).</param>
    /// <param name="audioTrack">The sound track played, 0-based (the first is the full mix).</param>
    /// <param name="mix">Instead, several tracks mixed at their gains (the editor's own level for each).</param>
    public ClipPlayback(string path, int width, int height, int audioTrack = 0, TrackMix? mix = null)
    {
        if (!File.Exists(path))
            throw new ShareException($"The clip was not found: {path}");
        _width = Math.Max(2, width & ~1);
        _height = Math.Max(2, height & ~1);
        ClipTranscoder.RequireLoaded();
        AVFormatContext* format = null;
        ClipTranscoder.Check(ffmpeg.avformat_open_input(&format, path, null, null), "Opening the clip");
        _format = format;
        try
        {
            ClipTranscoder.Check(ffmpeg.avformat_find_stream_info(format, null), "Reading the clip");
            _videoIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (_videoIndex < 0)
                throw new ShareException("The clip has no video.");
            AVStream* video = format->streams[_videoIndex];
            _videoBase = video->time_base;
            _origin = (video->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : video->start_time) * ffmpeg.av_q2d(_videoBase);
            AVRational rate = video->avg_frame_rate.num > 0 ? video->avg_frame_rate : video->r_frame_rate;
            _frameSeconds = rate.num > 0 ? rate.den / (double)rate.num : 1.0 / 60;
            _video = HardwareDecoding.OpenVideo(video, hardwareDecode: true, out _device);

            // Each sound track read on its own (a track the clip lacks, or cannot decode, is left out).
            _mix = mix;
            if (mix == null)
                _single = AudioStream(format, audioTrack) is int one and >= 0 ? new SoundReader(path, one, _origin) : null;
            else
            {
                var tracks = new List<MixTrack>();
                for (int gain = 0; gain < mix.Tracks.Count; gain++)
                    if (AudioStream(format, mix.Tracks[gain]) is int stream and >= 0)
                        tracks.Add(new MixTrack(new SoundReader(path, stream, _origin), gain));
                _tracks = [.. tracks];
            }
            ReadOnly(format, _videoIndex);
            _picture = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            Seek(0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The stream of the <paramref name="track"/>th sound track when it has a decoder, else -1.</summary>
    static int AudioStream(AVFormatContext* format, int track)
    {
        for (int i = 0, seen = 0; i < format->nb_streams; i++)
        {
            var par = format->streams[i]->codecpar;
            if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && seen++ == track)
                return ffmpeg.avcodec_find_decoder(par->codec_id) != null ? i : -1;
        }
        return -1;
    }

    /// <summary>The file reads only <paramref name="index"/>'s packets (the demuxer skips the other tracks).</summary>
    internal static void ReadOnly(AVFormatContext* format, int index)
    {
        for (int i = 0; i < format->nb_streams; i++)
            format->streams[i]->discard = i == index ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
    }

    public bool HasAudio => _single != null || _tracks.Length > 0;

    /// <summary>Length of one picture in seconds (from the frame rate).</summary>
    public double FrameSeconds => _frameSeconds;

    /// <summary>Time of the picture <see cref="NextPicture"/> gave.</summary>
    public double PictureTime { get; private set; }

    /// <summary>Time of the sound piece <see cref="NextSound"/> gave.</summary>
    public double SoundTime { get; private set; }

    /// <summary>The sound piece <see cref="NextSound"/> gave (valid until the next call).</summary>
    public ReadOnlySpan<byte> Sound => _single != null ? _single.Sound : _mixed.AsSpan(0, _mixedLength);

    /// <summary>Starts over at <paramref name="seconds"/> into the clip.</summary>
    public void Seek(double seconds)
    {
        _from = Math.Max(0, seconds);
        long target = (long)((_origin + _from) / ffmpeg.av_q2d(_videoBase));
        if (ffmpeg.avformat_seek_file(_format, _videoIndex, long.MinValue, target, target, 0) < 0)
            ffmpeg.avformat_seek_file(_format, _videoIndex, long.MinValue, target, long.MaxValue, 0);
        ffmpeg.avcodec_flush_buffers(_video);
        _single?.Seek(_from);
        foreach (var track in _tracks)
        {
            track.Reader.Seek(_from);
            track.Used = 0;
        }
        ffmpeg.av_frame_unref(_picture);
        _videoEof = _videoDone = false;
        _mixedLength = 0;
        _mixAt = _from;
    }

    /// <summary>
    /// The next picture at or after the seek point (converted only by <see cref="CopyPicture"/>); false when the clip
    /// has no more.
    /// </summary>
    public bool NextPicture()
    {
        ffmpeg.av_frame_unref(_picture);
        while (!_videoDone)
        {
            if (ReceivePicture())
                return true;
            if (_videoEof)
            {
                _videoDone = true; // flushed, and nothing more came
                break;
            }
            if (!Feed(_format, _packet, _videoIndex, _video))
                _videoEof = true;
        }
        return false;
    }

    /// <summary>The next sound piece at or after the seek point; false when the clip has no more (or no sound).</summary>
    public bool NextSound()
    {
        if (_single == null)
            return NextMixed();
        if (!_single.Next())
            return false;
        SoundTime = _single.Time;
        return true;
    }

    /// <summary>A track of the mix: its reader, its gain's place, and how much of the reader's piece is mixed.</summary>
    sealed class MixTrack(SoundReader reader, int gain)
    {
        public SoundReader Reader { get; } = reader;
        public int Gain { get; } = gain;
        /// <summary>Bytes of the reader's piece already mixed (the piece stays until the reader's next read).</summary>
        public int Used;
        public int Left => Reader.Sound.Length - Used;
    }

    /// <summary>
    /// The next piece of the tracks' sum, each at its gain as it is now: as long as the shortest piece read (they all
    /// start at the seek point, so they stay in step); a track that has ended adds nothing.
    /// </summary>
    bool NextMixed()
    {
        int bytes = int.MaxValue;
        foreach (var track in _tracks)
        {
            if (track.Left == 0 && track.Reader.Next())
                track.Used = 0;
            if (track.Left > 0)
                bytes = Math.Min(bytes, track.Left);
        }
        if (bytes == int.MaxValue)
            return false;
        int samples = bytes / 2;
        if (_sum.Length < samples)
            _sum = new float[samples * 2];
        if (_mixed.Length < bytes)
            _mixed = new byte[bytes * 2];
        var sum = _sum.AsSpan(0, samples);
        sum.Clear();
        var gains = _mix!.Gains;
        foreach (var track in _tracks)
        {
            if (track.Left == 0)
                continue; // it has ended
            float gain = (float)gains[track.Gain];
            if (gain != 0)
            {
                var piece = MemoryMarshal.Cast<byte, short>(track.Reader.Sound.Slice(track.Used, bytes));
                for (int i = 0; i < samples; i++)
                    sum[i] += gain * piece[i];
            }
            track.Used += bytes;
        }
        var output = MemoryMarshal.Cast<byte, short>(_mixed.AsSpan(0, bytes));
        for (int i = 0; i < samples; i++)
            output[i] = (short)Math.Clamp(MathF.Round(sum[i]), short.MinValue, short.MaxValue);
        _mixedLength = bytes;
        SoundTime = _mixAt;
        _mixAt += bytes / (double)(SampleRate * BytesPerFrame);
        return true;
    }

    /// <summary>Hands the decoder the next packet of its track; at the end of the file, tells it so (false).</summary>
    internal static bool Feed(AVFormatContext* format, AVPacket* packet, int index, AVCodecContext* decoder)
    {
        while (true)
        {
            if (ffmpeg.av_read_frame(format, packet) < 0)
            {
                ffmpeg.avcodec_send_packet(decoder, null);
                return false;
            }
            bool ours = packet->stream_index == index;
            if (ours)
                ffmpeg.avcodec_send_packet(decoder, packet);
            ffmpeg.av_packet_unref(packet);
            if (ours)
                return true;
        }
    }

    /// <summary>A decoded picture showing at or after the seek point (the one on screen at that moment counts).</summary>
    bool ReceivePicture()
    {
        while (true)
        {
            int got = ffmpeg.avcodec_receive_frame(_video, _picture);
            if (got == ffmpeg.AVERROR_EOF)
                _videoDone = true;
            if (got < 0)
                return false;
            long pts = _picture->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? _picture->pts : _picture->best_effort_timestamp;
            double t = pts * ffmpeg.av_q2d(_videoBase) - _origin;
            if (t > _from - _frameSeconds / 2)
            {
                PictureTime = t;
                return true;
            }
            ffmpeg.av_frame_unref(_picture);
        }
    }

    /// <summary>Converts the picture <see cref="NextPicture"/> gave into <paramref name="bgra"/> (width × height × 4 bytes).</summary>
    public void CopyPicture(Span<byte> bgra)
    {
        if (bgra.Length < _width * _height * 4)
            throw new ArgumentException("buffer too small", nameof(bgra));
        fixed (byte* dst = bgra)
            _converter.Convert(_picture, _width, _height, SwsFlags.SWS_BILINEAR, dst, _width * 4);
    }

    public void Dispose()
    {
        _single?.Dispose();
        foreach (var track in _tracks)
            track.Reader.Dispose();
        _converter.Dispose();
        AVFrame* f = _picture;
        ffmpeg.av_frame_free(&f);
        _picture = null;
        AVPacket* p = _packet;
        ffmpeg.av_packet_free(&p);
        _packet = null;
        AVCodecContext* c = _video;
        if (c != null)
            ffmpeg.avcodec_free_context(&c);
        _video = null;
        HardwareDecoding.Return(_device);
        _device = null;
        AVFormatContext* format = _format;
        if (format != null)
            ffmpeg.avformat_close_input(&format);
        _format = null;
    }
}
