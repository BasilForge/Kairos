namespace Kairos.Engine.Audio;

public enum VirtualRole { Game, Chat, Media, System }

/// <summary>
/// A virtual sound device a mixer app makes (one per kind of sound), which kind it carries, and where the whole mix is:
/// played on the headphones or speakers you listen on (SteelSeries Sonar: Windows can record that device), or only on
/// a recording device (GoXLR's Broadcast Stream Mix), so no playback device has all of it.
/// </summary>
public sealed record VirtualDevice(string Brand, VirtualRole Role, bool MixOnYourDevice = false, bool MixOnARecordingDevice = false)
{
    /// <summary>Whether recording it as the system sound misses the game (a chat or media device).</summary>
    public bool MissesTheGame => Role is VirtualRole.Chat or VirtualRole.Media;
}

/// <summary>
/// Sound apps that split the PC's sound into virtual devices (SteelSeries Sonar, GoXLR, Elgato Wave Link, Logitech
/// MIXLINE, Voicemeeter, NVIDIA Broadcast) told apart by name, so the system sound is recorded from the device the game plays to: a game
/// on "Sonar - Gaming" is not heard on "Sonar - Chat".
/// </summary>
public static class VirtualAudio
{
    /// <summary>
    /// A mixer app: the word its devices' names carry (written without spaces, matched with spaces and case aside: Windows
    /// names a GoXLR Mini "GO XLR MINI"), its name, its own exe when that exe plays everyone's sound again, mixed
    /// (capturing it records every other app a second time, a moment late), whether it plays all it mixes on your own
    /// headphones or speakers, and the words of the recording devices that carry its whole mix and the voice alone.
    /// </summary>
    sealed record Mixer(string[] Match, string Brand, string? App = null, bool MixOnYourDevice = false, string[]? WholeMix = null, string? Voice = null);

    /// <summary>
    /// GoXLR mixes in the hardware: its playback devices are the mixer's inputs and its mix comes back as the recording
    /// device Broadcast Stream Mix (and VOD: all but the music), the voice alone as Chat Mic (its manual; GoXLR Utility's
    /// routing), so no exe plays it again; its app only drives the hardware. RØDECaster Duo and Pro II mix in the hardware
    /// too, their mix back as the recording device Main Stereo. Wave Link plays its Monitor Mix on your headphones and
    /// sends its Stream Mix as a recording device. Logitech MIXLINE likewise: its one playback device ("Speakers
    /// (MIXLINE)") played again on the headset by MIXLINE.exe, its mixes as the recording devices MIXLINE Stream and Record.
    /// </summary>
    static readonly Mixer[] Mixers =
    [
        new(["Sonar"], "SteelSeries Sonar", App: "SteelSeriesSonar", MixOnYourDevice: true),
        new(["GoXLR"], "GoXLR", App: "GoXLR", WholeMix: ["BroadcastStreamMix", "VOD"], Voice: "ChatMic"),
        new(["RODECaster", "RØDECaster"], "RØDECaster", WholeMix: ["MainStereo"]),
        new(["WaveLink", "ElgatoVirtual"], "Wave Link", App: "WaveLink", MixOnYourDevice: true, WholeMix: ["StreamMix"]),
        new(["MIXLINE"], "Logitech MIXLINE", App: "MIXLINE", MixOnYourDevice: true, WholeMix: ["MIXLINEStream", "MIXLINERecord"]),
        new(["Voicemeeter"], "Voicemeeter", App: "voicemeeter"),
        new(["NVIDIABroadcast"], "NVIDIA Broadcast", App: "NVIDIA Broadcast"),
    ];

    static readonly (string Word, VirtualRole Role)[] Roles =
    [
        ("Gaming", VirtualRole.Game), ("Game", VirtualRole.Game),
        ("Voice Chat", VirtualRole.Chat), ("Chat", VirtualRole.Chat), ("Communication", VirtualRole.Chat),
        ("Media", VirtualRole.Media), ("Music", VirtualRole.Media), ("Browser", VirtualRole.Media), ("Aux", VirtualRole.Media),
        ("Sample", VirtualRole.Media), ("SFX", VirtualRole.Media), ("Stream", VirtualRole.Media),
        ("System", VirtualRole.System),
    ];

    static Mixer? BrandOf(string name)
    {
        foreach (var mixer in Mixers)
            if (SaysAny(name, mixer.Match))
                return mixer;
        return null;
    }

    static bool SaysAny(ReadOnlySpan<char> text, string[] keys)
    {
        foreach (string key in keys)
            if (Says(text, key))
                return true;
        return false;
    }

