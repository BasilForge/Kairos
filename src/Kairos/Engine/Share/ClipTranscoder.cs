using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Kairos.Engine.Share;

/// <summary>A clip could not be prepared; the message says why (shown to the user as detail).</summary>
public class ShareException(string message) : Exception(message);

/// <summary>The H.264 encoder itself failed: the next encoder in the list gets a turn.</summary>
sealed class EncoderFailedException(string message) : ShareException(message);

/// <summary>The graphics card could not decode this clip: the pass runs again with a CPU decoder.</summary>
sealed class HardwareDecodeFailedException(string message) : ShareException(message);

/// <param name="Bytes">Size of the clip's file.</param>
public sealed record MediaInfo(double Duration, int Width, int Height, double Fps, string VideoCodec, string? AudioCodec,
    long AudioBitrate, int AudioStreams, long Bytes = 0);

public sealed record ShareResult(string Path, long Bytes, string Encoder);

/// <summary>
/// Converts a clip to H.264 + AAC MP4 with the FFmpeg that OBS ships (already next to Kairos), so sharing adds
/// nothing to the download. Video: hardware decode (needed for AV1), crop/scale/fps on the CPU, hardware H.264
/// encode when there is one. Audio: the first track (the full mix) is copied as it is.
/// </summary>
public static unsafe class ClipTranscoder
{
    /// <summary>Hardware encoders first (seconds, no CPU load), then Media Foundation (Intel and others), then x264.</summary>
    public static readonly string[] DefaultEncoders = ["h264_nvenc", "h264_amf", "h264_mf", "libx264"];

    const int MaxSizeRetries = 3;
    static readonly AVRational Microseconds = new() { num = 1, den = ffmpeg.AV_TIME_BASE };
    static readonly object LoadLock = new();
    static bool _loaded;

    /// <summary>Binds the FFmpeg DLLs in <paramref name="libraryDir"/> (avcodec-62.dll and friends). Safe to call again.</summary>
    public static void Load(string libraryDir)
    {
        lock (LoadLock)
        {
            if (_loaded)
                return;
            if (!File.Exists(Path.Combine(libraryDir, CaptureBackend.FfmpegMainLibrary)))
                throw new ShareException($"FFmpeg was not found in {libraryDir}");
            ffmpeg.RootPath = libraryDir;
            DynamicallyLoadedBindings.FunctionResolver = new FolderResolver(libraryDir);
            DynamicallyLoadedBindings.Initialize();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            _loaded = true;
        }
    }

    public static MediaInfo Probe(string path)
    {
        RequireLoaded();
        using var input = new Input(path);
        AVStream* v = input.Video;
        AVFormatContext* fmt = input.Format;
        double duration = fmt->duration > 0
            ? fmt->duration / (double)ffmpeg.AV_TIME_BASE
            : v->duration * ffmpeg.av_q2d(v->time_base);
        AVStream* a = input.Audio;
        return new MediaInfo(duration, v->codecpar->width, v->codecpar->height, Fps(v),
            ffmpeg.avcodec_get_name(v->codecpar->codec_id),
            a == null ? null : ffmpeg.avcodec_get_name(a->codecpar->codec_id),
            a == null ? 0 : a->codecpar->bit_rate, input.AudioStreams, new FileInfo(path).Length);
    }

    /// <summary>NVENC runs on the decoder's D3D11 device (tests turn it off to compare the files).</summary>
    internal static bool UseDecoderDevice = true;

