namespace Kairos.Engine.Captions;

/// <summary>
/// The Whisper model auto captions use (large-v3-turbo, quantized), kept in Kairos's local folder (it goes when Kairos
/// is removed). Not shipped with the app:
/// downloaded once, only after the user agreed, and removable in Settings.
/// </summary>
public sealed class CaptionModel(string folder)
{
    public const string FileName = "ggml-large-v3-turbo-q5_0.bin";
    public const long Bytes = 574_041_195;
    const string Sha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2";
    public static readonly Uri Source = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + FileName);

    public static CaptionModel Default { get; } = new(System.IO.Path.Combine(CaptionFiles.Root, "model"));

    /// <summary>Where Kairos kept the model before it moved to the local folder.</summary>
    public static string OldFolder => System.IO.Path.Combine(Settings.SettingsStore.AppDataDir, "models");

    public string Folder => folder;
    public string Path => System.IO.Path.Combine(folder, FileName);

    /// <summary>Downloaded in full (a partial file does not count).</summary>
    public bool IsPresent
    {
        get
        {
            var file = new FileInfo(Path);
            return file.Exists && file.Length == Bytes;
        }
    }

    /// <summary>
    /// Fetches the model into a part file, checks its size and fingerprint, then gives it its name. Nothing is kept when it
    /// fails or is cancelled.
    /// </summary>
    /// <param name="consent">The user agreed to this download (it is refused otherwise).</param>
    public async Task Download(bool consent, IProgress<double>? progress, CancellationToken ct)
    {
        if (!consent)
            throw new InvalidOperationException("The captions model is only downloaded after the user agreed.");
        Directory.CreateDirectory(folder);
        RemoveParts();
        string part = Path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await VerifiedDownload.To(part, Source.ToString(), Bytes, Sha256, "captions model", progress, ct).ConfigureAwait(false);
            HeldFile.Retry(() => File.Move(part, Path, overwrite: true)); // just downloaded: an antivirus reads it
        }
        finally
        {
            VerifiedDownload.Quietly(() => File.Delete(part));
        }
    }

    /// <summary>Moves a model from <paramref name="oldFolder"/> here, unless one is here already; true when it moved.</summary>
    public bool MoveFrom(string oldFolder)
    {
        string old = System.IO.Path.Combine(oldFolder, FileName);
        if (!File.Exists(old) || File.Exists(Path))
            return false;
        Directory.CreateDirectory(folder);
        File.Move(old, Path);
        return true;
    }

    /// <summary>Deletes the model (Settings); auto captions ask to download it again next time.</summary>
    public void Remove()
    {
        RemoveParts();
        if (File.Exists(Path))
            File.Delete(Path);
    }

    /// <summary>Clears what an interrupted download left (the window closed mid-download).</summary>
    public void RemoveParts()
    {
        if (!Directory.Exists(folder))
            return;
        foreach (string part in Directory.EnumerateFiles(folder, FileName + ".*.part"))
            VerifiedDownload.Quietly(() => File.Delete(part));
    }
}
