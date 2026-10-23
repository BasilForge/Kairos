using System.Runtime.InteropServices;
using static Kairos.Engine.Audio.CoreAudio;

namespace Kairos.Engine.Audio;

/// <summary>
/// One process's own sound (its child processes' too) from Windows process loopback: by process id, so it needs no
/// window and injects nothing. Captures on its own thread in 7.1, folds it to 48 kHz float stereo and hands each packet
/// to <see cref="Packet"/> without allocating.
/// </summary>
public sealed class ProcessLoopback : IDisposable
{
    public const int SampleRate = 48000, Channels = 2;

    /// <summary>
    /// The sound is asked for in 7.1 (front left and right, centre, LFE, back and side pairs) and folded here: asked for
    /// stereo, Windows folded an app playing to a 7.1 device (SteelSeries Sonar's Gaming and Media) to a fifth of its
    /// level (-98.5 dB from -85, measured 2026-10-21); in 7.1 a stereo app comes as it plays, on any device.
    /// </summary>
    const int Surround = 8, SurroundMask = 0x63F;

    /// <summary>A packet: interleaved float samples, frame count, and its time in ns on libobs's clock (QPC).</summary>
    public delegate void PacketHandler(IntPtr data, uint frames, ulong timestampNs);

    readonly uint _pid;
    readonly PacketHandler _packet;
    readonly Thread _thread;
    readonly ManualResetEventSlim _stop = new();

    /// <summary>Why the capture could not start or stopped (the device went away), or null while it runs (or has not tried yet).</summary>
    public string? Error { get; private set; }

