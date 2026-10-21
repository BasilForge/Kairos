using System.Diagnostics;

namespace System.Media;

/// <summary>The part of SoundPlayer the chime and shutter sounds use, played through PulseAudio/PipeWire's paplay.</summary>
public sealed class SoundPlayer
{
    readonly string _file = Path.Combine(Path.GetTempPath(), $"kairos-{Environment.ProcessId}-{Guid.NewGuid():N}.wav");

    public SoundPlayer(Stream wav)
    {
        using var file = File.Create(_file);
        wav.CopyTo(file);
        Stream = wav;
    }

    public Stream? Stream { get; }

    /// <summary>Plays asynchronously on the default output device; a missing paplay means no sound, not an error.</summary>
    public void Play()
    {
        try
        {
            Process.Start(new ProcessStartInfo("paplay", _file) { UseShellExecute = false, RedirectStandardError = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
