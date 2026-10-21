using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Kairos.Site;

/// <summary>
/// One DevTools connection (a page's or the browser's): numbered requests out, their answers and the events in. A
/// failed request raises <see cref="InvalidOperationException"/>, which the publishing steps treat as "the page is
/// between loads" and try again.
/// </summary>
internal sealed class DevToolsConnection : IDisposable
{
    readonly ClientWebSocket _socket = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly CancellationTokenSource _closed = new();
    int _next;

    /// <summary>An event of the browser (method, parameters), on the reading thread.</summary>
    public event Action<string, JsonElement>? Event;

    /// <summary>The connection ended (the browser or the page went away).</summary>
    public event Action? Lost;

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public static async Task<DevToolsConnection> Open(string url, CancellationToken ct)
    {
        var connection = new DevToolsConnection();
        await connection._socket.ConnectAsync(new Uri(url), ct);
        _ = Task.Run(connection.Read);
        return connection;
    }

    public async Task<JsonElement> Call(string method, string? parameters = null, CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            var bytes = Encoding.UTF8.GetBytes($"{{\"id\":{id},\"method\":{JsonSerializer.Serialize(method)},\"params\":{parameters ?? "{}"}}}");
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
            await using (linked.Token.Register(() => answer.TrySetCanceled()))
                return await answer.Task;
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("The browser is gone: " + e.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    async Task Read()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_closed.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _closed.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);
                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idElement))
                {
                    if (!_pending.TryGetValue(idElement.GetInt32(), out var answer))
                        continue;
                    if (root.TryGetProperty("error", out var error))
                        answer.TrySetException(new InvalidOperationException(error.GetProperty("message").GetString()));
                    else
                        answer.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                }
                else if (root.TryGetProperty("method", out var method))
                    Event?.Invoke(method.GetString() ?? "", root.TryGetProperty("params", out var p) ? p.Clone() : default);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or JsonException)
        {
            // The browser closed: everything waiting is told below.
        }
        finally
        {
            foreach (var answer in _pending.Values)
                answer.TrySetException(new InvalidOperationException("The browser closed."));
            Lost?.Invoke();
        }
    }

    public void Dispose()
    {
        _closed.Cancel();
        _socket.Dispose();
    }
}

/// <summary>
/// A Chromium-family browser (Brave, Chrome, Chromium, Edge) running on Kairos's own profile, driven over its DevTools port.
/// The sign-ins live in that profile only, never in the user's everyday browser. Without windows of its own it sits in
/// the background and exits when the last page closes.
/// </summary>
internal sealed class ChromiumProcess : IDisposable
{
    static readonly string[] Names =
        ["brave-browser", "brave", "google-chrome-stable", "google-chrome", "chromium", "chromium-browser", "microsoft-edge-stable", "microsoft-edge", "vivaldi"];

    readonly Process _process;
    readonly DevToolsConnection _browser;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly int _port;

    ChromiumProcess(Process process, DevToolsConnection browser, int port)
    {
        _process = process;
        _browser = browser;
        _port = port;
    }

    /// <summary>The first browser found on the PATH (KAIROS_BROWSER names one by hand).</summary>
    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("KAIROS_BROWSER") is { Length: > 0 } chosen && File.Exists(chosen))
            return chosen;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            foreach (string name in Names)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return path;
            }
        return null;
    }

    public bool HasExited => _process.HasExited || !_browser.IsOpen;

    public event Action? Exited;

    /// <param name="headless">No window at all (reading the sign-ins); the profile cannot be shared with a visible one meanwhile.</param>
    public static async Task<ChromiumProcess> Start(string profile, bool headless, CancellationToken ct)
    {
        string exe = Find() ?? throw new InvalidOperationException("No Chromium-based browser found.");
        Directory.CreateDirectory(profile);
        string portFile = Path.Combine(profile, "DevToolsActivePort");
        File.Delete(portFile);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[]
        {
            "--user-data-dir=" + profile, "--remote-debugging-port=0", "--remote-allow-origins=*", "--no-first-run", "--no-default-browser-check",
            "--lang=en-US", "--accept-lang=en-US", "--password-store=basic", "--disk-cache-size=67108864", "--no-startup-window",
            // Pages the user cannot see must not be slowed: the posting steps poll the page several times a second.
            "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows",
            "--disable-features=CalculateNativeWinOcclusion,Translate",
            headless ? "--headless=new" : "about:blank",
        })
            start.ArgumentList.Add(argument);
        start.Environment["LANGUAGE"] = "en_US";
        var process = Process.Start(start) ?? throw new InvalidOperationException("The browser did not start.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            // The browser writes its port and the address of its own DevTools socket once it is listening.
            for (int waited = 0; waited < 300; waited++)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException("The browser closed straight away (is another one using the profile?).");
                if (File.Exists(portFile) && File.ReadAllLines(portFile) is { Length: >= 2 } lines && int.TryParse(lines[0], out int port))
                {
                    var browser = await DevToolsConnection.Open($"ws://127.0.0.1:{port}{lines[1]}", ct);
                    var result = new ChromiumProcess(process, browser, port);
                    browser.Lost += () => result.Exited?.Invoke();
                    return result;
                }
                await Task.Delay(100, ct);
            }
            throw new TimeoutException("The browser did not come up.");
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>A new window on <paramref name="url"/>, and its page.</summary>
    public async Task<DevToolsPage> OpenPage(string url, int width, int height, CancellationToken ct)
    {
        var created = await _browser.Call("Target.createTarget",
            JsonSerializer.Serialize(new { url, newWindow = true, width, height }), ct);
        string targetId = created.GetProperty("targetId").GetString()!;
        string list = await _http.GetStringAsync($"http://127.0.0.1:{_port}/json/list", ct);
        using var doc = JsonDocument.Parse(list);
        string socket = doc.RootElement.EnumerateArray().First(t => t.GetProperty("id").GetString() == targetId).GetProperty("webSocketDebuggerUrl").GetString()!;
        var connection = await DevToolsConnection.Open(socket, ct);
        var page = new DevToolsPage(this, connection, targetId);
        await page.Prepare();
        return page;
    }

    internal Task<JsonElement> Browser(string method, string? parameters = null) => _browser.Call(method, parameters);

    /// <summary>Closes the browser; its profile is left as it is.</summary>
    public async Task Close()
    {
        try
        {
            await _browser.Call("Browser.close").WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException)
        {
            // Already going.
        }
        try
        {
            if (!_process.WaitForExit(4000))
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        _browser.Dispose();
        _http.Dispose();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}
