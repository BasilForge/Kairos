using System.Text.Json;
using Kairos.Engine;

namespace Kairos.App;

/// <summary>
/// What the engine learnt about this PC's encoders (kept in hardware.json beside the settings, the engine's own file):
/// the encoders that could not start, those that failed with B-frames, how far "Automatic" stepped down for skipped
/// frames, and the NVIDIA driver that NVENC called outdated. All of it is forgotten when the hardware, the driver or the
/// screen changes (<see cref="HardwareProfile.Fingerprint"/>).
/// </summary>
public sealed class HardwareState
{
    public string Fingerprint { get; set; } = "";
    /// <summary>Encoders that failed in two runs: left out from the start.</summary>
    public List<string> Failed { get; set; } = [];
    /// <summary>Encoders that failed once (a driver reset or a full GPU passes): tried again at the next start.</summary>
    public List<string> Suspect { get; set; } = [];
    public List<string> NoBFrames { get; set; } = [];
    public int AutoStep { get; set; }
    /// <summary>When Automatic last stepped down (or a step was given back): a quiet week gives one step back.</summary>
    public DateTime? AutoStepAt { get; set; }
    public string? NvidiaOutdatedDriver { get; set; }
    /// <summary>How much lighter recording runs because it fell behind a game (<see cref="Engine.LagRelief"/>; 0: as chosen).</summary>
    public int Relief { get; set; }
    /// <summary>When recording was last made lighter (or eased): a quiet week eases it one step.</summary>
    public DateTime? ReliefAt { get; set; }
    /// <summary>The step numbers <see cref="Relief"/> was kept under (<see cref="Engine.LagRelief.Numbering"/>).</summary>
    public int ReliefNumbering { get; set; }
    /// <summary>The screen capture is black here with Automatic and shows with Windows Graphics Capture (<see cref="Engine.ScreenCheck"/>).</summary>
    public bool ScreenWgc { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Its file, beside settings.json.</summary>
    public const string FileName = "hardware.json";

    /// <summary>The state kept for <paramref name="fingerprint"/>; a fresh one for new hardware.</summary>
    public static HardwareState Load(string path, string fingerprint)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<HardwareState>(File.ReadAllText(path), Options) is { } kept)
            {
                if (kept.Fingerprint == fingerprint)
                {
                    // A step kept by a Kairos that numbered them otherwise means another step now: from none again.
                    if (kept.Relief > 0 && kept.ReliefNumbering != Engine.LagRelief.Numbering)
                    {
                        ObsLog.Info($"lighter recording: relief {kept.Relief} was kept by an older Kairos: as chosen again");
                        kept.ChoseVideo();
                    }
                    return kept;
                }
                // New hardware or driver: what failed before may work now; an outdated driver stays known for the restart note.
                return new HardwareState { Fingerprint = fingerprint, NvidiaOutdatedDriver = kept.NvidiaOutdatedDriver };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            ObsLog.Warn($"hardware state: {e.Message}");
        }
        return new HardwareState { Fingerprint = fingerprint };
    }

    public void Save(string path)
    {
        try
        {
            // Through a .tmp file, as settings.json: a crash mid-write never leaves half a file.
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Options));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn($"hardware state: {e.Message}");
        }
    }

    /// <summary>An encoder could not start: suspected the first time, left out for good the second (another run).</summary>
    public void NoteFailure(string id)
    {
        if (Failed.Contains(id))
            return;
        if (Suspect.Remove(id))
            Failed.Add(id);
        else
            Suspect.Add(id);
    }

    /// <summary>An encoder started: no longer suspected. True when that changed something to keep.</summary>
    public bool NoteWorked(string id) => Suspect.Remove(id);

    /// <summary>Automatic: one step lighter (quality, then frame rate, then size).</summary>
    public void StepDown(DateTime now)
    {
        AutoStep++;
        AutoStepAt = now;
    }

    /// <summary>A week without a step down: one step back (the game or the PC may have changed). True when it did.</summary>
    public bool StepUpIfQuiet(DateTime now)
    {
        (int step, DateTime? at, bool eased) = BackIfQuiet(AutoStep, AutoStepAt, now);
        (AutoStep, AutoStepAt) = (step, at);
        return eased;
    }

    /// <summary>Recording fell behind a game: lighter, to step <paramref name="to"/> (<see cref="Engine.LagRelief.Next"/>).</summary>
    public void Relieve(DateTime now, int to)
    {
        Relief = to;
        ReliefAt = now;
        ReliefNumbering = Engine.LagRelief.Numbering;
    }

    /// <summary>A week without falling behind: one step closer to what the user chose. True when it did.</summary>
    public bool EaseIfQuiet(DateTime now)
    {
        (int step, DateTime? at, bool eased) = BackIfQuiet(Relief, ReliefAt, now);
        (Relief, ReliefAt) = (step, at);
        return eased;
    }

    /// <summary>The user chose new video settings: recorded as chosen again.</summary>
    public void ChoseVideo()
    {
        Relief = 0;
        ReliefAt = null;
    }

    /// <summary>One step back after a quiet week (a step without a date starts its week now).</summary>
    static (int Step, DateTime? At, bool Eased) BackIfQuiet(int step, DateTime? at, DateTime now) =>
        step == 0 ? (step, at, false)
        : at is not { } since ? (step, now, false)
        : now - since < TimeSpan.FromDays(7) ? (step, at, false)
        : (step - 1, now, true);

    /// <summary>
    /// NVENC said the NVIDIA driver is too old: "update it" the first time, "restart the PC" once the driver changed
    /// (a driver update needs a restart before NVENC takes it).
    /// </summary>
    public string NvidiaNote(string? driverNow)
    {
        bool updated = NvidiaOutdatedDriver != null && driverNow != null && NvidiaOutdatedDriver != driverNow;
        if (!updated)
            NvidiaOutdatedDriver = driverNow;
        return updated ? "status.nvidiaRestart" : "status.nvidiaDriver";
    }
}
