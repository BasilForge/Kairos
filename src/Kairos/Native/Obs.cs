using System.Runtime.InteropServices;

namespace Kairos.Native;

// Layouts and signatures follow libobs 32.2.2 (libobs/obs.h, media-io/*.h, callback/*.h).

[StructLayout(LayoutKind.Sequential)]
public struct ObsVideoInfo
{
    public IntPtr GraphicsModule;
    public uint FpsNum;
    public uint FpsDen;
    public uint BaseWidth;
    public uint BaseHeight;
    public uint OutputWidth;
    public uint OutputHeight;
    public int OutputFormat;
    public uint Adapter;
    public byte GpuConversion;
    public int Colorspace;
    public int Range;
    public int ScaleType;
}

[StructLayout(LayoutKind.Sequential)]
public struct ObsAudioInfo
{
    public uint SamplesPerSec;
    public int Speakers;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vec2(float x, float y)
{
    public float X = x;
    public float Y = y;
}

[StructLayout(LayoutKind.Sequential)]
public struct CallData
{
    public IntPtr Stack;
    public UIntPtr Size;
    public UIntPtr Capacity;
    public byte Fixed;
}

public static class ObsConst
{
    public const int VideoFormatNv12 = 2;
    public const int VideoCs709 = 2;
    public const int VideoRangePartial = 1;
    public const int ScaleBicubic = 2;
    public const int ScaleArea = 5;
    public const int SpeakersStereo = 2;
    public const int VideoSuccess = 0;
    public const int ModuleSuccess = 0;
    public const int BoundsScaleInner = 2;
    public const uint AlignCenter = 0;
    public const int LogError = 100, LogWarning = 200, LogInfo = 300, LogDebug = 400;
    // graphics/graphics.h: enum gs_color_format, gs_zstencil_format, gs_blend_type and the clear flags.
    public const int GsBgra = 5, GsZsNone = 0, GsBlendZero = 0, GsBlendOne = 1;
    public const uint GsClearColor = 1;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ObsLogHandler(int level, IntPtr message, IntPtr args, IntPtr param);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ObsSignalCallback(IntPtr data, IntPtr calldata);

/// <summary>obs_volmeter_updated_t: the three arrays are float[MAX_AUDIO_CHANNELS] in dBFS.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ObsVolmeterCallback(IntPtr param, IntPtr magnitude, IntPtr peak, IntPtr inputPeak);

/// <summary>obs_source_audio_capture_t: audio after the source's resampler, planar float at the output rate.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ObsAudioCaptureCallback(IntPtr param, IntPtr source, IntPtr audioData, [MarshalAs(UnmanagedType.U1)] bool muted);

/// <summary>obs_add_tick_callback's tick: called on libobs's graphics thread once per frame.</summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void ObsTickCallback(IntPtr param, float seconds);

/// <summary>struct vec4 (graphics/vec4.h) as gs_clear reads it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Vec4
{
    public float X, Y, Z, W;
}

/// <summary>struct audio_data (media-io/audio-io.h).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ObsAudioData
{
    public IntPtr Data0, Data1, Data2, Data3, Data4, Data5, Data6, Data7;
    public uint Frames;
    public ulong Timestamp;
}

public static class Obs
{
    const string Lib = "obs";
    const CallingConvention Cc = CallingConvention.Cdecl;
    const UnmanagedType U8 = UnmanagedType.LPUTF8Str;