    public ProcessLoopback(uint pid, PacketHandler packet)
    {
        _pid = pid;
        _packet = packet;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Process audio {pid}", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    void Run()
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr stereo = IntPtr.Zero;
        using var ready = new AutoResetEvent(false);
        try
        {
            client = Activate(_pid, _stop);
            IntPtr format = Marshal.AllocHGlobal(40);                       // WAVEFORMATEXTENSIBLE
            try
            {
                Marshal.WriteInt16(format, 0, unchecked((short)0xFFFE));       // WAVE_FORMAT_EXTENSIBLE
                Marshal.WriteInt16(format, 2, Surround);
                Marshal.WriteInt32(format, 4, SampleRate);
                Marshal.WriteInt32(format, 8, SampleRate * Surround * 4);      // bytes per second
                Marshal.WriteInt16(format, 12, Surround * 4);                  // block align
                Marshal.WriteInt16(format, 14, 32);
                Marshal.WriteInt16(format, 16, 22);                            // the extension's size
                Marshal.WriteInt16(format, 18, 32);                            // valid bits
                Marshal.WriteInt32(format, 20, SurroundMask);
                Marshal.StructureToPtr(new Guid("00000003-0000-0010-8000-00aa00389b71"), format + 24, false); // IEEE float
                const uint flags = 0x00020000 /* LOOPBACK */ | 0x00040000 /* EVENTCALLBACK */;
                Check(client.Initialize(0 /* shared */, flags, 1_000_000 /* 100 ms */, 0, format, IntPtr.Zero), "Initialize");
            }
            finally
            {
                Marshal.FreeHGlobal(format);
            }
            Check(client.GetBufferSize(out uint bufferFrames), "GetBufferSize");
            stereo = Marshal.AllocHGlobal((int)Math.Max(bufferFrames, (uint)SampleRate) * Channels * 4);
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            Check(client.GetService(typeof(IAudioCaptureClient).GUID, out object service), "GetService");
            capture = (IAudioCaptureClient)service;
            Check(client.Start(), "Start");

            var waits = new WaitHandle[] { _stop.WaitHandle, ready };
            while (WaitHandle.WaitAny(waits, 200) != 0)
            {
                int hr;
                while ((hr = capture.GetNextPacketSize(out uint next)) == 0 && next > 0)
                {
                    if ((hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint bufferFlags, out _, out ulong qpc)) != 0)
                        break;
                    ulong ts = qpc != 0 ? qpc * 100 : Native.Obs.os_gettime_ns() - (ulong)frames * 1_000_000_000UL / SampleRate;
                    if ((bufferFlags & 0x2 /* SILENT */) != 0)
                        NativeMemoryClear(stereo, (int)frames * Channels * 4);
                    else
                        Fold(data, stereo, frames);
                    _packet(stereo, frames, ts);
                    capture.ReleaseBuffer(frames);
                }
                // The device went away (the audio service restarted, the device changed): said, so the source starts again.
                Check(hr, "GetNextPacketSize");
            }
            client.Stop();
        }
        catch (OperationCanceledException)
        {
            // Stopped while Windows was still activating it.
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or TimeoutException)
        {
            Error = e.Message;
            ObsLog.Warn($"process audio {_pid}: {e.Message}");
        }
        finally
        {
            if (capture != null)
                Marshal.ReleaseComObject(capture);
            if (client != null)
                Marshal.ReleaseComObject(client);
            if (stereo != IntPtr.Zero)
                Marshal.FreeHGlobal(stereo);
        }
    }

    static unsafe void Fold(IntPtr surround, IntPtr stereo, uint frames) =>
        Downmix(new ReadOnlySpan<float>((void*)surround, (int)frames * Surround), new Span<float>((void*)stereo, (int)frames * Channels));

    /// <summary>
    /// 7.1 to stereo as ITU-R BS.775 folds it (centre and the back and side pairs at -3 dB, LFE left out), not scaled
    /// down (as ffmpeg and mpv fold float): a stereo app keeps its level.
    /// </summary>
    internal static void Downmix(ReadOnlySpan<float> surround, Span<float> stereo)
    {
        const float side = 0.70710677f;
        for (int i = 0, o = 0; i + Surround <= surround.Length && o + 1 < stereo.Length; i += Surround, o += Channels)
        {
            float centre = side * surround[i + 2];
            stereo[o] = surround[i] + centre + side * (surround[i + 4] + surround[i + 6]);
            stereo[o + 1] = surround[i + 1] + centre + side * (surround[i + 5] + surround[i + 7]);
        }
    }

    static unsafe void NativeMemoryClear(IntPtr p, int bytes) => new Span<byte>((void*)p, bytes).Clear();

    static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    /// <summary>The process's audio client: process loopback "include the process tree", activated asynchronously.</summary>
    static IAudioClient Activate(uint pid, ManualResetEventSlim stop)
    {
        IntPtr parameters = Marshal.AllocHGlobal(12), variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(parameters, 0, 1);         // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(parameters, 4, (int)pid);
            Marshal.WriteInt32(parameters, 8, 0);         // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
            for (int i = 0; i < 24; i += 8)
                Marshal.WriteInt64(variant, i, 0);
            Marshal.WriteInt16(variant, 0, 65);           // VT_BLOB
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 16, parameters);
            var handler = new ActivationHandler();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, variant, handler, out var operation);
            // A stop (the row moved to another process) does not wait out Windows' answer: Dispose waits for this thread.
            int answered = WaitHandle.WaitAny([stop.WaitHandle, handler.Done.WaitHandle], TimeSpan.FromSeconds(5));
            if (answered == 0)
                throw new OperationCanceledException();
            if (answered == WaitHandle.WaitTimeout)
                throw new TimeoutException("process loopback did not answer");
            Marshal.ReleaseComObject(operation);
            Check(handler.Result, "Activate");
            return (IAudioClient)handler.Client!;
        }
        finally
        {
            Marshal.FreeHGlobal(parameters);
            Marshal.FreeHGlobal(variant);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        // A thread still in a call to Windows keeps its stop signal (it ends on its own; disposing it under it would throw).
        if (_thread.Join(2000))
            _stop.Dispose();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Result;
        public object? Client;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            operation.GetActivateResult(out Result, out Client);
            Done.Set();
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject;
}
