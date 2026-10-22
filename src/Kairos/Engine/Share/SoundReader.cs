using FFmpeg.AutoGen;

namespace Kairos.Engine.Share;

/// <summary>Sound tracks a player plays mixed, each at a gain that may change while it plays (set on any thread).</summary>
public sealed class TrackMix(IReadOnlyList<int> tracks, IReadOnlyList<double> gains)
{
    double[] _gains = [.. gains];

    /// <summary>The clip's sound tracks, 0-based (0: the first).</summary>
    public IReadOnlyList<int> Tracks { get; } = tracks;

    /// <summary>Each track's gain, in the order of <see cref="Tracks"/>; a new value is heard from the next piece on.</summary>
    public IReadOnlyList<double> Gains
    {
        get => Volatile.Read(ref _gains);
        set => Volatile.Write(ref _gains, [.. value]);
    }
}

/// <summary>
/// One sound track of a clip as 16-bit stereo at 48 kHz from a point on, trimmed (or led by silence) to start exactly
/// there. It reads its own open file, so it keeps ahead of the pictures however the file stores them. Not thread-safe.
/// </summary>
sealed unsafe class SoundReader : IDisposable
{
    AVFormatContext* _format;
    AVCodecContext* _decoder;
    AVFrame* _samples;
    AVPacket* _packet;
    SwrContext* _swr;
    readonly int _index;
    readonly AVRational _base;
    /// <summary>Clip time 0 (the video's first timestamp), in seconds.</summary>
    readonly double _origin;
    double _from;
    /// <summary>Time of the next sound sample once sound has started (pieces follow on without gaps).</summary>
    double _soundAt = double.NaN;
    byte[] _sound = [];
    int _soundLength;
    bool _eof, _done;

    /// <param name="index">The track's stream in the file (it has a decoder).</param>
    public SoundReader(string path, int index, double origin)
    {
        _index = index;
        _origin = origin;
        AVFormatContext* format = null;
        ClipTranscoder.Check(ffmpeg.avformat_open_input(&format, path, null, null), "Opening the clip");
        _format = format;
        try
        {
            ClipTranscoder.Check(ffmpeg.avformat_find_stream_info(format, null), "Reading the clip");
            AVStream* stream = format->streams[index];
            _base = stream->time_base;
            _decoder = SoundCodecs.OpenDecoder(stream);
            ClipPlayback.ReadOnly(format, index);
            _samples = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Time of the piece <see cref="Next"/> gave.</summary>
    public double Time { get; private set; }

    /// <summary>The piece <see cref="Next"/> gave (valid until the next call).</summary>
    public ReadOnlySpan<byte> Sound => _sound.AsSpan(0, _soundLength);

    public void Seek(double from)
    {
        _from = from;
        long target = (long)((_origin + _from) / ffmpeg.av_q2d(_base));
        if (ffmpeg.avformat_seek_file(_format, _index, long.MinValue, target, target, 0) < 0)
            ffmpeg.avformat_seek_file(_format, _index, long.MinValue, target, long.MaxValue, 0);
        ffmpeg.avcodec_flush_buffers(_decoder);
        SwrContext* swr = _swr;
        ffmpeg.swr_free(&swr);
        _swr = null;
        _soundAt = double.NaN;
        _soundLength = 0;
        _eof = _done = false;
    }

    /// <summary>The next piece at or after the seek point; false when the track has no more.</summary>
    public bool Next()
    {
        while (!_done)
        {
            if (Receive())
                return true;
            if (_eof)
            {
                _done = true;
                break;
            }
            if (!ClipPlayback.Feed(_format, _packet, _index, _decoder))
                _eof = true;
        }
        return false;
    }

    bool Receive()
    {
        while (true)
        {
            int got = ffmpeg.avcodec_receive_frame(_decoder, _samples);
            if (got == ffmpeg.AVERROR_EOF)
                _done = true;
            if (got < 0)
                return false;
            try
            {
                if (Convert(_samples))
                    return true;
            }
            finally
            {
                ffmpeg.av_frame_unref(_samples);
            }
        }
    }

    bool Convert(AVFrame* frame)
    {
        if (_swr == null)
        {
            AVChannelLayout stereo;
            ffmpeg.av_channel_layout_default(&stereo, 2);
            SwrContext* swr = null;
            ClipTranscoder.Check(ffmpeg.swr_alloc_set_opts2(&swr, &stereo, AVSampleFormat.AV_SAMPLE_FMT_S16, ClipPlayback.SampleRate,
                &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null), "Preparing the sound");
            _swr = swr;
            ClipTranscoder.Check(ffmpeg.swr_init(_swr), "Preparing the sound");
        }
        const int bytesPerFrame = ClipPlayback.BytesPerFrame;
        int room = ffmpeg.swr_get_out_samples(_swr, frame->nb_samples);
        if (_sound.Length < room * bytesPerFrame)
            _sound = new byte[room * bytesPerFrame * 2];
        int samples;
        fixed (byte* outData = _sound)
        {
            byte* outPtr = outData;
            samples = ffmpeg.swr_convert(_swr, &outPtr, room, frame->extended_data, frame->nb_samples);
        }
        if (samples <= 0)
            return false;

        double start = _soundAt;
        if (double.IsNaN(start))
        {
            long pts = frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? frame->pts : frame->best_effort_timestamp;
            start = pts * ffmpeg.av_q2d(_base) - _origin;
        }
        double end = start + samples / (double)ClipPlayback.SampleRate;
        if (end <= _from)
        {
            // Before the seek point: dropped, but the clock follows it.
            _soundAt = end;
            return false;
        }
        _soundLength = samples * bytesPerFrame;
        if (start < _from)
        {
            // Starts before the seek point: cut its head off so the sound begins exactly there.
            int skip = Math.Min(samples, (int)Math.Round((_from - start) * ClipPlayback.SampleRate)) * bytesPerFrame;
            Buffer.BlockCopy(_sound, skip, _sound, 0, _soundLength - skip);
            _soundLength -= skip;
            start = _from;
        }
        else if (double.IsNaN(_soundAt) && start > _from + 0.0005)
        {
            // The first sound comes a little after the seek point: silence before it keeps it in step with the pictures.
            int pad = (int)Math.Round((start - _from) * ClipPlayback.SampleRate) * bytesPerFrame;
            var padded = new byte[Math.Max(_sound.Length, pad + _soundLength)];
            Buffer.BlockCopy(_sound, 0, padded, pad, _soundLength);
            _sound = padded;
            _soundLength += pad;
            start = _from;
        }
        _soundAt = end;
        Time = start;
        return _soundLength > 0;
    }

    public void Dispose()
    {
        SwrContext* swr = _swr;
        ffmpeg.swr_free(&swr);
        _swr = null;
        AVFrame* f = _samples;
        ffmpeg.av_frame_free(&f);
        _samples = null;
        AVPacket* p = _packet;
        ffmpeg.av_packet_free(&p);
        _packet = null;
        AVCodecContext* c = _decoder;
        if (c != null)
            ffmpeg.avcodec_free_context(&c);
        _decoder = null;
        AVFormatContext* format = _format;
        if (format != null)
            ffmpeg.avformat_close_input(&format);
        _format = null;
    }
}