    /// <summary>
    /// Writes <paramref name="target"/> (via a .part file, so a half file never appears). When the result is over
    /// <paramref name="maxBytes"/>, the video bitrate is lowered by the measured overshoot and the pass runs again.
    /// </summary>
    public static ShareResult Transcode(string source, string target, SharePlan plan, long maxBytes, IProgress<double>? progress,
        CancellationToken ct, string[]? encoders = null, bool gpuDecode = true)
    {
        RequireLoaded();
        // Unique per run: a cancelled run may still be closing its own file when the next one starts.
        string temp = $"{target}.{Guid.NewGuid():N}.part";
        var failures = new List<string>();
        bool hardwareDecode = gpuDecode;
        try
        {
            foreach (string encoder in encoders ?? DefaultEncoders)
            {
                if (ffmpeg.avcodec_find_encoder_by_name(encoder) == null)
                    continue;
                long bitrate = plan.VideoBitrate;
                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        PassStats stats;
                        try
                        {
                            stats = Pass.Run(source, temp, plan with { VideoBitrate = bitrate }, encoder, hardwareDecode, progress, ct);
                        }
                        catch (HardwareDecodeFailedException) when (hardwareDecode)
                        {
                            hardwareDecode = false;
                            attempt--;
                            continue;
                        }
                        long size = new FileInfo(temp).Length;
                        if (size <= maxBytes)
                        {
                            string final = Place(temp, target);
                            progress?.Report(1);
                            return new ShareResult(final, size, encoder);
                        }
                        long videoBudget = maxBytes - (size - stats.VideoBytes);
                        double scale = 0.95 * videoBudget / Math.Max(1, stats.VideoBytes);
                        bitrate = (long)(bitrate * Math.Min(scale, 0.9));
                        if (attempt >= MaxSizeRetries || videoBudget <= 0 || bitrate < 50_000)
                            throw new ShareException($"The clip does not fit in {maxBytes / 1_000_000.0:0.#} MB, even at a low quality. Choose a shorter length.");
                    }
                }
                catch (EncoderFailedException e)
                {
                    failures.Add($"{encoder}: {e.Message}");
                }
            }
            throw new ShareException("No H.264 encoder could be started. " + string.Join("; ", failures));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Loads each DLL by full path so Windows also finds its dependencies (libx264, zlib...) in that folder,
    /// whichever folder the process started from.
    /// </summary>
    sealed class FolderResolver(string dir) : FunctionResolverBase
    {
        protected override string GetNativeLibraryName(string libraryName, int version) => CaptureBackend.FfmpegLibraryName(libraryName, version);

        protected override IntPtr LoadNativeLibrary(string libraryName) =>
            NativeLibrary.TryLoad(Path.Combine(dir, libraryName), out IntPtr handle) ? handle : IntPtr.Zero;

        protected override IntPtr FindFunctionPointer(IntPtr nativeLibraryHandle, string functionName) =>
            NativeLibrary.TryGetExport(nativeLibraryHandle, functionName, out IntPtr address) ? address : IntPtr.Zero;
    }

