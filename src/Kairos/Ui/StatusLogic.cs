using System.Globalization;
using Kairos.App;

namespace Kairos.Ui;

/// <summary>What the Status page says: the instant replay card and the recording card.</summary>
public static class StatusLogic
{
    /// <summary>The instant replay card: offline | error | starting | paused | off | waiting | active. A recording has its own card.</summary>
    public static string ReplayState(StatusDto? status, bool replayEnabled)
    {
        if (status == null)
            return "offline";
        var e = status.Engine;
        if (status.Error != null && e?.BufferActive != true && e?.Recording != true)
            return "error";
        if (e == null)
            return "starting";
        if (!replayEnabled)
            return "off"; // a plain recorder: nothing kept in the background
        if (status.PausedByUser)
            return "paused";
        return status.WaitingForGame ? "waiting" : "active";
    }

    /// <summary>Why the full screen is recorded instead of the chosen capture, or "" (the Status page's notice).</summary>
    /// <summary>The Quality page's line under Automatic: what it records with, and why not AV1 or a graphics card.</summary>
    public static string AutoLine(Engine.AutoPick auto)
    {
        var choice = Engine.EncoderCatalog.ById(auto.Encoder);
        string why = choice.Vendor == Engine.EncoderVendor.Software ? " " + Text.Get("quality.autoCpu")
            : choice.Codec != Settings.VideoCodec.Av1 ? " " + Text.Get("quality.autoNoAv1") : "";
        return Text.Format("quality.autoNow", Text.Ltr(Engine.EncoderCatalog.Describe(choice)), Text.Ltr(auto.Device), $"{auto.Height}p", auto.Fps,
            Text.Get($"quality.{auto.Quality.ToString().ToLowerInvariant()}")) + why;
    }

    public static string CaptureWarning(Engine.EngineStatus? engine) => engine switch
    {
        { CaptureNotice: { } key } => Text.Get(key),
        { NvidiaNote: { } nvidia } => Text.Get(nvidia),
        { DiskFellBack: true } => Text.Get("status.diskReplayFailed"),
        { EncoderFallback: { } wanted, Encoder: { } id } => Text.Format("status.encoderFallback", Text.Ltr(Engine.EncoderCatalog.Describe(Engine.EncoderCatalog.ById(id))), Text.Ltr(wanted)),
        { AudioDeviceTrouble: { } device } => Text.Format("status.audioDevice", device),
        { Behind: true } => Text.Get("status.behind"),
        { Relief: > 0 } lighter => Text.Get(Engine.LagRelief.TextKey(lighter.Relief)),
        _ => "",
    };

    /// <summary>
    /// The line under the instant replay title: what to try when it could not start (in the user's language; libobs's own
    /// words go to the log), or how to turn it on (the hotkeys sit in the buttons).
    /// </summary>
    public static string ReplayDetail(string state) => state switch
    {
        "error" => Text.Get("fix.closeRecorders"),
        "off" => Text.Get("status.offHint"),
        _ => "",
    };

    /// <summary>The recording card: idle | recording | paused.</summary>
    public static string RecordState(StatusDto? status) => status?.Engine is { Recording: true } e
        ? e.RecordingPaused ? "paused" : "recording"
        : "idle";

    /// <summary>"12:34 • 1.2 GB": how long the recording is (without its pauses) and how big so far.</summary>
    public static string RecordingDetail(double seconds, long bytes) =>
        Text.Ltr($"{Text.Duration((int)seconds)} • {Size(bytes)}");

    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : $"{bytes >> 20} MB";
}
