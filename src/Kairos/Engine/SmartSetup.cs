using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>
/// What this PC has, as far as recording goes: the GPU that drives the captured screen and every GPU's name, NVIDIA's
/// driver version, the CPU's threads and name, the screen's size and refresh rate, and whether it runs on a battery now.
/// </summary>
public sealed record HardwareProfile(string? CaptureGpu, IReadOnlyList<string> Gpus, string? NvidiaDriver, int CpuThreads, string Cpu,
    int RefreshHz, int ScreenWidth, int ScreenHeight, bool OnBattery)
{
    /// <summary>The captured screen's Windows device name (\\.\DISPLAY1), for the game layer; null when unknown.</summary>
    public string? CaptureDevice { get; init; }

    /// <summary>Changes when the hardware, NVIDIA's driver or the screen changes (what an encoder's result is kept for).</summary>
    public string Fingerprint => $"{string.Join("+", Gpus)}|{NvidiaDriver}|{Cpu}|{ScreenWidth}x{ScreenHeight}@{RefreshHz}";

    /// <summary>The profile now, for the screen libobs captures as <paramref name="monitorId"/> (no process opened).</summary>
    public static HardwareProfile Read(string? monitorId)
    {
        var screen = App.Displays.For(monitorId);
        var (width, height, hz) = App.Displays.Mode(screen?.Device);
        var adapters = App.Displays.Adapters();
        return new HardwareProfile(screen?.Gpu, [.. adapters.Select(a => a.Name)],
            adapters.FirstOrDefault(a => a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)).Driver,
            Environment.ProcessorCount, App.Displays.CpuName(), hz, width, height, App.Displays.OnBattery())
        {
            CaptureDevice = screen?.Device,
        };
    }
}

/// <summary>"Automatic": the encoder, picture size, frame rate and quality this PC records best with.</summary>
public sealed record SmartChoice(EncoderChoice Encoder, OutputResolution Resolution, int Fps, QualityPreset Quality);

/// <summary>
/// Picks what to record with from the hardware and the encoders libobs has (which already says what the card encodes):
/// the card before the codec (NVIDIA, AMD, Intel, then the CPU), its best codec, a size and rate the encoder keeps up
/// with, never above the screen's own rate, and 60 at most (what every site plays). Frames skipped while recording
/// step it down: quality, then the frame rate, then the size.
/// </summary>
public static class SmartSetup
{
    /// <summary>
    /// Intel Quick Sync only on the GPU that drives the captured screen, and newer than Haswell: on another GPU libobs
    /// falls back to its old encoder, which fails on many PCs ("MFX_ERR_NOT_FOUND").
    /// </summary>
    public static IReadOnlyList<string> Usable(IReadOnlyList<string> ids, HardwareProfile hw)
    {
        bool intelScreen = hw.CaptureGpu?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true && !Gpus.IsOldIntel(hw.CaptureGpu);
        return [.. ids.Where(id => intelScreen || !id.StartsWith("obs_qsv11", StringComparison.Ordinal))];
    }

    /// <summary>The best encoder for <paramref name="codec"/> this card can use (<see cref="Usable"/>), with its limits.</summary>
    public static EncoderChoice Pick(VideoCodec codec, IReadOnlyList<string> ids, HardwareProfile hw)
    {
        var encoder = EncoderCatalog.Pick(codec, Usable(ids, hw));
        // NVENC HEVC before Turing has no B-frames (the GPU that drives the captured screen).
        return encoder.Vendor == EncoderVendor.Nvidia && encoder.Codec == VideoCodec.Hevc && !Gpus.NvencHevcBFrames(hw.CaptureGpu)
            ? encoder with { NoBFrames = true }
            : encoder;
    }

    /// <summary>
    /// Automatic's format, and what a choice the card lacks falls to: H.264, the most stable and it plays everywhere.
    /// AV1 and HEVC are the user's choice, on a card that records them.
    /// </summary>
    public const VideoCodec DefaultCodec = VideoCodec.H264;

    /// <summary>
    /// The format a manual choice records: as chosen when the recording card has it (<see cref="EncoderCatalog.FirstCard"/>),
    /// else <see cref="DefaultCodec"/>, so HEVC never comes in by itself where Windows may not play it.
    /// </summary>
    public static VideoCodec ManualCodec(VideoCodec chosen, IReadOnlyCollection<string> ids) =>
        EncoderCatalog.HasHardware(chosen, EncoderCatalog.FirstCard(ids)) ? chosen : DefaultCodec;

    /// <summary>The encoder a manual choice records with (<see cref="ManualCodec"/> on what this PC may use, then <see cref="Pick"/>).</summary>
    public static EncoderChoice PickChosen(VideoCodec chosen, IReadOnlyList<string> ids, HardwareProfile hw) =>
        Pick(ManualCodec(chosen, Usable(ids, hw)), ids, hw);

    public static SmartChoice Choose(HardwareProfile hw, IReadOnlyList<string> ids, int step = 0)
    {
        var encoder = Pick(DefaultCodec, ids, hw);
        int fps = Math.Min(60, hw.RefreshHz > 0 ? hw.RefreshHz : 60);
        OutputResolution size;
        QualityPreset quality;
        bool integrated = encoder.Vendor == EncoderVendor.Intel
                          || (encoder.Vendor == EncoderVendor.Amd && hw.CaptureGpu?.Contains(" RX ", StringComparison.OrdinalIgnoreCase) != true);
        if (encoder.Vendor == EncoderVendor.Software)
        {
            int threads = hw.CpuThreads;
            size = Cap(hw, threads >= 12 ? 1080 : 720);
            fps = Math.Min(fps, 30);
            quality = threads < 4 ? QualityPreset.Light : QualityPreset.Balanced;
            encoder = encoder with { X264Preset = threads >= 6 ? "veryfast" : threads >= 4 ? "superfast" : "ultrafast" };
        }
        else if (integrated)
        {
            size = Cap(hw, 1080);
            quality = QualityPreset.Balanced;
        }
        else
        {
            // Up to 1440p: H.264 at 4K would be huge.
            size = Cap(hw, 1440);
            quality = QualityPreset.High;
        }
        if (hw.OnBattery)
            quality = Lighter(quality);
        if (step >= 1)
            quality = Lighter(quality);
        if (step >= 2)
            fps = Math.Min(fps, 30);
        if (step >= 3)
            size = Smaller(size, hw);
        return new SmartChoice(encoder, size, fps, quality);
    }

    static OutputResolution Cap(HardwareProfile hw, int height) =>
        hw.ScreenHeight > height ? ResolutionOptions.ForHeight(height) : OutputResolution.Native;

    /// <summary>The next preset down (720p stays).</summary>
    static OutputResolution Smaller(OutputResolution size, HardwareProfile hw) =>
        ResolutionOptions.ForHeight(ResolutionOptions.Height(size, hw.ScreenHeight) - 1);

    static QualityPreset Lighter(QualityPreset q) => q switch
    {
        QualityPreset.Ultra => QualityPreset.High,
        QualityPreset.High => QualityPreset.Balanced,
        _ => QualityPreset.Light,
    };
}
