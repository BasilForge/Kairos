using Kairos.Engine.Share;

namespace Kairos.Tests;

/// <summary>The FFmpeg 8 libraries the tests run on: KAIROS_FFMPEG, else the copy tools/get-ffmpeg.sh makes in ~/ffmpeg8.</summary>
static class TestFfmpeg
{
    public static string? Folder => new[] { Environment.GetEnvironmentVariable("KAIROS_FFMPEG"), Kairos.Engine.CaptureBackend.FfmpegFolder }
        .FirstOrDefault(d => d != null && File.Exists(Path.Combine(d, Kairos.Engine.CaptureBackend.FfmpegMainLibrary)));

    public static void Load()
    {
        string? dir = Folder;
        Assert.True(dir != null, "FFmpeg 8 libraries not found (run tools/get-ffmpeg.sh)");
        ClipTranscoder.Load(dir!);
    }
}
