using System.IO.Compression;
using Kairos.Engine.Updates;

namespace Kairos.Engine.Captions;

/// <summary>Where auto captions keep what they download: in Kairos's local folder, so it goes when Kairos is removed.</summary>
public static class CaptionFiles
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "captions");
}

/// <summary>
/// Auto captions' engine: whisper.cpp 1.9.5 with Vulkan (five DLLs, 14 MB zipped). Not in the installer: downloaded with the
/// model, after the user agreed, checked and unpacked into <see cref="CaptionFiles.Root"/>\runtime.
/// </summary>
public sealed class CaptionRuntime(string folder, string source)
{
    const string WhisperVersion = "1.9.5";
    public const string AssetName = $"kairos-captions-runtime-{WhisperVersion}.zip";
    public const string Tag = $"captions-runtime-{WhisperVersion}";
    public const long Bytes = 14_848_938;
    const string Sha256 = "9b0a98ccfdc482b21e4d4a6a33ce825d735043e2dc3a40e6f269214fb031d18a";
    public static readonly string[] Files = OperatingSystem.IsWindows()
        ? ["whisper.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll", "ggml-vulkan.dll"]
        : ["libwhisper.so", "libggml.so", "libggml-base.so", "libggml-cpu.so"];

    /// <summary>The library Kairos loads (the others are found beside it).</summary>
    public static string MainLibrary => Files[0];

    /// <summary>From the GitHub release, or (testing before publishing) the local update folder's captions-runtime\.</summary>
    public static CaptionRuntime Default { get; } = new(Path.Combine(CaptionFiles.Root, "runtime"),
        UpdateRules.LocalFeed(Settings.SettingsStore.AppDataDir) is { } feed
            ? Path.Combine(feed, "captions-runtime", AssetName)
            : $"{UpdateRules.Repository}/releases/download/{Tag}/{AssetName}");

    public string Folder => folder;

    public bool IsPresent => Files.All(f => File.Exists(Path.Combine(folder, f)));

    /// <summary>Fetches, checks and unpacks the engine. Nothing is kept when it fails or is cancelled.</summary>
    /// <param name="consent">The user agreed to this download (it is refused otherwise).</param>
    public async Task Download(bool consent, IProgress<double>? progress, CancellationToken ct)
    {
        if (!consent)
            throw new InvalidOperationException("The captions engine is only downloaded after the user agreed.");
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Build the captions engine once with tools/build-whisper.sh, then try again.");
        string parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        Directory.CreateDirectory(parent);
        string stem = Path.Combine(parent, Path.GetFileName(folder) + "." + Guid.NewGuid().ToString("N"));
        string part = stem + ".zip.part", unpacked = stem + ".part";
        try
        {
            await VerifiedDownload.To(part, source, Bytes, Sha256, "captions engine", progress, ct).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(part, unpacked);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            Directory.Move(unpacked, folder);
        }
        finally
        {
            VerifiedDownload.Quietly(() => File.Delete(part));
            VerifiedDownload.Quietly(() =>
            {
                if (Directory.Exists(unpacked))
                    Directory.Delete(unpacked, recursive: true);
            });
        }
    }

    /// <summary>Deletes the engine (Settings); auto captions ask to download it again next time.</summary>
    public void Remove()
    {
        RemoveParts();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>Clears what an interrupted download left (the window closed mid-download).</summary>
    public void RemoveParts()
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        if (!Directory.Exists(parent))
            return;
        string name = Path.GetFileName(folder);
        foreach (string file in Directory.EnumerateFiles(parent, name + ".*.part"))
            VerifiedDownload.Quietly(() => File.Delete(file));
        foreach (string dir in Directory.EnumerateDirectories(parent, name + ".*.part"))
            VerifiedDownload.Quietly(() => Directory.Delete(dir, recursive: true));
    }

    /// <summary>Bytes on disk (the storage row in Settings).</summary>
    public long DiskBytes => Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length) : 0;
}
