namespace Kairos.Settings;

/// <summary>Decides whether a settings change has to rebuild the capture pipeline (which empties the replay buffer).</summary>
public static class SettingsDiff
{
    public static bool NeedsRebuild(AppSettings before, AppSettings after) => PipelineKey(before) != PipelineKey(after);

    /// <summary>
    /// The settings with everything the pipeline ignores (hotkeys, looks, sounds, startup, voice sensitivity, sharing)
    /// reset to defaults, and the volumes, which are set on the running sources. Smart Save on/off stays: it decides
    /// whether the microphone source exists.
    /// </summary>
    static string PipelineKey(AppSettings s)
    {
        var copy = SettingsStore.Copy(s);
        copy.Hotkeys = new HotkeySettings();
        copy.General = new GeneralSettings { OnlyWhenGaming = Engine.SceneRules.PausesWithoutGame(s) }; // whether the replay waits for a game
        copy.Voice = new VoiceSettings { Enabled = s.Voice.Enabled };
        copy.Share = new ShareSettings();
        // The camera's place, size and shape are set on the running scene.
        copy.Camera = s.Camera with { X = 0, Y = 0, Size = 0, Shape = CameraShape.Square };
        LiveAudio.Default(copy.Audio).SetOn(copy.Audio);
        // Instant replay on/off only starts or stops the buffer; a rebuild would also end a full recording.
        copy.Replay.Enabled = true;
        copy.Replay.FreshAfterSave = false; // read when a clip is saved
        return SettingsStore.Serialize(copy);
    }
}
