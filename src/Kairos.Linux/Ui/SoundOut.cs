using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Kairos.Ui;

/// <summary>
/// Plays 16-bit stereo sound on the default output through PulseAudio's simple API (PipeWire answers it too), and says how much
/// has been heard (the preview player's clock). Pieces queue until <see cref="Start"/>; a thread hands them to the server.
/// Same surface as the Windows class, which uses waveOut.
/// </summary>
sealed class SoundOut : IDisposable
{
    const string Lib = "libpulse-simple.so.0";

    readonly IntPtr _stream;
    readonly int _rate, _bytesPerFrame;
    readonly bool _mute;
    readonly BlockingCollection<byte[]> _queue = new();
    readonly object _lock = new();
    readonly ManualResetEventSlim _started = new(false);
    readonly Thread _writer;
    long _queuedBytes, _sentBytes;
    volatile bool _disposed;

    /// <param name="mute">Plays silence instead (self-tests on a PC someone is using).</param>
    public SoundOut(int sampleRate, int channels, bool mute = false)
    {
        _rate = sampleRate;
        _bytesPerFrame = channels * 2;
        _mute = mute;
        var spec = new SampleSpec { Format = 3 /* PA_SAMPLE_S16LE */, Rate = (uint)sampleRate, Channels = (byte)channels };
        _stream = pa_simple_new(IntPtr.Zero, "Kairos", 1 /* PA_STREAM_PLAYBACK */, null, "Clip preview", ref spec, IntPtr.Zero, IntPtr.Zero, out int error);
        if (_stream == IntPtr.Zero)
            throw new InvalidOperationException($"No sound output (PulseAudio error {error}).");
        _writer = new Thread(Run) { IsBackground = true, Name = "Kairos sound" };
        _writer.Start();
    }

    /// <summary>Scales 16-bit samples in place by <paramref name="gain"/> (0 = silence, 1 = as they are, 2 = twice as loud,
    /// held at the loudest a sample can be instead of wrapping round).</summary>
    public static void Scale(Span<byte> pcm, double gain)
    {
        if (gain == 1)
            return;
        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        if (gain <= 0)
        {
            samples.Clear();
            return;
        }
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
    }

    /// <summary>Queues a piece of sound (copied, so the buffer can be reused at once), at <paramref name="gain"/>.</summary>
    public void Write(ReadOnlySpan<byte> pcm, double gain = 1)
    {
        if (pcm.IsEmpty || _disposed)
            return;
        var copy = pcm.ToArray();
        if (_mute)
            Array.Clear(copy);
        else
            Scale(copy, gain);
        lock (_lock)
            _queuedBytes += copy.Length;
        try
        {
            _queue.Add(copy);
        }
        catch (InvalidOperationException)
        {
            // Closed meanwhile.
        }
    }

    public void Start() => _started.Set();

    /// <summary>Seconds of sound heard so far: what the server has been given, less what it still holds back.</summary>
    public double PlayedSeconds
    {
        get
        {
            if (_disposed || !_started.IsSet)
                return 0;
            long sent;
            lock (_lock)
                sent = _sentBytes;
            double latency = pa_simple_get_latency(_stream, out _) / 1e6;
            return Math.Max(0, sent / (double)(_rate * _bytesPerFrame) - latency);
        }
    }

    /// <summary>Seconds of sound written so far.</summary>
    public double WrittenSeconds
    {
        get
        {
            lock (_lock)
                return _queuedBytes / (double)(_rate * _bytesPerFrame);
        }
    }

    void Run()
    {
        _started.Wait();
        try
        {
            foreach (byte[] piece in _queue.GetConsumingEnumerable())
            {
                if (_disposed)
                    break;
                if (pa_simple_write(_stream, piece, (nuint)piece.Length, out _) < 0)
                    break;
                lock (_lock)
                    _sentBytes += piece.Length;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queue.CompleteAdding();
        _started.Set(); // a writer still waiting for Start goes on to see it is over
        _writer.Join(1000);
        pa_simple_free(_stream);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [DllImport(Lib)] static extern IntPtr pa_simple_new(IntPtr server, string name, int direction, string? device, string streamName, ref SampleSpec spec,
        IntPtr map, IntPtr attr, out int error);
    [DllImport(Lib)] static extern int pa_simple_write(IntPtr s, byte[] data, nuint bytes, out int error);
    [DllImport(Lib)] static extern ulong pa_simple_get_latency(IntPtr s, out int error);
    [DllImport(Lib)] static extern void pa_simple_free(IntPtr s);
}
