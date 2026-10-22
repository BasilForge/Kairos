using Kairos.App;
using Kairos.Settings;

namespace Kairos.Engine.Audio;

/// <summary>
/// The first time Kairos sees a sound mixer that splits the PC's sound (SteelSeries Sonar, GoXLR), it sets the sound up
/// for it once, with nothing to choose: separate sources (each app from itself: all of it, once, whatever the mixer
/// does), for the first mixer ever, and the voice alone as the microphone when that is the mixer's whole mix (GoXLR's
/// Broadcast Stream Mix). Never again: a later choice is the person's own, and the Audio page says what it would miss or
/// record twice.
/// </summary>
public static class MixerSetup
{
    /// <summary>What was done, a line per mixer seen the first time (for the log, no device names); empty when none was new.</summary>
    /// <param name="defaultMic">The device the microphone's "Default" opens.</param>
    /// <param name="separateSources">Whether this Windows gives an app's own sound (separate sources can run).</param>
    public static List<string> FirstTime(AppSettings settings, IReadOnlyList<NameValue> speakers, IReadOnlyList<NameValue> mics, string? defaultMic,
        bool separateSources)
    {
        var audio = settings.Audio;
        var done = new List<string>();
        var mixers = speakers.Concat(mics).Select(d => VirtualAudio.Classify(d.Name))
            .Where(d => d is { MixOnYourDevice: true } or { MixOnARecordingDevice: true }).Select(d => d!.Brand);
        foreach (string mixer in mixers.Except(audio.MixersSetUp))
        {
            var changes = new List<string>();
            // Once ever: all together chosen after the first mixer stays, a second mixer later too.
            if (audio.MixersSetUp.Count == 0 && audio.Layout == AudioLayout.Together && separateSources)
            {
                audio.Layout = AudioLayout.Separate;
                changes.Add("separate sources (was all together)");
            }
            if (VirtualAudio.MicOnAWholeMix(mics, audio.MicDevice, defaultMic) is { Voice: { } voice } whole && whole.Mixer == mixer)
            {
                audio.MicDevice = voice.Value;
                changes.Add("the microphone its voice alone (was its whole mix)");
            }
            audio.MixersSetUp.Add(mixer);
            done.Add($"{mixer} seen the first time: {(changes.Count == 0 ? "the sound was set up for it already" : string.Join(", ", changes))}");
        }
        if (done.Count > 0)
            settings.Normalize(); // separate sources' built-in rows
        return done;
    }
}
