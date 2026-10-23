using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kairos.Native;

namespace Kairos.Engine.Audio;

/// <summary>
/// The libobs source type <see cref="Id"/>: one process's own sound (<see cref="ProcessLoopback"/>), set by the "pid"
/// setting and moved to another process with obs_source_update. Registered once, after libobs starts.
/// </summary>
public static unsafe class ProcessAudioSource
{
    public const string Id = "kairos_process_audio";

    static IntPtr _id, _name;

    /// <summary>The first members of struct obs_source_info (libobs 32.2.2, obs-source.h); the rest stay zero.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct SourceInfo
    {
        public IntPtr Id;
        public int Type;
        public uint OutputFlags;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr> GetName;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> Create;
        public delegate* unmanaged[Cdecl]<IntPtr, void> Destroy;
        public IntPtr GetWidth, GetHeight, GetDefaults, GetProperties;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> Update;
    }

    /// <summary>struct obs_source_audio (obs.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct SourceAudio
    {
        public IntPtr Data0, Data1, Data2, Data3, Data4, Data5, Data6, Data7;
        public uint Frames;
        public int Speakers;
        public int Format;
        public uint SamplesPerSec;
        public ulong Timestamp;
    }

    public static void Register()
    {
        if (_id != IntPtr.Zero)
            return;
        _id = Marshal.StringToCoTaskMemUTF8(Id);      // libobs keeps the pointer: never freed
        _name = Marshal.StringToCoTaskMemUTF8("Process audio");
        var info = new SourceInfo
        {
            Id = _id,
            Type = 0,                // OBS_SOURCE_TYPE_INPUT
            OutputFlags = 1u << 1,   // OBS_SOURCE_AUDIO
            GetName = &GetName,
            Create = &Create,
            Destroy = &Destroy,
            Update = &Update,
        };
        Obs.obs_register_source_s((IntPtr)(&info), (nuint)sizeof(SourceInfo));
    }

    /// <summary>How long a capture that failed <paramref name="failures"/> times before waits to be tried again: 2, 4, 8 … 60 s.</summary>
    internal static TimeSpan RetryAfter(int failures) => TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(failures, 5)));

    /// <summary>One source's state: its libobs source and the capture of its current process.</summary>
    sealed class State(IntPtr source)
    {
        public readonly IntPtr Source = source;
        public ProcessLoopback? Capture;
        public uint Pid;
        int _failures;
        DateTime _retryAt;

        /// <summary>
        /// Captures <paramref name="pid"/> (asked every 2 s). A capture that failed or stopped is tried again for the same
        /// process after 2, 4, 8 … up to 60 s, never on every ask (each try is a thread and a line in the log).
        /// </summary>
        public void Target(uint pid)
        {
            var now = DateTime.UtcNow;
            if (pid == Pid)
            {
                if (Capture?.Error == null || now < _retryAt)
                    return; // the same process: its capture runs, or waits to be tried again
                _failures++;
            }
            else
            {
                _failures = 0;
            }
            _retryAt = now + RetryAfter(_failures);
            Capture?.Dispose();
            Capture = null;
            Pid = pid;
            if (pid != 0)
                Capture = new ProcessLoopback(pid, Output);
        }


        void Output(IntPtr data, uint frames, ulong timestamp)
        {
            var audio = new SourceAudio
            {
                Data0 = data,
                Frames = frames,
                Speakers = ObsConst.SpeakersStereo,
                Format = 4, // AUDIO_FORMAT_FLOAT (interleaved)
                SamplesPerSec = ProcessLoopback.SampleRate,
                Timestamp = timestamp,
            };
            Obs.obs_source_output_audio(Source, (IntPtr)(&audio));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static IntPtr GetName(IntPtr typeData) => _name;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static IntPtr Create(IntPtr settings, IntPtr source)
    {
        try
        {
            var state = new State(source);
            state.Target((uint)Obs.obs_data_get_int(settings, "pid"));
            return GCHandle.ToIntPtr(GCHandle.Alloc(state));
        }
        catch (Exception e)
        {
            ObsLog.Error($"process audio: {e}");
            return IntPtr.Zero; // libobs then does not create the source
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Destroy(IntPtr data)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(data);
            ((State)handle.Target!).Target(0);
            handle.Free();
        }
        catch (Exception e)
        {
            ObsLog.Error($"process audio: {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Update(IntPtr data, IntPtr settings)
    {
        try
        {
            ((State)GCHandle.FromIntPtr(data).Target!).Target((uint)Obs.obs_data_get_int(settings, "pid"));
        }
        catch (Exception e)
        {
            ObsLog.Error($"process audio: {e}");
        }
    }
}
