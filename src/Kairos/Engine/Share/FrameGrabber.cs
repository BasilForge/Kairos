using FFmpeg.AutoGen;

namespace Kairos.Engine.Share;

/// <summary>A picture from the clip as 32-bit BGRA rows (top-down, no padding).</summary>
public sealed record GrabbedFrame(int Width, int Height, byte[] Bgra);

/// <summary>
/// Reads single frames of a clip for the trim bar and the preview. Keeps the file and decoder open between calls
/// (one grabber per open share panel). Uses the graphics card like the converter (AV1 needs it), else the CPU.
/// Not thread-safe: one caller at a time.
/// </summary>
public sealed unsafe class FrameGrabber : IDisposable
{
    AVFormatContext* _format;
    AVCodecContext* _decoder;
    AVBufferRef* _device;
    AVFrame* _frame;
    AVPacket* _packet;
    readonly BgraConverter _converter = new();
    readonly int _video;
    readonly AVRational _timeBase;
    readonly long _startPts;

    /// <summary>Pictures decoded so far (tests: a step forward must not start over from the key frame).</summary>
    internal long FramesDecoded { get; private set; }

    /// <summary>The time of the picture still in _frame (the last one given), or none.</summary>
    long _at = long.MinValue;
    /// <summary>The decoder was told the clip ended: it must be seeked before it decodes again.</summary>
    bool _drained;

    public FrameGrabber(string path)
    {
        if (!File.Exists(path))
            throw new ShareException($"The clip was not found: {path}");
        ClipTranscoder.RequireLoaded();
        AVFormatContext* format = null;
        ClipTranscoder.Check(ffmpeg.avformat_open_input(&format, path, null, null), "Opening the clip");
        _format = format;
        try
        {
            ClipTranscoder.Check(ffmpeg.avformat_find_stream_info(format, null), "Reading the clip");
            _video = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (_video < 0)
                throw new ShareException("The clip has no video.");
            AVStream* stream = format->streams[_video];
            _timeBase = stream->time_base;
            _startPts = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
            _decoder = HardwareDecoding.OpenVideo(stream, hardwareDecode: true, out _device);
            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// The frame at <paramref name="seconds"/>, <paramref name="width"/> pixels wide (height follows the clip's shape,
    /// even). <paramref name="exact"/> false returns the key frame at or before that time, which needs no decoding past
    /// it (fast enough for the trim bar while dragging). Null if nothing could be decoded there.
    /// </summary>
    public GrabbedFrame? Grab(double seconds, int width, bool exact)
    {
        long target = _startPts + (long)(Math.Max(0, seconds) / ffmpeg.av_q2d(_timeBase));
        // The same moment again (an edit while the playhead stays): the picture is still here, only converted again.
        if (exact && _at == target)
            return Convert(width, SwsFlags.SWS_BILINEAR);
        // A little ahead of the last picture (stepping, a short drag): decoding on from it costs less than going back to
        // the key frame and decoding up to here again, and gives the very same picture. Anything else seeks as before.
        long ahead = (long)(AheadSeconds / ffmpeg.av_q2d(_timeBase));
        bool onward = exact && !_drained && _at != long.MinValue && target > _at && target - _at <= ahead;
        if (!onward)
        {
            if (ffmpeg.avformat_seek_file(_format, _video, long.MinValue, target, target, 0) < 0)
                ffmpeg.avformat_seek_file(_format, _video, long.MinValue, target, long.MaxValue, 0);
            ffmpeg.avcodec_flush_buffers(_decoder);
            _drained = false;
        }
        ffmpeg.av_frame_unref(_frame);
        _at = long.MinValue;

        AVFrame* best = null;
        bool eof = false;
        for (int packets = 0; packets < 2000 && best == null; packets++)
        {
            int read = ffmpeg.av_read_frame(_format, _packet);
            if (read < 0)
            {
                eof = true;
                _drained = true;
                ffmpeg.avcodec_send_packet(_decoder, null);
            }
            else if (_packet->stream_index != _video)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }
            else
            {
                ffmpeg.avcodec_send_packet(_decoder, _packet);
                ffmpeg.av_packet_unref(_packet);
            }

            while (true)
            {
                int got = ffmpeg.avcodec_receive_frame(_decoder, _frame);
                if (got < 0)
                    break;
                FramesDecoded++;
                long pts = _frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? _frame->pts : _frame->best_effort_timestamp;
                // Fast: the first picture is the key frame. Exact: the first picture at or past the time; near the end of
                // the clip the last picture there is.
                if (!exact || pts >= target || eof)
                {
                    best = _frame;
                    break;
                }
                ffmpeg.av_frame_unref(_frame);
            }
            if (eof && best == null)
                break;
        }
        if (best == null)
            return null;
        // The picture stays decoded in _frame for the next grab (the same moment, or a step on from it).
        _at = exact ? (best->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? best->pts : best->best_effort_timestamp) : long.MinValue;
        return Convert(width, exact ? SwsFlags.SWS_BILINEAR : SwsFlags.SWS_AREA); // area: clean downscaling
    }

    /// <summary>How far ahead of the last picture a grab decodes on instead of seeking (about half a key frame interval).</summary>
    const double AheadSeconds = 0.5;

    /// <summary>The picture kept in <c>_frame</c>, <paramref name="width"/> wide.</summary>
    GrabbedFrame Convert(int width, SwsFlags flags)
    {
        int outW = Math.Max(2, width & ~1);
        int outH = Math.Max(2, (int)Math.Round((double)outW * _frame->height / _frame->width) & ~1);
        var bgra = new byte[outW * outH * 4];
        fixed (byte* dst = bgra)
            _converter.Convert(_frame, outW, outH, flags, dst, outW * 4);
        // The picture has no alpha; make it opaque for display.
        for (int i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
        return new GrabbedFrame(outW, outH, bgra);
    }

    public void Dispose()
    {
        _converter.Dispose();
        AVFrame* f = _frame;
        ffmpeg.av_frame_free(&f);
        _frame = null;
        AVPacket* p = _packet;
        ffmpeg.av_packet_free(&p);
        _packet = null;
        AVCodecContext* d = _decoder;
        if (d != null)
            ffmpeg.avcodec_free_context(&d);
        _decoder = null;
        HardwareDecoding.Return(_device);
        _device = null;
        AVFormatContext* format = _format;
        if (format != null)
            ffmpeg.avformat_close_input(&format);
        _format = null;
    }
}