    /// <summary>Whether <paramref name="text"/> has <paramref name="key"/> (written without spaces), spaces and case aside; nothing allocated.</summary>
    static bool Says(ReadOnlySpan<char> text, string key)
    {
        Span<char> squeezed = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        int length = 0;
        foreach (char c in text)
            if (c != ' ')
                squeezed[length++] = c;
        return squeezed[..length].Contains(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The virtual device a sound device's name is, or null for a real one (or an app not known).</summary>
    public static VirtualDevice? Classify(string name)
    {
        if (BrandOf(name) is not { } mixer)
            return null;
        // The role is the device's own word, before the app's name in brackets ("Chat (TC-Helicon GoXLR)", "Sonar - Chat").
        string own = name.Contains(" - ", StringComparison.Ordinal) ? name[(name.IndexOf(" - ", StringComparison.Ordinal) + 3)..] : name;
        int bracket = own.IndexOf('(');
        if (bracket > 0 && !SaysAny(own.AsSpan(0, bracket), mixer.Match))
            own = own[..bracket];
        // A device named after the mixer gives its role in the brackets ("Speakers (RODECaster Duo Chat)").
        var role = Roles.FirstOrDefault(r => own.Contains(r.Word, StringComparison.OrdinalIgnoreCase));
        if (role.Word == null)
            role = Roles.FirstOrDefault(r => name.Contains(r.Word, StringComparison.OrdinalIgnoreCase));
        return new VirtualDevice(mixer.Brand, role.Word == null ? VirtualRole.System : role.Role, mixer.MixOnYourDevice, mixer.WholeMix != null);
    }

    /// <summary>Whether a process (its exe name, no ".exe") is a mixer app that plays other apps' sound again.</summary>
    public static bool IsMixerApp(string processName)
    {
        foreach (var mixer in Mixers)
            if (mixer.App != null && processName.StartsWith(mixer.App, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>A virtual device's name without its driver in brackets when the name already says the app ("SteelSeries Sonar - Chat"); any other as it is.</summary>
    public static string ShortName(string name)
    {
        int bracket = name.IndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 && BrandOf(name) is { } mixer && SaysAny(name.AsSpan(0, bracket), mixer.Match) ? name[..bracket] : name;
    }

    /// <summary>The mixer whose whole mix a recording device is (GoXLR's Broadcast Stream Mix: the game, chat, music and the voice), or null.</summary>
    public static string? WholeMixOf(string name) => BrandOf(name) is { WholeMix: { } mixes } mixer && SaysAny(name, mixes) ? mixer.Brand : null;

    /// <summary>The device a choice means: <paramref name="chosen"/>, or for "Default" the one Windows has as the default; null when it is not there.</summary>
    public static App.NameValue? Chosen(IEnumerable<App.NameValue> devices, string chosen, string? defaultId)
    {
        string id = chosen is "" or "default" ? defaultId ?? "" : chosen;
        return devices.FirstOrDefault(d => d.Value == id);
    }

    /// <summary>
    /// The microphone chosen, when it is a mixer's whole mix (GoXLR's Broadcast Stream Mix): it, the mixer, and among
    /// <paramref name="mics"/> the mixer's device with the voice alone (GoXLR's Chat Mic) if there; null for any other.
    /// </summary>
    public static (App.NameValue Mic, string Mixer, App.NameValue? Voice)? MicOnAWholeMix(IReadOnlyList<App.NameValue> mics, string chosen, string? defaultId)
    {
        if (Chosen(mics, chosen, defaultId) is not { } mic || WholeMixOf(mic.Name) is not { } mixer)
            return null;
        return (mic, mixer, mics.FirstOrDefault(m => BrandOf(m.Name) is { Voice: { } voice } b && b.Brand == mixer && Says(m.Name, voice)));
    }

    /// <summary>Whether recording <paramref name="name"/> as the system sound misses the game (a chat or media device).</summary>
    public static bool MissesTheGame(string name) => Classify(name)?.MissesTheGame == true;

    /// <summary>Among the devices, the one a mixer app gives the game (its "Gaming" or "Game" device), or null.</summary>
    public static string? GameDevice(IEnumerable<string> names) => names.FirstOrDefault(n => Classify(n)?.Role == VirtualRole.Game);

    /// <summary>Among the devices, the only real one (where a mixer such as Sonar plays all of it), or null when none or several.</summary>
    public static string? OnlyRealDevice(IEnumerable<string> names)
    {
        string? found = null;
        foreach (string name in names.Where(n => Classify(n) == null))
        {
            if (found != null)
                return null;
            found = name;
        }
        return found;
    }
}
