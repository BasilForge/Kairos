using System.Net.Http;
using System.Security.Cryptography;

namespace Kairos.Engine.Captions;

/// <summary>A file fetched whole into a part file and checked (its size and SHA-256) before anything uses it.</summary>
static class VerifiedDownload
{
    /// <summary>
    /// Fetches <paramref name="source"/> (an https address, or a local file for testing) into <paramref name="part"/>;
    /// throws <see cref="IOException"/> when it is not exactly the expected file. The caller removes the part file.
    /// </summary>
    /// <param name="name">What it is, for the error ("captions model").</param>
    public static async Task To(string part, string source, long bytes, string sha256, string name, IProgress<double>? progress,
        CancellationToken ct)
    {
        if (App.SideEffects.Held($"download {name}"))
            throw new IOException($"The {name} download was held (walk check).");
        await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await Copy(input, output, bytes, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
                await Copy(input, output, bytes, progress, ct).ConfigureAwait(false);
            }
        }
        if (new FileInfo(part).Length != bytes)
            throw new IOException($"The {name} did not download completely.");
        await using var check = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)) != sha256)
            throw new IOException($"The downloaded {name} is damaged.");
    }

    /// <summary>Clears a leftover; one still in use (a download running in this window) is cleared next time.</summary>
    public static void Quietly(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static async Task Copy(Stream input, Stream output, long bytes, IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        int read, percent = -1;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (done > bytes)
                break;
            int now = (int)(done * 100 / bytes);
            if (now != percent)
                progress?.Report((percent = now) / 100.0);
        }
    }
}
