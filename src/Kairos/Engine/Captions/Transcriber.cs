using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kairos.Engine.Captions;

/// <summary>
/// Listens to one sound track with Whisper on the graphics card and returns what was said, line by line, in source
/// seconds. The language is detected. The model is loaded for this call only and freed before it returns.
/// </summary>
public static unsafe class Transcriber
{
    /// <summary>Reading the sound and finding the speech are these shares of the progress; listening is the rest.</summary>
    const double ReadShare = 0.1, FindShare = 0.15;

    /// <summary>
    /// Whisper's longest line, which it counts in bytes: an Arabic letter is two, so twice <see cref="Captions.MaxLine"/>
    /// gives Arabic lines as many letters as English ones (longer English lines are split by <see cref="Captions"/>).
    /// </summary>
    public const int LineBytes = 2 * Captions.MaxLine;

    /// <param name="streams">Which audio streams, heard together (<see cref="Captions.Streams"/>).</param>
    /// <param name="modelPath">Default: the downloaded model.</param>
    /// <param name="runtimeFolder">Default: the downloaded engine (<see cref="CaptionRuntime.Default"/>).</param>
    public static IReadOnlyList<Caption> Run(string clip, IReadOnlyList<int> streams, IProgress<double>? progress, CancellationToken ct,
        string? modelPath = null, string? runtimeFolder = null)
    {
        modelPath ??= CaptionModel.Default.Path;
        if (!File.Exists(modelPath))
            throw new CaptionException("The captions model is not downloaded.");
        WhisperNative.Load(runtimeFolder ?? CaptionRuntime.Default.Folder);

        var sound = TrackAudio.Read(clip, streams, progress == null ? null : new ScaledProgress(progress, 0, ReadShare), ct);
        double soundStart = sound.Start;
        // Only where someone talks, one part after another: no lines made up in the quiet, and each line timed from
        // its own words.
        var regions = SpeechRegions.Find(sound.Samples, ct, progress == null ? null : new ScaledProgress(progress, ReadShare, FindShare));
        var speech = SpeechMap.Join(sound.Samples, regions);
        sound = null; // the whole track is not needed while Whisper listens (an hour of it is 230 MB)
        progress?.Report(FindShare);
        if (speech.Samples.Length < TrackAudio.Rate / 4)
        {
            progress?.Report(1);
            return [];
        }

        var context = WhisperNative.ContextDefaultParams();
        context.UseGpu = 1;
        nint whisper;
        fixed (byte* path = Encoding.UTF8.GetBytes(modelPath + "\0"))
            whisper = WhisperNative.InitFromFile(path, context);
        if (whisper == 0)
            throw new CaptionException("The captions model could not be loaded.");

        var listening = new Listening(progress == null ? null : new ScaledProgress(progress, FindShare, 1), ct);
        var handle = GCHandle.Alloc(listening);
        try
        {
            // Beam search: five candidate lines weighed together, not the first that comes (more accurate, a few
            // seconds more while listening; nothing to download).
            var full = WhisperNative.FullDefaultParams(1);
            full.Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            full.PrintSpecial = full.PrintProgress = full.PrintRealtime = full.PrintTimestamps = 0;
            full.DetectLanguage = 0;
            full.SuppressNonSpeech = 1;
            full.TokenTimestamps = 1;
            full.MaxLength = LineBytes;
            full.SplitOnWord = 1;
            full.Progress = &OnProgress;
            full.ProgressData = GCHandle.ToIntPtr(handle);
            full.Abort = &OnAbort;
            full.AbortData = GCHandle.ToIntPtr(handle);
            int result;
            fixed (byte* auto = "auto\0"u8)
            fixed (float* samples = speech.Samples)
            {
                full.Language = auto; // detect the language
                result = WhisperNative.Full(whisper, full, samples, speech.Samples.Length);
            }
            ct.ThrowIfCancellationRequested();
            if (result != 0)
                throw new CaptionException($"Listening failed ({result}).");

            var lines = new List<Caption>();
            int count = WhisperNative.SegmentCount(whisper);
            for (int i = 0; i < count; i++)
            {
                string text = Marshal.PtrToStringUTF8((nint)WhisperNative.SegmentText(whisper, i)) ?? "";
                // Times are in hundredths of a second from the start of the spoken parts.
                var (from, to) = speech.ToSource(WhisperNative.SegmentT0(whisper, i) / 100.0, WhisperNative.SegmentT1(whisper, i) / 100.0);
                double start = Math.Max(0, soundStart + from);
                double end = Math.Max(start, soundStart + to);
                lines.Add(new Caption(start, end, text.Trim()));
            }
            progress?.Report(1);
            return lines;
        }
        finally
        {
            WhisperNative.Free(whisper);
            handle.Free();
        }
    }

    sealed class Listening(IProgress<double>? progress, CancellationToken ct)
    {
        public void Report(int percent) => progress?.Report(Math.Clamp(percent, 0, 100) / 100.0);
        public bool Cancelled => ct.IsCancellationRequested;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnProgress(nint context, nint state, int percent, nint data) =>
        ((Listening)GCHandle.FromIntPtr(data).Target!).Report(percent);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte OnAbort(nint data) => ((Listening)GCHandle.FromIntPtr(data).Target!).Cancelled ? (byte)1 : (byte)0;
}
