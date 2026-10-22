using FFmpeg.AutoGen;
using Kairos.Engine.Share;

namespace Kairos.Engine.Edit;

/// <summary>
/// Writes an edited clip in one pass with the FFmpeg that OBS ships: the video decoded on the graphics card when it
/// can, through the edit's picture graph, encoded on the graphics card with Kairos's codec and quality; the sound
/// tracks the edit uses decoded, mixed and cut by its sound graph, encoded as one AAC track. Written through a part
/// file (index first) that is only renamed when everything succeeded; a failure or a cancel leaves nothing.
/// </summary>
public static unsafe class EditExporter
{
    static readonly AVRational Microseconds = new() { num = 1, den = ffmpeg.AV_TIME_BASE };

    static readonly Lock PriorityLock = new();
    static int _saving;
    static System.Diagnostics.ProcessPriorityClass _priorityBefore;

    /// <summary>
    /// While any save runs, the whole process runs below normal: FFmpeg's own decode, filter and encode threads as well,
    /// so a game in front keeps its CPU.
    /// </summary>
    static void LowerProcess()
    {
        lock (PriorityLock)
        {
            if (_saving++ > 0)
                return;
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                _priorityBefore = self.PriorityClass;
                if (_priorityBefore is System.Diagnostics.ProcessPriorityClass.Normal or System.Diagnostics.ProcessPriorityClass.AboveNormal
                    or System.Diagnostics.ProcessPriorityClass.High)
                    self.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _priorityBefore = System.Diagnostics.ProcessPriorityClass.BelowNormal; // left as it is
            }
        }
    }

    static void RestoreProcess()
    {
        lock (PriorityLock)
        {
            if (--_saving > 0 || _priorityBefore == System.Diagnostics.ProcessPriorityClass.BelowNormal)
                return;
            try
            {
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                if (self.PriorityClass == System.Diagnostics.ProcessPriorityClass.BelowNormal)
                    self.PriorityClass = _priorityBefore;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }
    }

    public static ShareResult Export(string source, string target, EditGraph graph, EncoderPlan plan, IProgress<double>? progress,
        CancellationToken ct)
    {
        ClipTranscoder.RequireLoaded();
        string temp = $"{target}.{Guid.NewGuid():N}.part";
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal; // a game in front keeps its CPU
        LowerProcess();
        try
        {
            var failures = new List<string>();
            bool hardwareDecode = true;
            foreach (string encoder in Encoders(ClipTranscoder.Probe(source).VideoCodec))
            {
                if (ffmpeg.avcodec_find_encoder_by_name(encoder) == null)
                    continue;
                try
                {
                    try
                    {
                        Run(source, temp, graph, plan, encoder, hardwareDecode, progress, ct);
                    }
                    catch (HardwareDecodeFailedException)
                    {
                        hardwareDecode = false;
                        ClipTranscoder.TryDelete(temp);
                        Run(source, temp, graph, plan, encoder, hardwareDecode, progress, ct);
                    }
                    long size = new FileInfo(temp).Length;
                    string final = ClipTranscoder.Place(temp, target);
                    progress?.Report(1);
                    return new ShareResult(final, size, encoder);
                }
                catch (EncoderFailedException e)
                {
                    failures.Add($"{encoder}: {e.Message}");
                    ClipTranscoder.TryDelete(temp);
                }
            }
            throw new ShareException("No video encoder could be started. " + string.Join("; ", failures));
        }
        finally
        {
            ClipTranscoder.TryDelete(temp);
            thread.Priority = priority;
            RestoreProcess();
        }
    }

    /// <summary>The clip's own codec on the graphics card first (AV1 stays AV1), then H.264 as sharing does.</summary>
    static IEnumerable<string> Encoders(string codec)
    {
        string[] own = codec switch
        {
            "av1" => ["av1_nvenc", "av1_amf", "av1_qsv"],
            "hevc" => ["hevc_nvenc", "hevc_amf", "hevc_qsv"],
            _ => [],
        };
        return own.Concat(ClipTranscoder.DefaultEncoders);
    }

    /// <summary>NVENC runs on the decoder's D3D11 device (tests turn it off to compare the files).</summary>
    internal static bool UseDecoderDevice = true;

    /// <summary>Pictures decoded by the last export (tests: a short part must not decode the whole clip).</summary>
    internal static long LastDecodedFrames;

    static void Run(string source, string temp, EditGraph graph, EncoderPlan plan, string encoder, bool hardwareDecode,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var pass = new Pass(source, graph, plan);
        pass.Open(hardwareDecode, encoder, temp);
        pass.Convert(progress, ct);
        LastDecodedFrames = pass.FramesDecoded;
    }

    /// <summary>Stage timing for the measuring harness (--perf export): where a save's time goes. Off, each call site costs one bool check.</summary>
    internal static bool Profile;
    internal static readonly string[] StageNames = ["read", "decode", "transfer", "filter", "encode", "write", "sound"];
    internal static readonly long[] StageTicks = new long[StageNames.Length];
    const int StRead = 0, StDecode = 1, StTransfer = 2, StFilter = 3, StEncode = 4, StWrite = 5, StSound = 6;
    static long Now() => Profile ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    static void Took(int stage, long since)
    {
        if (Profile)
            StageTicks[stage] += System.Diagnostics.Stopwatch.GetTimestamp() - since;
    }

    sealed class Pass : IDisposable
    {
        readonly ClipTranscoder.Input _input;
        readonly EditGraph _graph;
        readonly EncoderPlan _plan;
        readonly double _duration;
        readonly AVRational _rate;
        readonly long _origin;
        /// <summary>Reading starts this far before the first kept moment (so the sound is there from it) and runs this
        /// far past the last (so every picture up to it is decoded).</summary>
        const double Lead = 2, Tail = 1;
        bool _videoEnded;
        readonly HashSet<int> _audioEnded = [];
        AVCodecContext* _videoDecoder;
        AVBufferRef* _device;
        AVCodecContext* _videoEncoder;

        AVFilterGraph* _videoGraph;
        AVFilterGraph* _audioGraph;
        AVFilterContext* _videoSource;
        AVFilterContext* _videoSink;

        readonly Dictionary<int, (IntPtr Decoder, IntPtr Source)> _audio = []; // stream index -> decoder, abuffer
        AVFormatContext* _output;
        AVStream* _outVideo;
        readonly List<SoundTrack> _sounds = []; // the MP4's sound tracks, in order
        AVFrame* _frame;
        AVFrame* _transfer;
        AVFrame* _filtered;
        AVPacket* _packet;
        long _framesDecoded;
        public long FramesDecoded => _framesDecoded;

        int _lastPercent = -1;
        string _encoderName = "";

        public Pass(string source, EditGraph graph, EncoderPlan plan)
        {
            _input = new ClipTranscoder.Input(source);
            _graph = graph;
            _plan = plan;
            AVFormatContext* f = _input.Format;
            _duration = f->duration > 0 ? f->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            AVStream* v = _input.Video;
            double fps = ffmpeg.av_q2d(v->avg_frame_rate);
            if (!(fps > 0) || !double.IsFinite(fps))
                fps = ffmpeg.av_q2d(v->r_frame_rate);
            _rate = ffmpeg.av_d2q(fps > 0 ? fps : 60, 100_000);
            _origin = f->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : f->start_time;
        }

        public void Open(bool hardwareDecode, string encoder, string temp)
        {
            _encoderName = encoder;
            _videoDecoder = HardwareDecoding.OpenVideo(_input.Video, hardwareDecode, out _device);
            _frame = ffmpeg.av_frame_alloc();
            _transfer = ffmpeg.av_frame_alloc();
            _filtered = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            OpenAudio();
            OpenOutput(temp);
        }

        /// <summary>A decoder and a graph input per sound track the edit uses, and the AAC encoder for the mix.</summary>
        void OpenAudio()
        {
            if (_graph.AudioStreams.Count == 0 || _graph.Audio.Length == 0)
                return;
            var streams = new List<int>();
            for (int i = 0; i < _input.Format->nb_streams; i++)
                if (_input.Format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    streams.Add(i);
            _audioGraph = ffmpeg.avfilter_graph_alloc();
            AVFilterInOut* outputs = null;
            foreach (int n in _graph.AudioStreams)
            {
                if (n >= streams.Count)
                    throw new ShareException($"The clip has no sound track {n + 1}.");
                AVStream* s = _input.Format->streams[streams[n]];
                AVCodecContext* decoder = SoundCodecs.OpenDecoder(s);
                string args = SoundCodecs.BufferArgs(decoder, s->time_base);
                AVFilterContext* abuffer = null;
                ClipTranscoder.Check(ffmpeg.avfilter_graph_create_filter(&abuffer, ffmpeg.avfilter_get_by_name("abuffer"), $"a{n}", args, null,
                    _audioGraph), "Sound input");
                _audio[s->index] = ((IntPtr)decoder, (IntPtr)abuffer);

                AVFilterInOut* o = ffmpeg.avfilter_inout_alloc();
                o->name = ffmpeg.av_strdup($"a{n}");
                o->filter_ctx = abuffer;
                o->pad_idx = 0;
                o->next = outputs;
                outputs = o;
            }

            AVFilterInOut* inputs = null;
            string text = _graph.Audio;
            for (int o = _graph.AudioOutputs.Count - 1; o >= 0; o--)
            {
                string label = _graph.AudioOutputs[o];
                var track = new SoundTrack { Encoder = SoundCodecs.OpenAac(192_000), Name = o < _graph.AudioNames.Count ? _graph.AudioNames[o] : null };
                _sounds.Insert(0, track);

                AVFilterContext* sink = null;
                ClipTranscoder.Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), label, null, null,
                    _audioGraph), "Sound output");
                track.Sink = sink;
                AVFilterInOut* input = ffmpeg.avfilter_inout_alloc();
                input->name = ffmpeg.av_strdup(label);
                input->filter_ctx = sink;
                input->pad_idx = 0;
                input->next = inputs;
                inputs = input;
                text = text.Replace($"[{label}]", $",{SoundCodecs.AacFormat}[{label}]");
            }
            try
            {
                ClipTranscoder.Check(ffmpeg.avfilter_graph_parse_ptr(_audioGraph, text, &inputs, &outputs, null), "Sound edit");
                ClipTranscoder.Check(ffmpeg.avfilter_graph_config(_audioGraph, null), "Sound edit");
            }
            finally
            {
                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
            }
            foreach (var track in _sounds)
                ffmpeg.av_buffersink_set_frame_size(track.Sink, (uint)track.Encoder->frame_size);
        }

        /// <summary>The picture graph, built from the first decoded picture (its size and format).</summary>
        void OpenVideoGraph(AVFrame* first)
        {
            AVRational tb = _input.Video->time_base;
            string args = $"video_size={first->width}x{first->height}:pix_fmt={first->format}:time_base={tb.num}/{tb.den}:pixel_aspect=1/1" +
                $":colorspace={(int)first->colorspace}:range={(int)first->color_range}";
            _videoGraph = ffmpeg.avfilter_graph_alloc();
            string text = _graph.Video.Replace("[vout]", ",format=nv12,setsar=1[vout]");
            FilterChain.Build(_videoGraph, sound: false, args, text, "Picture edit", out _videoSource, out _videoSink, "vin", "vout");
        }

        void OpenVideoEncoder(AVFrame* first)
        {
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(_encoderName);
            Exception? last = null;
            // NVENC on the decoder's own D3D11 device: no second (CUDA) context on the graphics card for this save, which
            // took about half a gigabyte; the same encoder and settings. Without it (or if it will not open) as before.
            bool[] onDevice = UseDecoderDevice && _device != null && _encoderName.EndsWith("_nvenc", StringComparison.Ordinal) ? [true, false] : [false];
            foreach (bool shared in onDevice)
            foreach (bool tuned in new[] { true, false })
            {
                AVCodecContext* enc = ffmpeg.avcodec_alloc_context3(codec);
                if (shared)
                    enc->hw_device_ctx = ffmpeg.av_buffer_ref(_device);
                enc->width = first->width;
                enc->height = first->height;
                enc->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
                enc->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
                enc->framerate = _rate;
                enc->time_base = new AVRational { num = _rate.den, den = _rate.num };
                long max = (long)_plan.MaxBitrateKbps * 1000;
                enc->bit_rate = _encoderName == "libx264" ? max / 2 : 0;
                enc->rc_max_rate = max;
                enc->rc_buffer_size = (int)Math.Min(int.MaxValue, max * 2);
                enc->gop_size = Math.Max(1, (int)Math.Round(ffmpeg.av_q2d(_rate)));
                enc->color_range = first->color_range;
                enc->colorspace = first->colorspace;
                enc->color_primaries = first->color_primaries;
                enc->color_trc = first->color_trc;
                enc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
                AVDictionary* options = null;
                if (tuned)
                    foreach (var (key, value) in Options(_encoderName, _plan))
                        ffmpeg.av_dict_set(&options, key, value, 0);
                int result = ffmpeg.avcodec_open2(enc, codec, &options);
                ffmpeg.av_dict_free(&options);
                if (result >= 0)
                {
                    _videoEncoder = enc;
                    return;
                }
                last = new EncoderFailedException(ClipTranscoder.ErrorText(result));
                ffmpeg.avcodec_free_context(&enc);
            }
            throw last!;
        }

        /// <summary>Constant quality at the recording's CQ, capped at its bitrate (like Kairos's own recordings).</summary>
        static (string, string)[] Options(string name, EncoderPlan plan)
        {
            string cq = plan.Cq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string preset = plan.Preset.StartsWith('p') ? plan.Preset : "p5";
            return name switch
            {
                "av1_nvenc" or "hevc_nvenc" or "h264_nvenc" => [("preset", preset), ("tune", "hq"), ("rc", "vbr"), ("cq", cq), ("spatial-aq", "1")],
                "av1_amf" or "hevc_amf" or "h264_amf" => [("usage", "transcoding"), ("quality", "quality"), ("rc", "qvbr"), ("qvbr_quality_level", cq)],
                "av1_qsv" or "hevc_qsv" => [("global_quality", cq)],
                "h264_mf" => [("hw_encoding", "1"), ("rate_control", "quality")],
                "libx264" => [("preset", "faster"), ("crf", cq)],
                _ => [],
            };
        }

        void OpenOutput(string path)
        {
            AVFormatContext* output = null;
            ClipTranscoder.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", path), "Creating the MP4");
            _output = output;
        }

        /// <summary>The MP4's tracks and header, once the video encoder exists (it needs the first picture's size).</summary>
        void StartOutput(string path)
        {
            _outVideo = ffmpeg.avformat_new_stream(_output, null);
            ClipTranscoder.Check(ffmpeg.avcodec_parameters_from_context(_outVideo->codecpar, _videoEncoder), "Video track");
            _outVideo->time_base = _videoEncoder->time_base;
            _outVideo->avg_frame_rate = _rate;
            foreach (var track in _sounds)
            {
                track.Stream = ffmpeg.avformat_new_stream(_output, null);
                ClipTranscoder.Check(ffmpeg.avcodec_parameters_from_context(track.Stream->codecpar, track.Encoder), "Audio track");
                track.Stream->time_base = track.Encoder->time_base;
                if (track.Name != null)
                    ffmpeg.av_dict_set(&track.Stream->metadata, "handler_name", track.Name, 0); // the track's name, as Kairos records it
            }
            ClipTranscoder.Check(ffmpeg.avio_open(&_output->pb, path, ffmpeg.AVIO_FLAG_WRITE), "Writing the MP4");
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            int result = ffmpeg.avformat_write_header(_output, &options);
            ffmpeg.av_dict_free(&options);
            ClipTranscoder.Check(result, "Writing the MP4 header");
        }

        string _path = "";

        public void Convert(IProgress<double>? progress, CancellationToken ct)
        {
            _path = Marshal(_output->url);
            int videoIndex = _input.Video->index;
            SeekNearStart();
            // The streams still needed: the pictures and each sound track the edit uses, until they pass the last kept
            // moment or their part of the edit has ended.
            var needed = new HashSet<int>(_audio.Keys) { videoIndex };
            while (needed.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                long t0 = Now();
                int read = ffmpeg.av_read_frame(_input.Format, _packet);
                Took(StRead, t0);
                if (read == ffmpeg.AVERROR_EOF)
                    break;
                ClipTranscoder.Check(read, "Reading the clip");
                try
                {
                    int index = _packet->stream_index;
                    if (!needed.Contains(index))
                        continue;
                    if (PastEnd(_packet))
                        needed.Remove(index);
                    else if (index == videoIndex)
                    {
                        long t1 = Now();
                        CheckDecode(ffmpeg.avcodec_send_packet(_videoDecoder, _packet));
                        Took(StDecode, t1);
                        ReceiveVideo(progress, ct);
                        if (_videoEnded)
                            needed.Remove(index);
                    }
                    else if (_audio.TryGetValue(index, out var a))
                    {
                        long t1 = Now();
                        FeedAudio(index, a, _packet);
                        Took(StSound, t1);
                        if (_audioEnded.Contains(index))
                            needed.Remove(index);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            ffmpeg.avcodec_send_packet(_videoDecoder, null);
            ReceiveVideo(progress, ct);
            if (_videoGraph == null)
                throw new ShareException("The clip has no pictures.");
            if (!_videoEnded)
                Finished(ffmpeg.av_buffersrc_add_frame(_videoSource, null), "Finishing the picture");
            DrainVideo(progress, ct);
            foreach (var (index, a) in _audio)
            {
                FeedAudio(index, a, null);
                if (!_audioEnded.Contains(index))
                    Finished(ffmpeg.av_buffersrc_add_frame((AVFilterContext*)a.Source, null), "Finishing the sound");
            }
            DrainAudio(ct);
            EncodeVideo(null);
            foreach (var track in _sounds)
                EncodeAudio(track, null);
            ClipTranscoder.Check(ffmpeg.av_write_trailer(_output), "Finishing the MP4");
        }

        static string Marshal(byte* text) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)text) ?? "";

        /// <summary>Jumps to the keyframe a little before the first kept moment (from the clip's start, nothing to do).</summary>
        void SeekNearStart()
        {
            double from = _graph.From - Lead;
            if (from <= 0)
                return;
            AVStream* v = _input.Video;
            long at = ffmpeg.av_rescale_q(_origin + (long)(from * ffmpeg.AV_TIME_BASE), Microseconds, v->time_base);
            if (ffmpeg.avformat_seek_file(_input.Format, v->index, long.MinValue, at, at, 0) < 0)
                ffmpeg.avformat_seek_file(_input.Format, -1, long.MinValue, 0, 0, 0); // reading it all is slower, not wrong
        }

        /// <summary>A packet well past the last kept moment: its stream has given all the edit needs.</summary>
        bool PastEnd(AVPacket* packet)
        {
            if (double.IsPositiveInfinity(_graph.To))
                return false;
            long ts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
            if (ts == ffmpeg.AV_NOPTS_VALUE)
                return false;
            AVStream* s = _input.Format->streams[packet->stream_index];
            double seconds = ts * ffmpeg.av_q2d(s->time_base) - _origin / (double)ffmpeg.AV_TIME_BASE;
            return seconds > _graph.To + Tail;
        }

        /// <summary>A graph input that says its part of the edit is over (the last kept piece has ended) is fine.</summary>
        static int Finished(int result, string what) => result == ffmpeg.AVERROR_EOF ? 0 : ClipTranscoder.Check(result, what);

        int CheckDecode(int result)
        {
            if (result < 0 && _device != null && _framesDecoded == 0)
                throw new HardwareDecodeFailedException(ClipTranscoder.ErrorText(result));
            return ClipTranscoder.Check(result, "Decoding the video");
        }

        void ReceiveVideo(IProgress<double>? progress, CancellationToken ct)
        {
            while (true)
            {
                long t0 = Now();
                int result = ffmpeg.avcodec_receive_frame(_videoDecoder, _frame);
                Took(StDecode, t0);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return;
                CheckDecode(result);
                _framesDecoded++;
                long pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE)
                    pts = _frame->pts;
                AVFrame* picture = _frame;
                if (_frame->hw_frames_ctx != null)
                {
                    long t1 = Now();
                    ClipTranscoder.Check(ffmpeg.av_hwframe_transfer_data(_transfer, _frame, 0), "Reading the decoded picture");
                    Took(StTransfer, t1);
                    ffmpeg.av_frame_copy_props(_transfer, _frame);
                    ffmpeg.av_frame_unref(_frame);
                    picture = _transfer;
                }
                // Source time from 0, the clock the edit's times use.
                long start = ffmpeg.av_rescale_q(_origin, Microseconds, _input.Video->time_base);
                picture->pts = pts - start;
                if (_videoGraph == null)
                {
                    OpenVideoGraph(picture);
                    OpenVideoEncoder(picture);
                    StartOutput(_path);
                }
                Report(progress, picture->pts * ffmpeg.av_q2d(_input.Video->time_base));
                if (_graph.ZoomAt is { } zoomAt && !_videoEnded)
                    SetZoom(VideoZoom.Crop(zoomAt(picture->pts * ffmpeg.av_q2d(_input.Video->time_base)), picture->width, picture->height));
                long t2 = Now();
                int added = _videoEnded ? ffmpeg.AVERROR_EOF : ffmpeg.av_buffersrc_add_frame(_videoSource, picture);
                Took(StFilter, t2);
                ffmpeg.av_frame_unref(picture);
                if (added == ffmpeg.AVERROR_EOF)
                    _videoEnded = true; // the last kept piece is over: the rest is not needed
                else
                    ClipTranscoder.Check(added, "Preparing the picture");
                DrainVideo(progress, ct);
                long t3 = Now();
                DrainAudio(ct);
                Took(StSound, t3);
            }
        }

        (int W, int H, int X, int Y)? _zoom;

        /// <summary>The graph's <c>crop@zoom</c> shows this area from the next picture on (only what changed is sent).</summary>
        void SetZoom((int W, int H, int X, int Y) zoom)
        {
            if (_zoom == zoom)
                return;
            foreach (var (name, value) in new[] { ("w", zoom.W), ("h", zoom.H), ("x", zoom.X), ("y", zoom.Y) })
                ClipTranscoder.Check(ffmpeg.avfilter_graph_send_command(_videoGraph, "crop@zoom", name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), null, 0, 0),
                    "Zooming the picture");
            _zoom = zoom;
        }

        void DrainVideo(IProgress<double>? progress, CancellationToken ct)
        {
            while (true)
            {
                long t0 = Now();
                int result = ffmpeg.av_buffersink_get_frame(_videoSink, _filtered);
                Took(StFilter, t0);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return;
                ClipTranscoder.Check(result, "Preparing the picture");
                ct.ThrowIfCancellationRequested();
                _filtered->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
                _filtered->pts = ffmpeg.av_rescale_q(_filtered->pts, ffmpeg.av_buffersink_get_time_base(_videoSink), _videoEncoder->time_base);
                EncodeVideo(_filtered);
                ffmpeg.av_frame_unref(_filtered);
            }
        }

        void FeedAudio(int index, (IntPtr Decoder, IntPtr Source) a, AVPacket* packet)
        {
            var decoder = (AVCodecContext*)a.Decoder;
            int sent = ffmpeg.avcodec_send_packet(decoder, packet);
            if (sent < 0 && sent != ffmpeg.AVERROR_EOF)
                return; // a damaged sound packet is skipped, like players do
            AVStream* s = _input.Format->streams[index];
            long start = ffmpeg.av_rescale_q(_origin, Microseconds, s->time_base);
            while (true)
            {
                int result = ffmpeg.avcodec_receive_frame(decoder, _frame);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return;
                ClipTranscoder.Check(result, "Decoding the sound");
                long pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE)
                    pts = _frame->pts;
                _frame->pts = pts - start;
                int added = _audioEnded.Contains(index) ? ffmpeg.AVERROR_EOF : ffmpeg.av_buffersrc_add_frame((AVFilterContext*)a.Source, _frame);
                ffmpeg.av_frame_unref(_frame);
                if (added == ffmpeg.AVERROR_EOF)
                    _audioEnded.Add(index); // its part of the edit is over
                else
                    ClipTranscoder.Check(added, "Preparing the sound");
            }
        }

        void DrainAudio(CancellationToken ct)
        {
            if (_sounds.Count == 0 || _outVideo == null)
                return; // the MP4 header waits for the first picture
            foreach (var track in _sounds)
                while (true)
                {
                    int result = ffmpeg.av_buffersink_get_frame(track.Sink, _filtered);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                        break;
                    ClipTranscoder.Check(result, "Preparing the sound");
                    ct.ThrowIfCancellationRequested();
                    _filtered->pts = track.Samples;
                    track.Samples += _filtered->nb_samples;
                    EncodeAudio(track, _filtered);
                    ffmpeg.av_frame_unref(_filtered);
                }
        }

        void EncodeVideo(AVFrame* frame)
        {
            if (_videoEncoder == null)
                return;
            long t0 = Now();
            int sent = ffmpeg.avcodec_send_frame(_videoEncoder, frame);
            Took(StEncode, t0);
            if (sent < 0 && sent != ffmpeg.AVERROR_EOF)
                throw new EncoderFailedException(ClipTranscoder.ErrorText(sent));
            long t1 = Now();
            Write(_videoEncoder, _outVideo);
            Took(StWrite, t1);
        }

        void EncodeAudio(SoundTrack track, AVFrame* frame)
        {
            int sent = ffmpeg.avcodec_send_frame(track.Encoder, frame);
            if (sent < 0 && sent != ffmpeg.AVERROR_EOF)
                ClipTranscoder.Check(sent, "Encoding the sound");
            Write(track.Encoder, track.Stream);
        }

        /// <summary>One sound track of the MP4: its graph output, encoder and stream, and the samples written.</summary>
        sealed class SoundTrack
        {
            public AVFilterContext* Sink;
            public AVCodecContext* Encoder;
            public AVStream* Stream;
            public string? Name;
            public long Samples;
        }

        void Write(AVCodecContext* encoder, AVStream* stream)
        {
            while (true)
            {
                int result = ffmpeg.avcodec_receive_packet(encoder, _packet);
                if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF)
                    return;
                if (result < 0)
                {
                    if (encoder == _videoEncoder)
                        throw new EncoderFailedException(ClipTranscoder.ErrorText(result));
                    ClipTranscoder.Check(result, "Encoding the sound");
                }
                _packet->stream_index = stream->index;
                ffmpeg.av_packet_rescale_ts(_packet, encoder->time_base, stream->time_base);
                ClipTranscoder.Check(ffmpeg.av_interleaved_write_frame(_output, _packet), "Writing the clip");
            }
        }

        void Report(IProgress<double>? progress, double seconds)
        {
            if (progress == null || _duration <= 0)
                return;
            // Over the source range the edit keeps (a 10-second end of a long clip runs 0 to 100, not 97 to 100).
            double from = _graph.From, to = Math.Min(_graph.To, _duration);
            double done = Math.Clamp(to - from > 0.01 ? (seconds - from) / (to - from) : seconds / _duration, 0, 0.99);
            int percent = (int)(done * 100);
            if (percent != _lastPercent)
            {
                _lastPercent = percent;
                progress.Report(done);
            }
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
            AVFilterGraph* graph = _videoGraph;
            if (graph != null)
                ffmpeg.avfilter_graph_free(&graph);
            graph = _audioGraph;
            if (graph != null)
                ffmpeg.avfilter_graph_free(&graph);
            _videoGraph = _audioGraph = null;
            foreach (var a in _audio.Values)
            {
                var decoder = (AVCodecContext*)a.Decoder;
                ffmpeg.avcodec_free_context(&decoder);
            }
            _audio.Clear();
            AVCodecContext* context = _videoEncoder;
            if (context != null)
                ffmpeg.avcodec_free_context(&context);
            foreach (var track in _sounds)
            {
                context = track.Encoder;
                if (context != null)
                    ffmpeg.avcodec_free_context(&context);
                track.Encoder = null;
            }
            _sounds.Clear();
            context = _videoDecoder;
            if (context != null)
                ffmpeg.avcodec_free_context(&context);
            _videoEncoder = _videoDecoder = null;
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