    // Core
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_startup([MarshalAs(U8)] string locale, [MarshalAs(U8)] string moduleConfigPath, IntPtr store);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)]
    public delegate bool ObsEnumProc(IntPtr param, IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_enum_sources(ObsEnumProc proc, IntPtr param);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_enum_scenes(ObsEnumProc proc, IntPtr param);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_source_get_name(IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_source_get_id(IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_wait_for_destroy_queue();
    [DllImport(Lib, CallingConvention = Cc)] public static extern int obs_reset_video(ref ObsVideoInfo ovi);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_reset_audio(ref ObsAudioInfo oai);
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern int obs_open_module(out IntPtr module, [MarshalAs(U8)] string path, [MarshalAs(U8)] string dataPath);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_init_module(IntPtr module);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_post_load_modules();
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_log_loaded_modules();
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_encoder_types(UIntPtr idx, out IntPtr id);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_get_video();
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_get_audio();
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_set_output_source(uint channel, IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern double obs_get_active_fps();
    [DllImport(Lib, CallingConvention = Cc)] public static extern uint obs_get_lagged_frames();
    [DllImport(Lib, CallingConvention = Cc)] public static extern uint video_output_get_skipped_frames(IntPtr video);
    [DllImport(Lib, CallingConvention = Cc)] public static extern uint video_output_get_total_frames(IntPtr video);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void base_set_log_handler(ObsLogHandler handler, IntPtr param);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void bfree(IntPtr ptr);

    // Data
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_data_create();
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_data_release(IntPtr data);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_data_set_string(IntPtr data, [MarshalAs(U8)] string name, [MarshalAs(U8)] string val);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_data_set_int(IntPtr data, [MarshalAs(U8)] string name, long val);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_data_set_double(IntPtr data, [MarshalAs(U8)] string name, double val);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_data_set_bool(IntPtr data, [MarshalAs(U8)] string name, [MarshalAs(UnmanagedType.U1)] bool val);

    // Properties
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_get_source_properties([MarshalAs(U8)] string id);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_source_properties(IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_source_update(IntPtr source, IntPtr settings);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_properties_get(IntPtr props, [MarshalAs(U8)] string name);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_properties_destroy(IntPtr props);
    [DllImport(Lib, CallingConvention = Cc)] public static extern UIntPtr obs_property_list_item_count(IntPtr p);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_property_list_item_name(IntPtr p, UIntPtr idx);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_property_list_item_string(IntPtr p, UIntPtr idx);
    /// <summary>obs_combo_format: 0 invalid, 1 int, 2 float, 3 string. A list's values are read the way its format says.</summary>
    [DllImport(Lib, CallingConvention = Cc)] public static extern int obs_property_list_format(IntPtr p);
    [DllImport(Lib, CallingConvention = Cc)] public static extern long obs_property_list_item_int(IntPtr p, UIntPtr idx);

    // Sources and scenes
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern IntPtr obs_source_create([MarshalAs(U8)] string id, [MarshalAs(U8)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_source_release(IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_source_remove(IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_source_set_audio_mixers(IntPtr source, uint mixers);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_source_set_volume(IntPtr source, float volume);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_source_set_muted(IntPtr source, [MarshalAs(UnmanagedType.U1)] bool muted);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_scene_create([MarshalAs(U8)] string name);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_scene_release(IntPtr scene);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_scene_get_source(IntPtr scene);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_scene_add(IntPtr scene, IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_sceneitem_set_bounds_type(IntPtr item, int type);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_sceneitem_set_bounds_alignment(IntPtr item, uint alignment);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_sceneitem_set_bounds(IntPtr item, ref Vec2 bounds);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_sceneitem_set_pos(IntPtr item, ref Vec2 pos);

    // Encoders
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern IntPtr obs_video_encoder_create([MarshalAs(U8)] string id, [MarshalAs(U8)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern IntPtr obs_audio_encoder_create([MarshalAs(U8)] string id, [MarshalAs(U8)] string name, IntPtr settings, UIntPtr mixerIdx, IntPtr hotkeyData);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_encoder_set_video(IntPtr encoder, IntPtr video);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_encoder_set_audio(IntPtr encoder, IntPtr audio);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_encoder_release(IntPtr encoder);

    // Outputs
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern IntPtr obs_output_create([MarshalAs(U8)] string id, [MarshalAs(U8)] string name, IntPtr settings, IntPtr hotkeyData);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_output_release(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_output_set_video_encoder(IntPtr output, IntPtr encoder);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_output_set_audio_encoder(IntPtr output, IntPtr encoder, UIntPtr idx);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_output_start(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_output_stop(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_output_force_stop(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_output_active(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_output_get_last_error(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_output_pause(IntPtr output, [MarshalAs(UnmanagedType.U1)] bool pause);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_output_paused(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] public static extern ulong obs_output_get_total_bytes(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_output_get_signal_handler(IntPtr output);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_output_get_proc_handler(IntPtr output);

    // Volume meters (obs-audio-controls.h)
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr obs_volmeter_create(int faderType);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_volmeter_destroy(IntPtr volmeter);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool obs_volmeter_attach_source(IntPtr volmeter, IntPtr source);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_volmeter_detach_source(IntPtr volmeter);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_volmeter_add_callback(IntPtr volmeter, ObsVolmeterCallback callback, IntPtr param);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_volmeter_remove_callback(IntPtr volmeter, ObsVolmeterCallback callback, IntPtr param);

    // Raw audio taps (obs-source.h)
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern void obs_source_add_audio_capture_callback(IntPtr source, ObsAudioCaptureCallback callback, IntPtr param);
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern void obs_source_remove_audio_capture_callback(IntPtr source, ObsAudioCaptureCallback callback, IntPtr param);

    // Graphics (obs.h, graphics/graphics.h): the screenshot's copy of the canvas
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_add_tick_callback(ObsTickCallback tick, IntPtr param);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_remove_tick_callback(ObsTickCallback tick, IntPtr param);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_enter_graphics();
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_leave_graphics();
    [DllImport(Lib, CallingConvention = Cc)] public static extern void obs_render_main_texture();
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr gs_texrender_create(int format, int zsformat);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_texrender_destroy(IntPtr texrender);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool gs_texrender_begin(IntPtr texrender, uint cx, uint cy);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_texrender_end(IntPtr texrender);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr gs_texrender_get_texture(IntPtr texrender);
    [DllImport(Lib, CallingConvention = Cc)] public static extern IntPtr gs_stagesurface_create(uint width, uint height, int format);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_stagesurface_destroy(IntPtr stagesurf);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_stage_texture(IntPtr dst, IntPtr src);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool gs_stagesurface_map(IntPtr stagesurf, out IntPtr data, out uint linesize);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_stagesurface_unmap(IntPtr stagesurf);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_ortho(float left, float right, float top, float bottom, float znear, float zfar);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_clear(uint flags, ref Vec4 color, float depth, byte stencil);
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_blend_state_push();
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_blend_state_pop();
    [DllImport(Lib, CallingConvention = Cc)] public static extern void gs_blend_function(int src, int dest);

    // Callbacks
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool proc_handler_call(IntPtr handler, [MarshalAs(U8)] string name, ref CallData calldata);
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern void signal_handler_connect(IntPtr handler, [MarshalAs(U8)] string signal, ObsSignalCallback callback, IntPtr data);
    [DllImport(Lib, CallingConvention = Cc)]
    public static extern void signal_handler_disconnect(IntPtr handler, [MarshalAs(U8)] string signal, ObsSignalCallback callback, IntPtr data);
    [DllImport(Lib, CallingConvention = Cc)] [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool calldata_get_string(ref CallData calldata, [MarshalAs(U8)] string name, out IntPtr str);

    public static string? Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    /// <summary>calldata_free from callback/calldata.h (static inline in C).</summary>
    public static void FreeCallData(ref CallData cd)
    {
        if (cd.Fixed == 0 && cd.Stack != IntPtr.Zero)
            bfree(cd.Stack);
        cd = default;
    }
}

/// <summary>Owned obs_data_t handle.</summary>
public sealed class ObsData : IDisposable
{
    public IntPtr Handle { get; private set; } = Obs.obs_data_create();

    public ObsData Set(string name, string value) { Obs.obs_data_set_string(Handle, name, value); return this; }
    public ObsData Set(string name, long value) { Obs.obs_data_set_int(Handle, name, value); return this; }
    public ObsData Set(string name, bool value) { Obs.obs_data_set_bool(Handle, name, value); return this; }
    public ObsData Set(string name, double value) { Obs.obs_data_set_double(Handle, name, value); return this; }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            Obs.obs_data_release(Handle);
        Handle = IntPtr.Zero;
    }
}
