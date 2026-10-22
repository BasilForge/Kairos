using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kairos.Native;

namespace Kairos.Engine.Replay;

/// <summary>
/// The libobs output type <see cref="Id"/>: a replay buffer kept on a drive (<see cref="ReplayRing"/>) instead of RAM.
/// It acts like libobs's own "replay_buffer" towards the pipeline: the proc handlers "save" and "get_last_replay" and the
/// signal "saved", so starting, stopping, saving and starting fresh go through the same calls; and "save_failed" when a
/// clip could not be written (replay_buffer stays silent then). Settings: "directory" and "format" (the clip's folder and
/// name, as replay_buffer), "seconds", "ring_path", "ring_bytes" and "keep_seconds" (its ring file and the longest clip
/// asked of it); or "share": the name of the output whose ring it takes its clips from (the quick clip: no writes of its
/// own). Registered once, after libobs starts.
/// </summary>
public static unsafe class DiskReplayOutput
{
    public const string Id = "kairos_disk_replay";

    static IntPtr _id, _name;

    /// <summary>The rings by their output's name, for the outputs that share one.</summary>
    static readonly ConcurrentDictionary<string, State> Rings = new();

    /// <summary>Every output of this type, by its libobs pointer.</summary>
    static readonly ConcurrentDictionary<IntPtr, State> Outputs = new();

    /// <summary>
    /// The next clip of <paramref name="output"/> is written straight to <paramref name="path"/> (its app's folder and
    /// name, as NVIDIA writes into the game's folder): no move afterwards for an antivirus or a thumbnail to get in the
    /// way of. False when <paramref name="output"/> is not one of these (a replay in RAM).
    /// </summary>
    public static bool PlaceNext(IntPtr output, string path)
    {
        if (!Outputs.TryGetValue(output, out var state))
            return false;
        state.NextPath = path;
        return true;
    }

    /// <summary>A ring could not be written: the pipeline goes back to RAM.</summary>
    public static event Action? Failed;

    /// <summary>
    /// Whether the output <paramref name="name"/> has its ring (libobs still makes an output whose create failed: the
    /// pipeline asks, and makes a RAM replay instead).
    /// </summary>
    public static bool HasRing(string name) => Rings.ContainsKey(name);

    /// <summary>struct obs_output_info (libobs 32.2.2, obs-output.h), up to encoded_packet; the rest stay zero.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct OutputInfo
    {
        public IntPtr Id;
        public uint Flags;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr> GetName;
        public delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr> Create;
        public delegate* unmanaged[Cdecl]<IntPtr, void> Destroy;
        public delegate* unmanaged[Cdecl]<IntPtr, byte> Start;
        public delegate* unmanaged[Cdecl]<IntPtr, ulong, void> Stop;
        public IntPtr RawVideo, RawAudio;
        public delegate* unmanaged[Cdecl]<IntPtr, Packet*, void> EncodedPacket;
        public IntPtr Update, GetDefaults, GetProperties, Unused1, GetTotalBytes, GetDroppedFrames, TypeData, FreeTypeData,
            GetCongestion, GetConnectTimeMs, EncodedVideoCodecs, EncodedAudioCodecs, RawAudio2, Protocols;
    }

    /// <summary>struct encoder_packet (obs-encoder.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Packet
    {
        public byte* Data;
        public nuint Size;
        public long Pts, Dts;
        public int TbNum, TbDen;
        public int Type;      // OBS_ENCODER_AUDIO 0, OBS_ENCODER_VIDEO 1
        public byte Keyframe;
        public long DtsUsec, SysDtsUsec;
        public int Priority, DropPriority;
        public nuint TrackIdx;
        public IntPtr Encoder;
    }

