using Kairos.Engine.Share;

namespace Kairos.Ui;

/// <summary>
/// What dragging a clip out of the Library hands on: the clip itself, or for HEVC (<see cref="SharePlanner.PlaysEverywhere"/>)
/// its H.264 copy at its own size in the Shared folder, made on the first drag (the card shows its progress) and taken
/// by the next. It has the name the share panel's whole-clip copy has, so either one serves both.
/// </summary>
public sealed class PlayableCopies(IShareHost host)
{
    readonly Dictionary<string, (DateTime Written, MediaInfo Info)> _info = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file to drag for <paramref name="clip"/>: itself, its H.264 copy, or null while it is still to make. The copy
    /// counts while it is on disk and not older than the clip (an edit saved over the clip, or a copy deleted, makes it again).
    /// </summary>
    public string? FileFor(string clip)
    {
        var info = Info(clip);
        if (SharePlanner.PlaysEverywhere(info.VideoCodec))
            return clip;
        string target = Target(clip, info);
        return File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(clip) ? target : null;
    }

    /// <summary>Makes <paramref name="clip"/>'s H.264 copy (on a worker thread); the next <see cref="FileFor"/> gives it.</summary>
    public ShareResult Make(string clip, IProgress<double> progress, CancellationToken ct)
    {
        var info = Info(clip);
        return host.Transcode(clip, Target(clip, info), SharePlanner.CopyPlan(0, info, 0, info.Duration), long.MaxValue, progress, ct);
    }

    string Target(string clip, MediaInfo info) => ShareFiles.CopyTarget(host.SharedFolder, clip, 0, info.Duration);

    /// <summary>The clip's format and size, read once (again only when the file was written since: an edit saved over it).</summary>
    MediaInfo Info(string clip)
    {
        var written = File.Exists(clip) ? File.GetLastWriteTimeUtc(clip) : DateTime.MinValue;
        lock (_info)
        {
            if (_info.TryGetValue(clip, out var known) && known.Written == written)
                return known.Info;
        }
        var info = host.Probe(clip);
        lock (_info)
            _info[clip] = (written, info);
        return info;
    }
}
