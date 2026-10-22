using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Kairos.Engine;

namespace Kairos.App;

public sealed record NameValue(string Name, string Value);

public sealed record StatusDto(
    EngineStatus? Engine,
    Dictionary<string, bool> Hotkeys,
    string? LastClip,
    string? Error,
    bool PausedByUser,
    bool WaitingForGame,
    string Version,
    Engine.Voice.VoiceStatus? Voice = null,
    string? LastShot = null,
    Engine.Updates.UpdateStatus? Update = null,
    Engine.Updates.UpdateLook? Look = null);

public sealed record CapsDto(
    List<MonitorInfo> Monitors,
    List<NameValue> Windows,
    List<NameValue> Mics,
    List<NameValue> Speakers,
    List<string> Encoders,
    string LibobsVersion,
    List<NameValue>? AudioApps = null,
    string? DefaultSpeaker = null,
    int RefreshHz = 0,
    string? DefaultMic = null);

public static class Ipc
{
    /// <summary>
    /// The settings window's volumes and mutes while a slider moves: one pipe line the engine sets on its sources at once,
    /// its saved settings left as they are (Save or Revert then settles them).
    /// </summary>
    public static string LiveLine(Settings.AudioSettings a) => "live " + JsonSerializer.Serialize(Settings.LiveAudio.Of(a), Json);

    public static Settings.LiveAudio ParseLive(string json) =>
        JsonSerializer.Deserialize<Settings.LiveAudio>(json, Json) ?? throw new JsonException("no live audio");

    /// <summary>The engine's pipe (KAIROS_PIPE: the walk check's own made-up engine, never the running one).</summary>
    public static string PipeName { get; } = Environment.GetEnvironmentVariable("KAIROS_PIPE") is { Length: > 0 } pipe ? pipe : AppInfo.PipeName;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// One request line in, one response line out; "levels" instead keeps the connection open and streams
/// "systemDb micDb" lines about 30 times a second. Each connection is served on its own task, so a
/// stream never blocks other commands. Only the current user can connect.
/// </summary>
public sealed class PipeServer : IDisposable
{
    readonly Action<string, int>? _onRequest;
    const int MaxConnections = 8;
    static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(33);

    readonly Func<string, string> _handle;
    readonly Func<IReadOnlyList<(string Id, double Db)>> _levels;
    readonly CancellationTokenSource _cts = new();
    readonly string _name;
    /// <summary>A client that connects but sends no command is dropped after this, so it cannot hold a connection slot.</summary>
    readonly TimeSpan _idleTimeout;

    /// <param name="onRequest">Told each request and the id of the process that sent it (the engine logs who asked it to exit).</param>
    public PipeServer(Func<string, string> handle, Func<IReadOnlyList<(string Id, double Db)>> levels, string? pipeName = null, TimeSpan? idleTimeout = null,
        Action<string, int>? onRequest = null)
    {
        _onRequest = onRequest;
        _name = pipeName ?? Ipc.PipeName;
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(5);
        _handle = handle;
        _levels = levels;
        _ = Task.Run(LoopAsync);
    }

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_name, PipeDirection.InOut, MaxConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                var connected = server;
                server = null;
                _ = Task.Run(() => ServeAsync(connected));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception e)
            {
                server?.Dispose();
                ObsLog.Warn($"pipe: {e.Message}");
                await Task.Delay(200);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    async Task ServeAsync(NamedPipeServerStream server)
    {
        await using (server)
        {
            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? request;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    wait.CancelAfter(_idleTimeout);
                    request = (await reader.ReadLineAsync(wait.Token))?.Trim();
                }
                if (request == "levels")
                {
                    // The meters start from now: drop the loudest peak since anyone last looked (it can be minutes old
                    // and made the Audio page's meters jump to full on opening).
                    _levels();
                    var line = new StringBuilder();
                    while (!_cts.IsCancellationRequested && server.IsConnected)
                    {
                        line.Clear();
                        foreach (var (id, db) in _levels())
                            line.Append(CultureInfo.InvariantCulture, $"{id}:{db:F1}|");
                        if (line.Length > 0)
                            line.Length--;
                        await writer.WriteLineAsync(line);
                        await Task.Delay(LevelInterval, _cts.Token);
                    }
                }
                else if (request != null)
                {
                    if (_onRequest != null && GetNamedPipeClientProcessId(server.SafePipeHandle, out uint client))
                        _onRequest(request, (int)client);
                    await writer.WriteLineAsync(_handle(request));
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Client went away or the engine is closing.
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Live audio levels from the engine for the settings window's meters (one background reader).</summary>
public sealed class LevelStream : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _levels = new();

    public LevelStream() : this(connect: true)
    {
    }

    /// <param name="connect">False: no engine, the levels come from <see cref="Push"/> (tests).</param>
    internal LevelStream(bool connect)
    {
        if (connect)
            new Thread(Run) { IsBackground = true, Name = "Kairos levels" }.Start();
    }

    /// <summary>A source's level (a row's id, or "system" and "mic" in the together layout); silent until it sends one.</summary>
    public double Level(string id) => _levels.TryGetValue(id, out double db) ? db : Engine.LevelMeter.Silent;

    /// <summary>One line of the engine's levels: "id:dB" pairs between "|" (an id may hold a colon or a space: the last colon splits).</summary>
    public static List<(string Id, double Db)> Parse(string line)
    {
        var levels = new List<(string, double)>();
        foreach (string pair in line.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = pair.LastIndexOf(':');
            if (colon > 0 && double.TryParse(pair.AsSpan(colon + 1), System.Globalization.CultureInfo.InvariantCulture, out double db))
                levels.Add((pair[..colon], db));
        }
        return levels;
    }

    /// <summary>A level changed (raised on the reading thread; the same level again raises nothing).</summary>
    public event Action? Changed;

    internal void Push(IReadOnlyList<(string Id, double Db)> levels)
    {
        bool changed = false;
        foreach (var (id, db) in levels)
            if (!_levels.TryGetValue(id, out double old) || old != db)
            {
                _levels[id] = db;
                changed = true;
            }
        if (changed)
        {
            Interlocked.MemoryBarrier(); // the new level is seen before a meter's "resting" is read
            Changed?.Invoke();
        }
    }

    void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                client.Connect(1000);
                using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
                writer.WriteLine("levels");
                while (!_cts.IsCancellationRequested && reader.ReadLine() is { } line)
                    Push(Parse(line));
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
            }
            Push(_levels.Keys.Select(id => (id, Engine.LevelMeter.Silent)).ToList());
            _cts.Token.WaitHandle.WaitOne(1000);
        }
    }

    public void Dispose() => _cts.Cancel();
}

public static class PipeClient
{
    /// <summary>Sends one command to the running engine. Null if it is not running or did not answer.</summary>
    public static string? Send(string command, int timeoutMs = 3000, string? pipeName = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? Ipc.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            writer.WriteLine(command);
            // "apply" rebuilds the pipeline, so allow it more time than the connect.
            return reader.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs * 5)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="Send"/> without holding a thread while the engine answers (overlapped pipe I/O): the settings window
    /// asks for the status twice a second, and a thread blocked on each answer cost about 7 ms of CPU every time.
    /// </summary>
    public static async Task<string?> SendAsync(string command, int timeoutMs = 3000, string? pipeName = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs * 5));
            await using var client = new NamedPipeClientStream(".", pipeName ?? Ipc.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeoutMs, cts.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(command.AsMemory(), cts.Token).ConfigureAwait(false);
            return await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }
}