    /// <summary>Replaces an older copy; if that one is still open (an upload in progress), takes "name (2).mp4".</summary>
    internal static string Place(string temp, string target)
    {
        string stem = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target));
        string ext = Path.GetExtension(target);
        string candidate = target;
        for (int n = 2; ; n++)
        {
            try
            {
                File.Move(temp, candidate, overwrite: true);
                return candidate;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && n < 50)
            {
                candidate = $"{stem} ({n}){ext}";
            }
        }
    }

    /// <summary>Kairos's FFmpeg: beside Kairos.exe (KAIROS_FFMPEG points a development build at another copy).</summary>
    public static string DefaultFolder =>
        Environment.GetEnvironmentVariable("KAIROS_FFMPEG") is { Length: > 0 } folder ? folder : CaptureBackend.FfmpegFolder;

    /// <summary>Loads FFmpeg from <see cref="DefaultFolder"/> on first use (a screenshot can be the first thing edited).</summary>
    internal static void RequireLoaded()
    {
        if (!_loaded)
            Load(DefaultFolder);
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static double Fps(AVStream* v)
    {
        double fps = ffmpeg.av_q2d(v->avg_frame_rate);
        return fps > 0 && double.IsFinite(fps) ? fps : ffmpeg.av_q2d(v->r_frame_rate);
    }

    internal static int Check(int result, string what)
    {
        if (result < 0)
            throw new ShareException($"{what}: {ErrorText(result)}");
        return result;
    }

    internal static string ErrorText(int code)
    {
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(code, buffer, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? code.ToString();
    }

    /// <summary>The source file: its video stream and the first audio stream (track 1 = the full mix).</summary>
    internal sealed class Input : IDisposable
    {
        AVFormatContext* _format;
        public AVFormatContext* Format => _format;
        public AVStream* Video { get; }
        public AVStream* Audio { get; }
        public int AudioStreams { get; }

        public Input(string path)
        {
            if (!File.Exists(path))
                throw new ShareException($"The clip was not found: {path}");
            AVFormatContext* format = null;
            Check(ffmpeg.avformat_open_input(&format, path, null, null), "Opening the clip");
            _format = format;
            try
            {
                Check(ffmpeg.avformat_find_stream_info(format, null), "Reading the clip");
                for (int i = 0; i < format->nb_streams; i++)
                {
                    AVStream* s = format->streams[i];
                    if (s->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && Video == null && (s->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                        Video = s;
                    else if (s->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        if (Audio == null)
                            Audio = s;
                        AudioStreams++;
                    }
                }
                if (Video == null)
                    throw new ShareException("The clip has no video.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            AVFormatContext* format = _format;
            if (format != null)
                ffmpeg.avformat_close_input(&format);
            _format = null;
        }
    }

    sealed record PassStats(long VideoBytes, long AudioBytes);

    /// <summary>One conversion attempt with one encoder at one bitrate. Owns every native object it creates.</summary>
    sealed class Pass : IDisposable
    {
        readonly SharePlan _plan;
        readonly Input _input;
        readonly AVRational _rate;
        AVCodecContext* _decoder;
        AVBufferRef* _device;
        AVCodecContext* _encoder;
        AVFilterGraph* _graph;
        AVFilterContext* _source;
        AVFilterContext* _sink;
        AVFormatContext* _output;
        AVStream* _outVideo;
        AVStream* _outAudio;
        AVFrame* _frame;
        AVFrame* _transfer;
        AVFrame* _filtered;
        AVPacket* _packet;
        long _videoStart, _videoEnd, _audioStart, _audioEnd;
        long _videoBytes, _audioBytes, _framesOut, _framesDecoded;
        int _lastPercent = -1;

        Pass(string source, SharePlan plan)
        {
            _plan = plan;
            _input = new Input(source);
            _rate = ffmpeg.av_d2q(plan.Fps, 100_000);
        }

        public static PassStats Run(string source, string temp, SharePlan plan, string encoder, bool hardwareDecode,
            IProgress<double>? progress, CancellationToken ct)
        {
            using var pass = new Pass(source, plan);
            pass.OpenDecoder(hardwareDecode);
            pass.OpenEncoder(encoder);
            pass.OpenOutput(temp);
            pass.Convert(progress, ct);
            return new PassStats(pass._videoBytes, pass._audioBytes);
        }

        void OpenDecoder(bool hardwareDecode)
        {
            AVStream* v = _input.Video;
            _decoder = HardwareDecoding.OpenVideo(v, hardwareDecode, out _device);

            _frame = ffmpeg.av_frame_alloc();
            _transfer = ffmpeg.av_frame_alloc();
            _filtered = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();

            // One time origin for both streams keeps them in sync after the cut.
            long origin = _input.Format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : _input.Format->start_time;
            long startUs = origin + (long)(_plan.Start * 1_000_000);
            long endUs = startUs + (long)(_plan.Duration * 1_000_000);
            _videoStart = ffmpeg.av_rescale_q(startUs, Microseconds, v->time_base);
            _videoEnd = ffmpeg.av_rescale_q(endUs, Microseconds, v->time_base);
            if (_input.Audio != null)
            {
                _audioStart = ffmpeg.av_rescale_q(startUs, Microseconds, _input.Audio->time_base);
                _audioEnd = ffmpeg.av_rescale_q(endUs, Microseconds, _input.Audio->time_base);
            }
            if (_plan.Start > 0)
            {
                // Lands on the keyframe at or before the start; frames before it are decoded and dropped.
                Check(ffmpeg.avformat_seek_file(_input.Format, v->index, long.MinValue, _videoStart, _videoStart, 0), "Seeking in the clip");
            }
        }

        /// <summary>A decode error before the first picture on the graphics card means: try the CPU decoder.</summary>
        int CheckDecode(int result)
        {
            if (result < 0 && _device != null && _framesDecoded == 0)
                throw new HardwareDecodeFailedException(ErrorText(result));
            return Check(result, "Decoding the video");
        }

        void OpenEncoder(string name)
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(name);
            Exception? last = null;
            // NVENC on the decoder's own D3D11 device: no second (CUDA) context for this conversion (about a quarter of a
            // gigabyte less), the same encoder and settings, the same file. Without it (or if it will not open) as before.
            bool[] onDevice = UseDecoderDevice && _device != null && name.EndsWith("_nvenc", StringComparison.Ordinal) ? [true, false] : [false];
            // Tuned options first; plain defaults if an older GPU or driver rejects them.
            foreach (bool shared in onDevice)
            foreach (bool tuned in new[] { true, false })
            {
                AVCodecContext* enc = ffmpeg.avcodec_alloc_context3(codec);
                if (shared)
                    enc->hw_device_ctx = ffmpeg.av_buffer_ref(_device);
                enc->width = _plan.Width;
                enc->height = _plan.Height;
                enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                enc->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
                enc->framerate = _rate;
                enc->time_base = new AVRational { num = _rate.den, den = _rate.num };
                enc->bit_rate = _plan.VideoBitrate;
                enc->rc_max_rate = _plan.VideoBitrate * 3 / 2;
                enc->rc_buffer_size = (int)Math.Min(int.MaxValue, _plan.VideoBitrate * 2);
                enc->gop_size = Math.Max(1, (int)Math.Round(_plan.Fps * 2));
                enc->color_range = _decoder->color_range;
                enc->colorspace = _decoder->colorspace;
                enc->color_primaries = _decoder->color_primaries;
                enc->color_trc = _decoder->color_trc;
                enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER; // MP4 keeps SPS/PPS in the header

                AVDictionary* options = null;
                if (tuned)
                {
                    foreach (var (key, value) in EncoderOptions(name))
                        ffmpeg.av_dict_set(&options, key, value, 0);
                }
                int result = ffmpeg.avcodec_open2(enc, codec, &options);
                ffmpeg.av_dict_free(&options);
                if (result >= 0)
                {
                    _encoder = enc;
                    return;
                }
                last = new EncoderFailedException(ErrorText(result));
                ffmpeg.avcodec_free_context(&enc);
            }
            throw last!;
        }

        static (string, string)[] EncoderOptions(string name) => name switch
        {
            "h264_nvenc" => [("preset", "p6"), ("tune", "hq"), ("rc", "vbr"), ("multipass", "qres"), ("spatial-aq", "1"),
                ("rc-lookahead", "20"), ("profile", "high")],
            "h264_amf" => [("usage", "transcoding"), ("quality", "quality"), ("rc", "vbr_peak"), ("profile", "high")],
            "h264_mf" => [("hw_encoding", "1"), ("rate_control", "pc_vbr")],
            "libx264" => [("preset", "faster"), ("profile", "high")],
            _ => [],
        };

        void OpenOutput(string path)
        {
            AVFormatContext* output = null;
            Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", path), "Creating the MP4");
            _output = output;

            _outVideo = ffmpeg.avformat_new_stream(output, null);
            Check(ffmpeg.avcodec_parameters_from_context(_outVideo->codecpar, _encoder), "Video track");
            _outVideo->time_base = _encoder->time_base;
            _outVideo->avg_frame_rate = _rate;

            if (_input.Audio != null)
            {
                _outAudio = ffmpeg.avformat_new_stream(output, null);
                Check(ffmpeg.avcodec_parameters_copy(_outAudio->codecpar, _input.Audio->codecpar), "Audio track");
                _outAudio->codecpar->codec_tag = 0;
                _outAudio->time_base = _input.Audio->time_base;
            }

            Check(ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE), "Writing the MP4");
            AVDictionary* options = null;
            // The index goes to the front so sites can start playing (and checking) the upload right away.
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            int result = ffmpeg.avformat_write_header(output, &options);
            ffmpeg.av_dict_free(&options);
            Check(result, "Writing the MP4 header");
        }

        void Convert(IProgress<double>? progress, CancellationToken ct)
        {
            int videoIndex = _input.Video->index;
            int audioIndex = _input.Audio == null ? -1 : _input.Audio->index;
            bool videoDone = false, audioDone = audioIndex < 0;

            while (!(videoDone && audioDone))
            {
                ct.ThrowIfCancellationRequested();
                int read = ffmpeg.av_read_frame(_input.Format, _packet);
                if (read == ffmpeg.AVERROR_EOF)
                    break;
                Check(read, "Reading the clip");
                try
                {
                    if (_packet->stream_index == videoIndex && !videoDone)
                    {
                        CheckDecode(ffmpeg.avcodec_send_packet(_decoder, _packet));
                        videoDone = ReceiveDecoded(progress, ct);
                    }
                    else if (_packet->stream_index == audioIndex && !audioDone)
                        audioDone = CopyAudio();
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            if (!videoDone)
            {
                ffmpeg.avcodec_send_packet(_decoder, null);
                ReceiveDecoded(progress, ct);
            }
            if (_graph != null)
            {
                Check(ffmpeg.av_buffersrc_add_frame(_source, null), "Finishing the picture");
                DrainFilter(progress, ct);
            }
            Encode(null);
            Check(ffmpeg.av_write_trailer(_output), "Finishing the MP4");
        }

        /// <summary>Feeds every frame the decoder has ready; true once the cut's end is reached.</summary>
        bool ReceiveDecoded(IProgress<double>? progress, CancellationToken ct)
        {
            while (true)
            {
                int result = ffmpeg.avcodec_receive_frame(_decoder, _frame);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return false;
                CheckDecode(result);
                _framesDecoded++;

                long pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE)
                    pts = _frame->pts;
                if (pts >= _videoEnd)
                {
                    ffmpeg.av_frame_unref(_frame);
                    return true;
                }
                if (pts < _videoStart)
                {
                    ffmpeg.av_frame_unref(_frame);
                    continue;
                }

                AVFrame* picture = _frame;
                if (_frame->hw_frames_ctx != null)
                {
                    Check(ffmpeg.av_hwframe_transfer_data(_transfer, _frame, 0), "Reading the decoded picture");
                    ffmpeg.av_frame_copy_props(_transfer, _frame);
                    ffmpeg.av_frame_unref(_frame);
                    picture = _transfer;
                }
                picture->pts = pts - _videoStart;
                if (_graph == null)
                    OpenFilter(picture);
                Check(ffmpeg.av_buffersrc_add_frame(_source, picture), "Preparing the picture");
                ffmpeg.av_frame_unref(picture);
                DrainFilter(progress, ct);
            }
        }

        /// <summary>fps, then the 9:16 window, then the size, in the encoders' common format (NV12).</summary>
        void OpenFilter(AVFrame* first)
        {
            AVRational tb = _input.Video->time_base;
            string args = $"video_size={first->width}x{first->height}:pix_fmt={first->format}:time_base={tb.num}/{tb.den}:pixel_aspect=1/1" +
                $":colorspace={(int)first->colorspace}:range={(int)first->color_range}";
            var chain = new List<string> { $"fps={_rate.num}/{_rate.den}" };
            if (_plan.Crop is { } c)
                chain.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}:exact=1");
            // Enlarging (a 4K copy) uses Lanczos, which keeps edges sharper than bicubic; shrinking stays bicubic.
            int sourceWidth = _plan.Crop?.Width ?? first->width;
            chain.Add($"scale={_plan.Width}:{_plan.Height}:flags={(_plan.Width > sourceWidth ? "lanczos" : "bicubic")}");
            chain.Add("format=nv12");
            chain.Add("setsar=1");

            _graph = ffmpeg.avfilter_graph_alloc();
            FilterChain.Build(_graph, sound: false, args, string.Join(',', chain), "Picture filters", out _source, out _sink);
        }

        void DrainFilter(IProgress<double>? progress, CancellationToken ct)
        {
            while (true)
            {
                int result = ffmpeg.av_buffersink_get_frame(_sink, _filtered);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return;
                Check(result, "Preparing the picture");
                ct.ThrowIfCancellationRequested();
                _filtered->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                _filtered->pts = ffmpeg.av_rescale_q(_filtered->pts, ffmpeg.av_buffersink_get_time_base(_sink), _encoder->time_base);
                Encode(_filtered);
                ffmpeg.av_frame_unref(_filtered);
                _framesOut++;
                Report(progress);
            }
        }

        void Report(IProgress<double>? progress)
        {
            if (progress == null || _plan.Duration <= 0)
                return;
            double done = Math.Clamp(_framesOut / _plan.Fps / _plan.Duration, 0, 0.99);
            int percent = (int)(done * 100);
            if (percent != _lastPercent)
            {
                _lastPercent = percent;
                progress.Report(done);
            }
        }

        void Encode(AVFrame* frame)
        {
            int sent = ffmpeg.avcodec_send_frame(_encoder, frame);
            if (sent < 0 && sent != ffmpeg.AVERROR_EOF)
                throw new EncoderFailedException(ErrorText(sent));
            while (true)
            {
                int result = ffmpeg.avcodec_receive_packet(_encoder, _packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return;
                if (result < 0)
                    throw new EncoderFailedException(ErrorText(result));
                _packet->stream_index = _outVideo->index;
                ffmpeg.av_packet_rescale_ts(_packet, _encoder->time_base, _outVideo->time_base);
                _videoBytes += _packet->size;
                Check(ffmpeg.av_interleaved_write_frame(_output, _packet), "Writing the video");
            }
        }

        /// <summary>Copies one audio packet if it is inside the cut; true once past the end.</summary>
        bool CopyAudio()
        {
            long pts = _packet->pts;
            if (pts == ffmpeg.AV_NOPTS_VALUE)
                return false;
            if (pts >= _audioEnd)
                return true;
            if (pts < _audioStart)
                return false;
            _packet->pts -= _audioStart;
            if (_packet->dts != ffmpeg.AV_NOPTS_VALUE)
                _packet->dts -= _audioStart;
            _packet->stream_index = _outAudio->index;
            _packet->pos = -1;
            ffmpeg.av_packet_rescale_ts(_packet, _input.Audio->time_base, _outAudio->time_base);
            _audioBytes += _packet->size;
            Check(ffmpeg.av_interleaved_write_frame(_output, _packet), "Writing the audio");
            return false;
        }

        public void Dispose()
        {
            if (_output != null)
            {
                if (_output->pb != null)
                    ffmpeg.avio_closep(&_output->pb);
                ffmpeg.avformat_free_context(_output);
                _output = null;
            }
            AVFilterGraph* graph = _graph;
            if (graph != null)
                ffmpeg.avfilter_graph_free(&graph);
            _graph = null;
            AVCodecContext* encoder = _encoder;
            if (encoder != null)
                ffmpeg.avcodec_free_context(&encoder);
            _encoder = null;
            AVCodecContext* decoder = _decoder;
            if (decoder != null)
                ffmpeg.avcodec_free_context(&decoder);
            _decoder = null;
            HardwareDecoding.Return(_device);
            _device = null;
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            frame = _transfer;
            ffmpeg.av_frame_free(&frame);
            frame = _filtered;
            ffmpeg.av_frame_free(&frame);
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _frame = _transfer = _filtered = null;
            _packet = null;
            _input.Dispose();
        }
    }
}
