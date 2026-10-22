namespace Kairos.Engine;

/// <summary>
/// The encoders that could not start this session (an AMD driver without OpenCL fails every AMF encoder): each one is
/// left out of the next pick, down <see cref="EncoderCatalog.Pick"/>'s order to x264, which runs anywhere.
/// </summary>
public sealed class EncoderFallback
{
    readonly List<string> _excluded;
    readonly List<string> _noBFrames;

    public EncoderFallback() : this([], [])
    {
    }

    /// <summary>
    /// What failed on this PC before (kept with its hardware), so it is not tried again at each start. A failure this run
    /// is left out for this run only (the keeper decides what it keeps); B-frame failures go into the keeper's list.
    /// </summary>
    public EncoderFallback(IEnumerable<string> failed, List<string> noBFrames)
    {
        _excluded = [.. failed];
        _noBFrames = noBFrames;
    }

    /// <summary>The encoders that failed with B-frames.</summary>
    public IReadOnlyCollection<string> WithoutBFrames => _noBFrames;

    /// <summary>The encoder failed with B-frames once: it is tried again without them.</summary>
    public bool NoBFrames(string id) => _noBFrames.Contains(id);

    /// <summary>The ids that failed, in order.</summary>
    public IReadOnlyList<string> Excluded => _excluded;

    /// <summary>The encoder ids still worth picking from.</summary>
    public IReadOnlyList<string> Usable(IReadOnlyList<string> ids) => [.. ids.Where(id => !_excluded.Contains(id))];

    /// <summary>
    /// Notes that <paramref name="id"/> could not start: true when another try is worth it, first the same encoder without
    /// B-frames when it had them (an NVENC card before Turing), then the next encoder.
    /// </summary>
    public bool Failed(string id, bool withBFrames = false)
    {
        if (withBFrames && !_noBFrames.Contains(id))
        {
            _noBFrames.Add(id);
            return true;
        }
        if (id == EncoderCatalog.X264 || _excluded.Contains(id))
            return false;
        _excluded.Add(id);
        return true;
    }
}

/// <summary>
/// A sound device libobs could not open, read from its log lines: WASAPI refuses a Bluetooth headset in hands-free
/// mode (0x88890008) and libobs retries every 3 s for ever, in silence.
/// </summary>
public sealed class AudioDeviceTrouble
{
    const string System = "(source: Desktop audio)";

    /// <summary>The system-sound device that could not start (its name once libobs says it), or null while it runs.</summary>
    public string? SystemDevice { get; private set; }

    /// <summary>A libobs log line: true when it says the system-sound device failed.</summary>
    public bool Note(string line)
    {
        if (!line.EndsWith(System, StringComparison.Ordinal))
            return false;
        if (line.StartsWith("WASAPI: Device '", StringComparison.Ordinal) && line.Contains("failed to start", StringComparison.Ordinal))
        {
            SystemDevice = Quoted(line);
            return true;
        }
        if (line.StartsWith("Device '", StringComparison.Ordinal) && line.Contains("invalidated", StringComparison.Ordinal) && SystemDevice != null)
        {
            SystemDevice = Quoted(line);
            return true;
        }
        if (line.Contains("] initialized", StringComparison.Ordinal))
            SystemDevice = null;
        return false;
    }

    static string? Quoted(string line)
    {
        int a = line.IndexOf('\''), b = a < 0 ? -1 : line.IndexOf('\'', a + 1);
        return a >= 0 && b > a ? line[(a + 1)..b] : null;
    }
}
