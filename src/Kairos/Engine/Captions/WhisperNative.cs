using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kairos.Engine.Captions;

/// <summary>
/// whisper.cpp 1.9.5 (downloaded on first use, built with Vulkan). Only the settings Kairos changes are named; the rest keep
/// whisper.cpp's defaults. Sizes and offsets were measured from that version's whisper.h with the MSVC compiler
/// (sizeof / offsetof): they must be measured again if the library is ever updated.
/// </summary>
internal static unsafe class WhisperNative
{
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct ContextParams
    {
        [FieldOffset(0)] public byte UseGpu;
        [FieldOffset(4)] public int GpuDevice;
    }

    [StructLayout(LayoutKind.Explicit, Size = 304)]
    public struct FullParams
    {
        [FieldOffset(4)] public int Threads;
        [FieldOffset(24)] public byte PrintSpecial;
        [FieldOffset(25)] public byte PrintProgress;
        [FieldOffset(26)] public byte PrintRealtime;
        [FieldOffset(27)] public byte PrintTimestamps;
        /// <summary>1 = a time for each word (lines then start and end with their words).</summary>
        [FieldOffset(28)] public byte TokenTimestamps;
        /// <summary>Longest line in characters (0 = no limit; needs <see cref="TokenTimestamps"/>).</summary>
        [FieldOffset(40)] public int MaxLength;
        /// <summary>1 = <see cref="MaxLength"/> breaks lines between words.</summary>
        [FieldOffset(44)] public byte SplitOnWord;
        /// <summary>"auto" = detect the language.</summary>
        [FieldOffset(104)] public byte* Language;
        /// <summary>1 = only detect the language and stop (kept 0).</summary>
        [FieldOffset(112)] public byte DetectLanguage;
        /// <summary>1 = leave out non-speech tokens ("[Music]" and the like).</summary>
        [FieldOffset(114)] public byte SuppressNonSpeech;
        [FieldOffset(176)] public delegate* unmanaged[Cdecl]<nint, nint, int, nint, void> Progress;
        [FieldOffset(184)] public nint ProgressData;
        [FieldOffset(208)] public delegate* unmanaged[Cdecl]<nint, byte> Abort;
        [FieldOffset(216)] public nint AbortData;
    }

    static readonly Lock LoadLock = new();
    static nint _library;

    public static delegate* unmanaged[Cdecl]<ContextParams> ContextDefaultParams;
    public static delegate* unmanaged[Cdecl]<int, FullParams> FullDefaultParams;
    public static delegate* unmanaged[Cdecl]<byte*, ContextParams, nint> InitFromFile;
    public static delegate* unmanaged[Cdecl]<nint, FullParams, float*, int, int> Full;
    public static delegate* unmanaged[Cdecl]<nint, int> SegmentCount;
    public static delegate* unmanaged[Cdecl]<nint, int, long> SegmentT0;
    public static delegate* unmanaged[Cdecl]<nint, int, long> SegmentT1;
    public static delegate* unmanaged[Cdecl]<nint, int, byte*> SegmentText;
    public static delegate* unmanaged[Cdecl]<nint, void> Free;
    static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, byte*, nint, void>, nint, void> LogSet;

    /// <summary>Loads whisper.dll and its ggml libraries from <paramref name="folder"/> once per process (the captions
    /// process: it ends when the captions are done).</summary>
    public static void Load(string folder)
    {
        lock (LoadLock)
        {
            if (_library != 0)
                return;
            string path = Path.Combine(folder, CaptionRuntime.MainLibrary);
            if (!File.Exists(path))
                throw new CaptionException("The captions runtime is missing from Kairos's folder.");
            nint library;
            try
            {
                // A full path: Windows finds ggml*.dll beside it.
                library = NativeLibrary.Load(path);
            }
            catch (DllNotFoundException)
            {
                throw new CaptionException("The captions runtime could not start (it needs a graphics driver with Vulkan).");
            }
            ContextDefaultParams = (delegate* unmanaged[Cdecl]<ContextParams>)NativeLibrary.GetExport(library, "whisper_context_default_params");
            FullDefaultParams = (delegate* unmanaged[Cdecl]<int, FullParams>)NativeLibrary.GetExport(library, "whisper_full_default_params");
            InitFromFile = (delegate* unmanaged[Cdecl]<byte*, ContextParams, nint>)NativeLibrary.GetExport(library, "whisper_init_from_file_with_params");
            Full = (delegate* unmanaged[Cdecl]<nint, FullParams, float*, int, int>)NativeLibrary.GetExport(library, "whisper_full");
            SegmentCount = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "whisper_full_n_segments");
            SegmentT0 = (delegate* unmanaged[Cdecl]<nint, int, long>)NativeLibrary.GetExport(library, "whisper_full_get_segment_t0");
            SegmentT1 = (delegate* unmanaged[Cdecl]<nint, int, long>)NativeLibrary.GetExport(library, "whisper_full_get_segment_t1");
            SegmentText = (delegate* unmanaged[Cdecl]<nint, int, byte*>)NativeLibrary.GetExport(library, "whisper_full_get_segment_text");
            Free = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "whisper_free");
            LogSet = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, byte*, nint, void>, nint, void>)NativeLibrary.GetExport(library, "whisper_log_set");
            // whisper.cpp and ggml write their progress to the console; Kairos keeps it quiet.
            LogSet(&Quiet, 0);
            _library = library;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Quiet(int level, byte* text, nint data)
    {
    }
}

/// <summary>Why auto captions could not run, in words for the editor.</summary>
public sealed class CaptionException(string message) : Exception(message);
