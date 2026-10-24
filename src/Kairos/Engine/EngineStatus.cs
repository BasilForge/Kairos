using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>What Automatic records with: the encoder, the GPU (or the CPU) it runs on, the picture's height, the rate and quality.</summary>
public sealed record AutoPick(string Encoder, string Device, int Height, int Fps, QualityPreset Quality);

public sealed record EngineStatus(
    bool BufferActive,
    bool Recording,
    double Fps,
    uint LaggedFrames,
    uint SkippedFrames,
    uint TotalFrames,
    int BufferSeconds,
    int BufferTargetSeconds,
    string Encoder,
    int Width,
    int Height,
    long WorkingSetMb,
    int EstimatedBufferMb,
    string ClipsFolder,
    bool FolderFellBack,
    string? LastError,
    bool RecordingPaused = false,
    double RecordingSeconds = 0,
    long RecordingBytes = 0,
    string? CaptureNotice = null,
    /// <summary>Separate sources: the rows this engine captures that hear nothing now (no game, the app closed). Only
    /// rows it has: a row chosen on the page but not saved yet, or any row while sound is recorded together, is not one.</summary>
    IReadOnlyList<string>? QuietAudioRows = null,
    /// <summary>The codec the user chose when another encoder records instead (its encoder could not start), else null.</summary>
    string? EncoderFallback = null,
    /// <summary>The system-sound device Windows refuses, else null.</summary>
    string? AudioDeviceTrouble = null,
    /// <summary>Automatic: what it records with; null when the user chose.</summary>
    AutoPick? Auto = null,
    /// <summary>NVENC called the NVIDIA driver outdated: the note's key, else null.</summary>
    string? NvidiaNote = null,
    /// <summary>The camera is on (its light too): the Status page's mark.</summary>
    bool CameraOn = false,
    /// <summary>The replay was asked for on the disk but is in RAM (no room, the drive missing, a write failed).</summary>
    bool DiskFellBack = false,
    /// <summary>How much lighter it records because it fell behind a game (<see cref="LagRelief"/>; 0: as chosen).</summary>
    int Relief = 0,
    /// <summary>Still falling behind at the lightest it can go: the user can cap the game's frame rate.</summary>
    bool Behind = false,
    /// <summary>
    /// Pictures libobs rendered this session, what <see cref="LaggedFrames"/> is out of (both run for the whole session;
    /// <see cref="SkippedFrames"/> and <see cref="TotalFrames"/> are the encoder's, each pipeline's own).
    /// </summary>
    uint RenderedFrames = 0,
    /// <summary>Separate sources: the quiet rows whose app plays on a device added as a row, heard there (row id, device id).</summary>
    IReadOnlyDictionary<string, string>? AudioRowsHeardOn = null)
{
    /// <summary>The four counters as the lag guard reads them.</summary>
    public FrameCount Frames => new(LaggedFrames, RenderedFrames, SkippedFrames, TotalFrames);
}
