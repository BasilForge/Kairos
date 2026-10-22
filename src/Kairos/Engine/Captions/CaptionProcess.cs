using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kairos.Engine.Captions;

/// <summary>
/// Runs <see cref="Transcriber"/> in a hidden Kairos process of its own (Kairos.exe --captions): the model, the graphics
/// card's memory and the runtime are all gone the moment it ends, and a crash in the native code cannot take the editor
/// with it. It ends by itself if the window that started it closes.
/// </summary>
public static class CaptionProcess
{
    const string Command = "--captions";

    /// <param name="exe">Kairos.exe.</param>
    /// <param name="ffmpegFolder">Where the FFmpeg DLLs are (default: beside Kairos.exe).</param>
    /// <param name="runtimeFolder">Where the whisper DLLs are (default: the downloaded engine).</param>
    /// <param name="streams">The audio streams heard together (written "2,3").</param>
    public static async Task<IReadOnlyList<Caption>> Run(string exe, string clip, IReadOnlyList<int> streams, IProgress<double>? progress, CancellationToken ct,
        string? ffmpegFolder = null, string? runtimeFolder = null)
    {
        ct.ThrowIfCancellationRequested();
        string output = Path.Combine(Path.GetTempPath(), "Kairos", "captions-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { Command, clip, string.Join(",", streams.Select(s => s.ToString(CultureInfo.InvariantCulture))), output,
                     Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        if (ffmpegFolder != null || runtimeFolder != null)
            start.ArgumentList.Add(ffmpegFolder ?? Share.ClipTranscoder.DefaultFolder);
        if (runtimeFolder != null)
            start.ArgumentList.Add(runtimeFolder);

        using var process = Process.Start(start) ?? throw new CaptionException("Auto captions could not start.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It already ended (it says why below).
        }
        string? error = null;
        try
        {
            using (ct.Register(() => Stop(process)))
            {
                while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("progress ", StringComparison.Ordinal) &&
                        double.TryParse(line.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        progress?.Report(value);
                    else if (line.StartsWith("error ", StringComparison.Ordinal))
                        error = line[6..];
                }
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new CaptionException(error ?? $"Auto captions stopped ({process.ExitCode}).");
            return JsonSerializer.Deserialize<List<Caption>>(await File.ReadAllTextAsync(output, ct).ConfigureAwait(false)) ?? [];
        }
        finally
        {
            Stop(process);
            try
            {
                File.Delete(output);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// The captions engine's folder: an installed Kairos uses the downloaded one (null) whatever it is told; a development
    /// build may be pointed at another (tests).
    /// </summary>
    internal static string? RuntimeFolder(string appDir, string? asked) => Share.ClipTranscoder.Installed(appDir) ? null : asked;

    /// <summary>Kairos.exe --captions &lt;clip&gt; &lt;streams, as 2,3&gt; &lt;output.json&gt; &lt;window pid&gt; [ffmpeg folder [whisper folder]].</summary>
    public static int Helper(string[] args)
    {
        using var say = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var streams = args.Length > 2 ? args[2].Split(',').Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out int n) ? n : -1).ToList() : [];
        if (args.Length < 5 || args[0] != Command || streams.Count == 0 || streams.Any(s => s < 0) || !int.TryParse(args[4], out int parent))
        {
            say.WriteLine("error usage: --captions <clip> <streams> <output.json> <pid> [ffmpeg folder]");
            return 2;
        }
        WatchParent(parent);
        try
        {
            Share.ClipTranscoder.Load(Share.ClipTranscoder.FfmpegFolder(AppContext.BaseDirectory, args.Length > 5 ? args[5] : null));
            int percent = -1;
            var progress = new InlineProgress(value =>
            {
                int now = (int)(value * 100);
                if (now == percent)
                    return;
                percent = now;
                say.WriteLine("progress " + value.ToString("0.###", CultureInfo.InvariantCulture));
            });
            var captions = Transcriber.Run(args[1], streams, progress, CancellationToken.None,
                runtimeFolder: RuntimeFolder(AppContext.BaseDirectory, args.Length > 6 ? args[6] : null));
            File.WriteAllText(args[3], JsonSerializer.Serialize(captions));
            return 0;
        }
        catch (Exception e) when (e is CaptionException or Share.ShareException or IOException or UnauthorizedAccessException)
        {
            say.WriteLine("error " + e.Message.ReplaceLineEndings(" "));
            return 1;
        }
    }

    /// <summary>Ends this process when the window that asked for the captions is gone.</summary>
    static void WatchParent(int pid)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            Environment.Exit(3);
            return;
        }
        var watcher = new Thread(() =>
        {
            parent.WaitForExit();
            Environment.Exit(3);
        }) { IsBackground = true, Name = "captions: window watch" };
        watcher.Start();
    }

}
