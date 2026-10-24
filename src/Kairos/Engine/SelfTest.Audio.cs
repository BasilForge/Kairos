using System.Diagnostics;
using System.Runtime.InteropServices;
using Kairos.Engine.Audio;
using Kairos.Native;
using Kairos.Settings;

namespace Kairos.Engine;

public static partial class SelfTest
{
    /// <summary>
    /// The tone the process-audio self-test listens for: 7 kHz at -85 dBFS, which can't be heard (a voice device such as
    /// NVIDIA Broadcast's speakers runs at 16 kHz and drops anything above 8 kHz, so the tone stays under that).
    /// </summary>
    const double ToneHz = 7000, ToneDbfs = -85;

    /// <summary>
    /// Kairos.exe --tone &lt;seconds&gt; [device id]: a hidden helper with no window that plays the self-test's tone on the
    /// default output device (or the one given), so its process can be captured by id.
    /// </summary>
    public static unsafe int Tone(string[] args)
    {
        double seconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 6;
        const int rate = 48000;
        float amplitude = (float)Math.Pow(10, ToneDbfs / 20);
        // Its own audio session that Windows does not remember (a mixer setting saved for this exe can't mute it), as an
        // app plays sound (PlaySound would play as "System sounds", which people often mute).
        var enumerator = (CoreAudio.IMMDeviceEnumerator)new CoreAudio.MMDeviceEnumerator();
        CoreAudio.IMMDevice device;
        Marshal.ThrowExceptionForHR(args.Length > 2
            ? enumerator.GetDevice(args[2], out device)
            : enumerator.GetDefaultAudioEndpoint(CoreAudio.Render, CoreAudio.Multimedia, out device));
        Guid iid = typeof(CoreAudio.IAudioClient).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, CoreAudio.ClsctxAll, IntPtr.Zero, out object activated));
        var client = (CoreAudio.IAudioClient)activated;
        IntPtr format = Marshal.AllocHGlobal(18), session = Marshal.AllocHGlobal(16);
        Marshal.WriteInt16(format, 0, 3);
        Marshal.WriteInt16(format, 2, 2);
        Marshal.WriteInt32(format, 4, rate);
        Marshal.WriteInt32(format, 8, rate * 8);
        Marshal.WriteInt16(format, 12, 8);
        Marshal.WriteInt16(format, 14, 32);
        Marshal.WriteInt16(format, 16, 0);
        Marshal.StructureToPtr(Guid.NewGuid(), session, false);
        const uint flags = 0x00080000 /* NOPERSIST */ | 0x80000000 /* AUTOCONVERTPCM */ | 0x08000000 /* SRC_DEFAULT_QUALITY */;
        Marshal.ThrowExceptionForHR(client.Initialize(0, flags, 10_000_000 /* 1 s */, 0, format, session));
        Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint bufferFrames));
        Marshal.ThrowExceptionForHR(client.GetService(typeof(CoreAudio.IAudioRenderClient).GUID, out object service));
        var render = (CoreAudio.IAudioRenderClient)service;
        long total = (long)(rate * seconds), written = 0;
        client.Start();
        while (written < total)
        {
            client.GetCurrentPadding(out uint padding);
            uint free = (uint)Math.Min(bufferFrames - padding, total - written);
            if (free > 0 && render.GetBuffer(free, out IntPtr data) == 0)
            {
                float* f = (float*)data;
                for (uint i = 0; i < free; i++)
                    f[2 * i] = f[2 * i + 1] = amplitude * (float)Math.Sin(2 * Math.PI * ToneHz * (written + i) / rate);
                render.ReleaseBuffer(free, 0);
                written += free;
            }
            Thread.Sleep(50);
        }
        Thread.Sleep(300);
        client.Stop();
        Marshal.FreeHGlobal(format);
        Marshal.FreeHGlobal(session);
        return 0;
    }

    /// <summary>
    /// Kairos.exe --selftest-process-audio: the own process-audio source (by process id, no window) must hear a hidden
    /// helper's tone, at the rate and level it plays, then open other running apps' sound (an app in the tray too)
    /// and say what six such captures cost. Nothing shows; the tone can't be heard.
    /// </summary>
    public static int ProcessAudio(TextWriter output)
    {
        ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, LogDir, new VideoConfig(1920, 1080, 1920, 1080, 30));
        ProcessAudioSource.Register();
        var sources = new List<(IntPtr Source, ObsAudioCaptureCallback Callback)>();
        Process? helper = null;
        string? toneExe = null;
        try
        {
            (helper, toneExe) = StartTone(7);
            var tone = new ToneMeter(ToneHz);
            sources.Add(Listen(helper.Id, "tone", 10, tone));
            Thread.Sleep(5000);
            var (rate, level, other, blocks) = tone.Result();
            output.WriteLine(FormattableString.Invariant(
                $"tone: {rate:F0} frames/s in {blocks} blocks, {ToneHz / 1000:F0} kHz at {level:F1} dBFS (played at {ToneDbfs}), off-tone {other:F1} dBFS"));

            // Other apps, from their process id: one in the tray needs no window.
            uint channel = 11;
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            foreach (string name in new[] { "Discord", "brave", "explorer", "Spotify", "steam" })
            {
                if (Process.GetProcessesByName(name).FirstOrDefault() is not { } p)
                    continue;
                sources.Add(Listen(p.Id, name, channel++, new ToneMeter(1000)));
                output.WriteLine($"opened {name} ({p.Id})");
                p.Dispose();
            }
            Thread.Sleep(10000);
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100;
            output.WriteLine(FormattableString.Invariant($"cpu with {sources.Count} process captures: {cpu:F1}% of one core"));
            bool ok = rate > 46000 && rate < 50000 && level > ToneDbfs - 10 && level < ToneDbfs + 6 && level > other + 20;
            output.WriteLine(ok ? "process audio ok" : "process audio: FAIL");
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            for (uint c = 10; c < 10 + (uint)sources.Count; c++)
                Obs.obs_set_output_source(c, IntPtr.Zero);
            foreach (var (source, callback) in sources)
            {
                Obs.obs_source_remove_audio_capture_callback(source, callback, IntPtr.Zero);
                Obs.obs_source_remove(source);
                Obs.obs_source_release(source);
            }
            ObsHost.DrainDestroyQueue();
            var leaked = ObsHost.AliveSources();
            output.WriteLine($"leaked sources after teardown: {(leaked.Count == 0 ? "none" : string.Join(", ", leaked))}");
            ObsHost.Shutdown();
            StopTone(helper, toneExe);
        }
    }

    /// <summary>
    /// The tone helper, hidden, under its own name (a mixer rule for "Kairos", such as a Stream Deck dial, can't mute it),
    /// playing for <paramref name="seconds"/>; give it a moment to start.
    /// </summary>
    static (Process Helper, string Exe) StartTone(double seconds, string? device = null)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "KairosTone.exe");
        File.Copy(Environment.ProcessPath!, exe, overwrite: true);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "--tone", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), device ?? "" }.Where(a => a.Length > 0))
            start.ArgumentList.Add(arg);
        var helper = Process.Start(start)!;
        helper.PriorityClass = ProcessPriorityClass.BelowNormal;
        Thread.Sleep(700);
        return (helper, exe);
    }

    static void StopTone(Process? helper, string? exe)
    {
        if (helper is { HasExited: false })
            helper.Kill();
        helper?.Dispose();
        if (exe == null)
            return;
        Thread.Sleep(300);
        try
        {
            File.Delete(exe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// --selftest ... --audio separate: the tone must reach "Mix" and "Other sounds" and no other track (read from each
    /// libobs mix before encoding), and the saved clip must carry the track names. With playback devices as rows
    /// (--output-row: the default one, --output-device: any), the tone (on the default device, or --tone-device) must
    /// reach the row of the device it plays on and no other track, and that row's live level and mute must act on it.
    /// </summary>
    static string? SeparateAudio(CapturePipeline pipeline, AppSettings s, TextWriter output)
    {
        var (helper, exe) = StartTone(16, ToneDevice);
        IntPtr audio = Obs.obs_get_audio();
        try
        {
            string playsOn = ToneDevice ?? CoreAudio.DefaultRenderId() ?? "";
            bool anyOutput = s.Audio.Sources.Any(r => r.Kind == AudioSourceKind.Output);
            var toneRow = s.Audio.Sources.FirstOrDefault(r => r.Kind == AudioSourceKind.Output && r.Target == playsOn);
            var (withSound, onOutputs) = CoreAudio.Sound(s.Audio.Sources);
            var assignment = AudioPartition.Assign(App.ProcessList.All(), withSound, new PartitionInput(0, s.Audio.Sources, App.ProcessList.SelfName, onOutputs));
            // A row's live mute and volume reach a source made after them (Other sounds gains processes any time).
            pipeline.ApplyLive(new LiveAudio(1, 1, [new("other", 0.3, true)]));
            pipeline.UpdateAudioTargets(assignment);
            var states = pipeline.OtherSourceStates();
            // (With the default playback device as a row, every app may play there: Other sounds can be empty.)
            if ((states.Count == 0 && !anyOutput) || states.Any(st => !st.Muted || Math.Abs(st.Volume - 0.3) > 0.01))
                return $"a new Other sounds source did not take the live mute/volume: {string.Join(",", states)}";
            pipeline.ApplyLive(LiveAudio.Of(s.Audio));
            output.WriteLine($"rows capturing: {string.Join(", ", pipeline.CapturingRows)}; other sounds: {string.Join(",", assignment.GetValueOrDefault("other", []))}");
            string wantedTrack = toneRow?.Label ?? TrackLayout.FileName(TrackRole.Other);
            string? why = null;
            foreach (var (name, level) in TrackTones(pipeline, audio, 4000))
            {
                output.WriteLine(FormattableString.Invariant($"track {name}: tone {level:F1} dBFS"));
                bool wanted = name == TrackLayout.FileName(TrackRole.Mix) || name == wantedTrack;
                if (wanted && level < ToneDbfs - 12)
                    why ??= $"the tone is missing from {name}";
                if (!wanted && name != TrackLayout.FileName(TrackRole.Microphone) && level > ToneDbfs - 20)
                    why ??= $"the tone leaked into {name}";
            }
            if (why != null || toneRow == null)
                return why;
            // The device row's own level and mute, live: half the volume is 6 dB down, muted is nothing.
            var live = LiveAudio.Of(s.Audio);
            LiveAudio With(double volume, bool muted) => live with { Rows = [.. live.Rows.Select(r => r.Id == toneRow.Id ? r with { Volume = volume, Muted = muted } : r)] };
            pipeline.ApplyLive(With(0.5, false));
            Thread.Sleep(600); // the mix runs a moment behind: the change heard first
            double half = TrackTones(pipeline, audio, 2500)[toneRow.Label];
            pipeline.ApplyLive(With(1, true));
            Thread.Sleep(600);
            double muted = TrackTones(pipeline, audio, 2500)[toneRow.Label];
            pipeline.ApplyLive(live);
            output.WriteLine(FormattableString.Invariant($"track {toneRow.Label}: at half volume {half:F1} dBFS, muted {muted:F1} dBFS"));
            if (Math.Abs(half - (ToneDbfs - 6)) > 2)
                return $"half volume on {toneRow.Label} gave {half:F1} dBFS, not about {ToneDbfs - 6} dBFS";
            return muted > ToneDbfs - 40 ? $"muting {toneRow.Label} left {muted:F1} dBFS" : null;
        }
        finally
        {
            StopTone(helper, exe);
        }
    }

    /// <summary>The tone's level on each track (read from each libobs mix before encoding) over <paramref name="ms"/>.</summary>
    static Dictionary<string, double> TrackTones(CapturePipeline pipeline, IntPtr audio, int ms)
    {
        var meters = new List<(int Mixer, string Name, ToneMeter Meter, AudioOutputCallback Callback)>();
        try
        {
            foreach (var track in pipeline.TrackNames.Select((name, i) => (name, i)))
            {
                var meter = new ToneMeter(ToneHz);
                AudioOutputCallback callback = (_, _, data) => meter.Add(data);
                Obs.audio_output_connect(audio, (nuint)track.i, IntPtr.Zero, callback, IntPtr.Zero);
                meters.Add((track.i, track.name, meter, callback));
            }
            Thread.Sleep(ms);
            return meters.ToDictionary(m => m.Name, m => m.Meter.Result().Tone);
        }
        finally
        {
            foreach (var (mixer, _, _, callback) in meters)
                Obs.audio_output_disconnect(audio, (nuint)mixer, callback, IntPtr.Zero);
        }
    }

    /// <summary>The device the separate-sources tone plays on (--tone-device), or null: the default one.</summary>
    static string? ToneDevice;

    /// <summary>A playback device as a row of its own track, named "Output 1", "Output 2"...</summary>
    static void AddOutputRow(AppSettings s, string device)
    {
        int n = s.Audio.Sources.Count(r => r.Kind == AudioSourceKind.Output) + 1;
        s.Audio.Sources.Add(new AudioSource { Id = $"output:test{n}", Kind = AudioSourceKind.Output, Target = device, Label = $"Output {n}", OwnTrack = true });
    }

    static (IntPtr, ObsAudioCaptureCallback) Listen(int pid, string name, uint channel, ToneMeter meter)
    {
        using var d = new ObsData().Set("pid", (long)pid);
        IntPtr source = Obs.obs_source_create(ProcessAudioSource.Id, name, d.Handle, IntPtr.Zero);
        if (source == IntPtr.Zero)
            throw new InvalidOperationException($"could not create the process audio source for {name}");
        ObsAudioCaptureCallback callback = (_, _, audio, _) => meter.Add(audio);
        Obs.obs_source_add_audio_capture_callback(source, callback, IntPtr.Zero);
        Obs.obs_set_output_source(channel, source);
        return (source, callback);
    }

    /// <summary>Counts the frames libobs hands over and measures one frequency (Goertzel) on the left channel.</summary>
    sealed class ToneMeter(double hz)
    {
        readonly object _gate = new();
        readonly Stopwatch _clock = new();
        long _frames;
        int _blocks;
        double _tone, _all;

        public unsafe void Add(IntPtr audioData)
        {
            var data = (ObsAudioData*)audioData;
            int n = (int)data->Frames;
            float* left = (float*)data->Data0;
            if (n == 0 || left == null)
                return;
            double k = 2 * Math.Cos(2 * Math.PI * hz / 48000), s1 = 0, s2 = 0, energy = 0;
            for (int i = 0; i < n; i++)
            {
                double s = left[i] + k * s1 - s2;
                s2 = s1;
                s1 = s;
                energy += left[i] * (double)left[i];
            }
            double power = s1 * s1 + s2 * s2 - k * s1 * s2; // |X|^2
            lock (_gate)
            {
                if (_blocks++ == 0)
                    _clock.Start();
                _frames += n;
                _tone += 4 * power / ((double)n * n) / 2; // the tone's mean square (amplitude^2 / 2)
                _all += energy / n;
            }
        }

        /// <summary>Frames per second, the tone's level and everything else's level (dBFS of a sine's peak), and the blocks.</summary>
        public (double Rate, double Tone, double Other, int Blocks) Result()
        {
            lock (_gate)
            {
                if (_blocks < 2)
                    return (0, -200, -200, _blocks);
                double tone = _tone / _blocks, all = _all / _blocks;
                static double Db(double meanSquare) => meanSquare <= 0 ? -200 : 10 * Math.Log10(meanSquare * 2);
                return (_frames / _clock.Elapsed.TotalSeconds, Db(tone), Db(Math.Max(0, all - tone)), _blocks);
            }
        }
    }
}
