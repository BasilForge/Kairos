using Kairos.Engine.Captions;

namespace Kairos.Ui.Edit;

/// <summary>What the editor's Auto captions button needs: the engine and model, and something that listens to a clip.</summary>
public interface ICaptionMaker
{
    /// <summary>The engine and the model are both on this PC.</summary>
    bool Ready { get; }
    /// <summary>What a download fetches (bytes): whatever of the engine and the model is missing.</summary>
    long DownloadBytes { get; }
    /// <summary>Only called after the user agreed to the download.</summary>
    Task Download(IProgress<double> progress, CancellationToken ct);
    Task<IReadOnlyList<Caption>> Listen(string clip, IReadOnlyList<int> streams, IProgress<double> progress, CancellationToken ct);
}

/// <summary>The real one: the downloaded engine and model, and Whisper in a hidden Kairos process of its own.</summary>
public sealed class CaptionMaker(CaptionModel model, CaptionRuntime runtime, string exe) : ICaptionMaker
{
    public bool Ready => model.IsPresent && runtime.IsPresent;

    public long DownloadBytes => (runtime.IsPresent ? 0 : CaptionRuntime.Bytes) + (model.IsPresent ? 0 : CaptionModel.Bytes);

    /// <summary>The engine first, then the model; one progress over both, by size.</summary>
    public async Task Download(IProgress<double> progress, CancellationToken ct)
    {
        double total = Math.Max(1, DownloadBytes), done = 0;
        if (!runtime.IsPresent)
        {
            await runtime.Download(consent: true, new ScaledProgress(progress, done / total, (done + CaptionRuntime.Bytes) / total), ct);
            done += CaptionRuntime.Bytes;
        }
        if (!model.IsPresent)
            await model.Download(consent: true, new ScaledProgress(progress, done / total, (done + CaptionModel.Bytes) / total), ct);
    }

    public Task<IReadOnlyList<Caption>> Listen(string clip, IReadOnlyList<int> streams, IProgress<double> progress, CancellationToken ct) =>
        CaptionProcess.Run(exe, clip, streams, progress, ct, runtimeFolder: runtime.Folder);
}