    public static void Register()
    {
        if (_id != IntPtr.Zero)
            return;
        _id = Marshal.StringToCoTaskMemUTF8(Id); // libobs keeps the pointer: never freed
        _name = Marshal.StringToCoTaskMemUTF8("Replay on the disk");
        var info = new OutputInfo
        {
            Id = _id,
            Flags = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 4), // VIDEO | AUDIO | ENCODED | MULTI_TRACK
            GetName = &GetName,
            Create = &Create,
            Destroy = &Destroy,
            Start = &Start,
            Stop = &Stop,
            EncodedPacket = &EncodedPacket,
        };
        Obs.obs_register_output_s((IntPtr)(&info), (nuint)sizeof(OutputInfo));
    }

    /// <summary>One output: its ring (or the shared one's name), where its clips go, and the last one written.</summary>
    sealed class State(IntPtr output, string name)
    {
        public readonly IntPtr Output = output;
        public readonly string Name = name;
        public ReplayRing? Ring;
        public string? Share;
        public string Directory = "", Format = "";
        public double Seconds;
        public string? LastPath, NextPath;
        public int Saving;
        public Task? SaveTask;

        public State? RingOwner => Share == null ? this : Rings.GetValueOrDefault(Share);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static IntPtr GetName(IntPtr typeData) => _name;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static IntPtr Create(IntPtr settings, IntPtr output)
    {
        try
        {
            var state = new State(output, Obs.Utf8(Obs.obs_output_get_name(output)) ?? "")
            {
                Share = Obs.Utf8(Obs.obs_data_get_string(settings, "share")) is { Length: > 0 } s ? s : null,
                Directory = Obs.Utf8(Obs.obs_data_get_string(settings, "directory")) ?? "",
                Format = Obs.Utf8(Obs.obs_data_get_string(settings, "format")) ?? "",
                Seconds = Obs.obs_data_get_int(settings, "seconds"),
            };
            if (state.Share == null)
            {
                state.Ring = new ReplayRing(Obs.Utf8(Obs.obs_data_get_string(settings, "ring_path"))!, Obs.obs_data_get_int(settings, "ring_bytes"),
                    Obs.obs_data_get_int(settings, "keep_seconds"));
                state.Ring.Failed += e =>
                {
                    ObsLog.Error($"replay on the disk: writing failed: {e.Message}");
                    Failed?.Invoke();
                };
                Rings[state.Name] = state;
            }
            var handle = GCHandle.ToIntPtr(GCHandle.Alloc(state));
            Outputs[output] = state;
            IntPtr signals = Obs.obs_output_get_signal_handler(output);
            Obs.signal_handler_add(signals, "void saved()");
            Obs.signal_handler_add(signals, "void save_failed()");
            IntPtr procs = Obs.obs_output_get_proc_handler(output);
            Obs.proc_handler_add(procs, "void save()", &Save, handle);
            Obs.proc_handler_add(procs, "void get_last_replay(out string path)", &GetLastReplay, handle);
            return handle;
        }
        catch (Exception e)
        {
            ObsLog.Error($"replay on the disk: {e.Message}");
            return IntPtr.Zero; // libobs then does not create the output: the pipeline keeps the replay in RAM
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Destroy(IntPtr data)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(data);
            var state = (State)handle.Target!;
            // A clip being written finishes first (replay_buffer waits for its writer too): it uses the encoders.
            if (state.SaveTask is { IsCompleted: false } saving && !saving.Wait(TimeSpan.FromSeconds(60)))
                ObsLog.Warn("replay on the disk: a clip was still being written as its replay went");
            Outputs.TryRemove(state.Output, out _);
            if (state.Ring != null)
            {
                Rings.TryRemove(new KeyValuePair<string, State>(state.Name, state));
                state.Ring.Dispose();
            }
            handle.Free();
        }
        catch (Exception e)
        {
            ObsLog.Error($"replay on the disk: {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static byte Start(IntPtr data)
    {
        try
        {
            var state = (State)GCHandle.FromIntPtr(data).Target!;
            if (state.Ring is { HasFailed: true } || !Obs.obs_output_can_begin_data_capture(state.Output, 0)
                || !Obs.obs_output_initialize_encoders(state.Output, 0))
                return 0;
            state.Ring?.Clear(); // a start (or a fresh one after a save) begins empty, as replay_buffer does
            return Obs.obs_output_begin_data_capture(state.Output, 0) ? (byte)1 : (byte)0;
        }
        catch (Exception e)
        {
            ObsLog.Error($"replay on the disk: {e.Message}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Stop(IntPtr data, ulong ts)
    {
        try
        {
            Obs.obs_output_end_data_capture(((State)GCHandle.FromIntPtr(data).Target!).Output);
        }
        catch (Exception e)
        {
            ObsLog.Error($"replay on the disk: {e.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void EncodedPacket(IntPtr data, Packet* p)
    {
        try
        {
            if (p == null || p->Data == null)
                return;
            var ring = ((State)GCHandle.FromIntPtr(data).Target!).Ring;
            if (ring == null)
                return; // shares another output's ring
            int track = p->Type == 1 ? 0 : (int)p->TrackIdx + 1;
            ring.Append(new RingPacket(p->Pts, p->Dts, p->DtsUsec, p->TbNum, p->TbDen, track, p->Keyframe != 0),
                new ReadOnlySpan<byte>(p->Data, (int)p->Size));
        }
        catch (Exception e)
        {
            ObsLog.Error($"replay on the disk: {e.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void Save(IntPtr data, IntPtr calldata)
    {
        try
        {
            var state = (State)GCHandle.FromIntPtr(data).Target!;
            if (Interlocked.Exchange(ref state.Saving, 1) == 1)
                return;
            state.SaveTask = Task.Run(() =>
            {
                try
                {
                    SaveNow(state);
                }
                catch (Exception e)
                {
                    ObsLog.Error($"replay on the disk: the clip could not be written: {e.Message}");
                    Signal(state, "save_failed"); // the pipeline says so now, not after the write limit
                }
                finally
                {
                    Volatile.Write(ref state.Saving, 0);
                }
            });
        }
        catch (Exception e)
        {
            ObsLog.Error($"replay on the disk: {e.Message}");
        }
    }

    static void Signal(State state, string signal)
    {
        var cd = new CallData();
        Obs.signal_handler_signal(Obs.obs_output_get_signal_handler(state.Output), signal, ref cd);
        Obs.FreeCallData(ref cd);
    }

    /// <summary>The last seconds into an MP4 named as replay_buffer names it, then "saved" (as replay_buffer signals it).</summary>
    static void SaveNow(State state)
    {
        var owner = state.RingOwner ?? throw new IOException("its replay is gone");
        var slice = owner.Ring!.Take(state.Seconds) ?? throw new IOException("nothing to save yet");
        string? path = Interlocked.Exchange(ref state.NextPath, null);
        if (path == null)
        {
            IntPtr name = Obs.os_generate_formatted_filename("mp4", true, state.Format);
            path = Path.Combine(state.Directory, Obs.Utf8(name)!);
            Obs.bfree(name);
        }
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var (video, audio) = Streams(owner);
        state.LastPath = RingMuxer.Write(path, video, audio, slice);
        Signal(state, "saved");
    }

    /// <summary>The ring owner's encoders as the MP4 needs them (their header bytes exist once they started).</summary>
    static (StreamInfo Video, List<StreamInfo> Audio) Streams(State owner)
    {
        IntPtr v = Obs.obs_output_get_video_encoder(owner.Output);
        var video = new StreamInfo(Obs.Utf8(Obs.obs_encoder_get_codec(v))!, (int)Obs.obs_encoder_get_width(v), (int)Obs.obs_encoder_get_height(v),
            0, 0, Extra(v));
        var audio = new List<StreamInfo>();
        for (nuint i = 0; i < 6; i++)
        {
            IntPtr a = Obs.obs_output_get_audio_encoder(owner.Output, i);
            if (a == IntPtr.Zero)
                break;
            audio.Add(new StreamInfo(Obs.Utf8(Obs.obs_encoder_get_codec(a))!, 0, 0, (int)Obs.obs_encoder_get_sample_rate(a),
                (int)Obs.audio_output_get_channels(Obs.obs_encoder_audio(a)), Extra(a)));
        }
        return (video, audio);
    }

    static byte[] Extra(IntPtr encoder) =>
        Obs.obs_encoder_get_extra_data(encoder, out IntPtr data, out nuint size) && data != IntPtr.Zero
            ? new ReadOnlySpan<byte>((void*)data, (int)size).ToArray()
            : [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void GetLastReplay(IntPtr data, IntPtr calldata)
    {
        try
        {
            if (((State)GCHandle.FromIntPtr(data).Target!).LastPath is { } path)
                Obs.CallDataSetString(calldata, "path", path);
        }
        catch (Exception e)
        {
            ObsLog.Error($"replay on the disk: {e.Message}");
        }
    }

    /// <summary>Bytes the ring of <paramref name="output"/> has written (the measures; 0 for none).</summary>
    public static long BytesWritten(string output) => Rings.TryGetValue(output, out var s) ? s.Ring?.BytesWritten ?? 0 : 0;
}
