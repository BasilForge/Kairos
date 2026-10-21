using Kairos.App;

namespace Kairos.App;

/// <summary>NVIDIA's overlay shortcuts have no Linux counterpart (GeForce Experience is Windows-only).</summary>
public static class NvidiaHotkeys
{
    public static IReadOnlyList<Hotkey> Read() => [];
}
