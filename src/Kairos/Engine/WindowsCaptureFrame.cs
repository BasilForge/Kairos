namespace Kairos.Engine;

/// <summary>
/// Whether Windows draws its yellow frame around what its own capture records (Apps mode), on screen only, not in the
/// clip: an app may turn it off only from build 20348 on (GraphicsCaptureSession.IsBorderRequired, which libobs turns
/// off), so every Windows 10 shows it.
/// </summary>
public static class WindowsCaptureFrame
{
    const int TurnedOffFrom = 20348;

    /// <summary>This Windows (the tests and renders set it to try another).</summary>
    public static bool Shown { get; internal set; } = Environment.OSVersion.Version.Build < TurnedOffFrom;
}
