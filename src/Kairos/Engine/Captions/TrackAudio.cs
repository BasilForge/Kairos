using FFmpeg.AutoGen;
using Kairos.Engine.Share;

namespace Kairos.Engine.Captions;

/// <summary>One sound track as Whisper hears it, and where it starts in the clip (source seconds).</summary>
public sealed record TrackSound(float[] Samples, double Start);

/// <summary>Reads audio streams of a clip as 16 kHz mono with the FFmpeg next to Kairos (load it first).</summary>
public static class TrackAudio
{
    public const int Rate = 16000;

    /// <summary>Several streams heard together (Discord and the mic): their sum, each in its place in the clip.</summary>
    public static TrackSound Read(string clip, IReadOnlyList<int> streams, IProgress<double>? progress, CancellationToken ct)
    {
        if (streams.Count == 1)
            return Read(clip, streams[0], progress, ct);
        TrackSound? sum = null;
        for (int t = 0; t < streams.Count; t++)
        {
            var sound = Read(clip, streams[t], progress == null ? null : new ScaledProgress(progress, t / (double)streams.Count, (t + 1) / (double)streams.Count), ct);
            sum = sum == null ? sound : Add(sum, sound, last: t == streams.Count - 1);
        }
        return sum!;
    }

    /// <summary>
    /// Two tracks as one, each in its place in the clip (the last add keeps the sum within -1..1): into the first's own
    /// samples when the second fits in them (Kairos's tracks start and end together), so an hour is not held three times.
    /// </summary>
    static TrackSound Add(TrackSound a, TrackSound b, bool last)
    {
        double start = Math.Min(a.Start, b.Start);
        int atA = (int)Math.Round((a.Start - start) * Rate), atB = (int)Math.Round((b.Start - start) * Rate);
        float[] sum = a.Samples;
        if (atA != 0 || atB + b.Samples.Length > a.Samples.Length)
        {
            sum = new float[Math.Max(atA + a.Samples.Length, atB + b.Samples.Length)];
            a.Samples.CopyTo(sum, atA);
        }
        for (int i = 0; i < b.Samples.Length; i++)
            sum[atB + i] += b.Samples[i];
        if (last)
            for (int i = 0; i < sum.Length; i++)
                sum[i] = Math.Clamp(sum[i], -1f, 1f);
        return new TrackSound(sum, start);
    }

    /// <param name="stream">Which audio stream (0 = the first, the full mix in Kairos's clips).</param>
    public static TrackSound Read(string clip, int stream, IProgress<double>? progress, CancellationToken ct)
    {
        if (!File.Exists(clip))
            throw new ShareException($"The clip was not found: {clip}");
        using var reader = new Reader(clip, stream);
        return reader.Read(progress, ct);
    }

    /// <summary>Owns every native object it creates.</summary>
    sealed unsafe class Reader : IDisposable
    {
        AVFormatContext* _format;
        AVCodecContext* _decoder;
        SwrContext* _swr;
        AVPacket* _packet;
        AVFrame* _frame;
        readonly AVStream* _audio;
        /// <summary>Source time 0: where the picture starts (as the players and the editor count it).</summary>
        readonly double _origin;
        readonly double _duration;
        double _start = double.NaN;
        float[] _chunk = new float[Rate];
        readonly List<float> _samples;

        public Reader(string clip, int stream)
        {
            ClipTranscoder.RequireLoaded();
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            try
            {
                AVFormatContext* format = null;
                ClipTranscoder.Check(ffmpeg.avformat_open_input(&format, clip, null, null), "Opening the clip");
                _format = format;
                ClipTranscoder.Check(ffmpeg.avformat_find_stream_info(format, null), "Reading the clip");
                AVStream* video = null;
                int seen = 0;
                for (int i = 0; i < format->nb_streams; i++)
                {
                    AVStream* s = format->streams[i];
                    if (s->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && seen++ == stream)
                        _audio = s;
                    else if (s->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && video == null &&
                             (s->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                        video = s;
                    // Only the one sound track is read from the file.
                    if (s != _audio)
                        s->discard = AVDiscard.AVDISCARD_ALL;
                }
                if (_audio == null)
                    throw new ShareException("The clip has no such sound track.");

                _decoder = SoundCodecs.OpenDecoder(_audio);

                AVStream* clock = video != null ? video : _audio;
                _origin = (clock->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : clock->start_time) * ffmpeg.av_q2d(clock->time_base);
                _duration = format->duration > 0 ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                _samples = new List<float>(_duration > 0 ? (int)Math.Min(int.MaxValue / 2, _duration * Rate + Rate) : Rate * 60);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public TrackSound Read(IProgress<double>? progress, CancellationToken ct)
        {
            int percent = -1;
            while (ffmpeg.av_read_frame(_format, _packet) >= 0)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (_packet->stream_index != _audio->index)
                        continue;
                    if (ffmpeg.avcodec_send_packet(_decoder, _packet) >= 0)
                        Drain();
                    if (_duration > 0 && progress != null && _packet->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        int now = (int)Math.Clamp((_packet->pts * ffmpeg.av_q2d(_audio->time_base) - _origin) / _duration * 100, 0, 99);
                        if (now != percent)
                            progress.Report((percent = now) / 100.0);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }
            ffmpeg.avcodec_send_packet(_decoder, null);
            Drain();
            if (_swr != null)
                Convert(null); // what the resampler still holds
            progress?.Report(1);
            return new TrackSound([.. _samples], double.IsNaN(_start) ? 0 : _start);
        }

        void Drain()
        {
            while (ffmpeg.avcodec_receive_frame(_decoder, _frame) >= 0)
            {
                Convert(_frame);
                ffmpeg.av_frame_unref(_frame);
            }
        }

        /// <param name="frame">null: flush the resampler.</param>
        void Convert(AVFrame* frame)
        {
            if (_swr == null)
            {
                AVChannelLayout mono;
                ffmpeg.av_channel_layout_default(&mono, 1);
                SwrContext* swr = null;
                ClipTranscoder.Check(ffmpeg.swr_alloc_set_opts2(&swr, &mono, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate,
                    &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null), "Preparing the sound");
                _swr = swr;
                ClipTranscoder.Check(ffmpeg.swr_init(_swr), "Preparing the sound");
            }
            if (frame != null && double.IsNaN(_start))
            {
                long pts = frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? frame->pts : frame->best_effort_timestamp;
                _start = pts == ffmpeg.AV_NOPTS_VALUE ? 0 : pts * ffmpeg.av_q2d(_audio->time_base) - _origin;
            }
            int room = ffmpeg.swr_get_out_samples(_swr, frame == null ? 0 : frame->nb_samples);
            if (room <= 0)
                return;
            if (_chunk.Length < room)
                _chunk = new float[room * 2];
            int got;
            fixed (float* outData = _chunk)
            {
                byte* outPtr = (byte*)outData;
                got = frame == null
                    ? ffmpeg.swr_convert(_swr, &outPtr, room, null, 0)
                    : ffmpeg.swr_convert(_swr, &outPtr, room, frame->extended_data, frame->nb_samples);
            }
            for (int i = 0; i < got; i++)
                _samples.Add(_chunk[i]);
        }

        public void Dispose()
        {
            SwrContext* swr = _swr;
            ffmpeg.swr_free(&swr);
            _swr = null;
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
            AVCodecContext* decoder = _decoder;
            ffmpeg.avcodec_free_context(&decoder);
            _decoder = null;
            AVFormatContext* format = _format;
            if (format != null)
                ffmpeg.avformat_close_input(&format);
            _format = null;
        }
    }
}
