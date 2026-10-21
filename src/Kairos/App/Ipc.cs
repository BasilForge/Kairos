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
    Engine.Updates.UpdateStatus? Update = null);

public sealed record CapsDto(
    List<MonitorInfo> Monitors,
    List<NameValue> Windows,
    List<NameValue> Mics,
    List<NameValue> Speakers,
    List<string> Encoders,
    string LibobsVersion);

public static class Ipc
{
    /// <summary>The engine's pipe. A development copy with its own data folder (KAIROS_DATA) gets a pipe of its own, so it never talks to the real one.</summary>
    public static readonly string PipeName = AppInfo.PipeName +
        (Environment.GetEnvironmentVariable("KAIROS_DATA") is { Length: > 0 } dev ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(dev)))[..8] : "");
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
    readonly Func<(double System, double Mic)> _levels;
    readonly CancellationTokenSource _cts = new();
    readonly string _name;
    /// <summary>A client that connects but sends no command is dropped after this, so it cannot hold a connection slot.</summary>
    readonly TimeSpan _idleTimeout;

    /// <param name="onRequest">Told each request and the id of the process that sent it (the engine logs who asked it to exit).</param>
    public PipeServer(Func<string, string> handle, Func<(double System, double Mic)> levels, string? pipeName = null, TimeSpan? idleTimeout = null,
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
                    while (!_cts.IsCancellationRequested && server.IsConnected)
                    {
                        var (system, mic) = _levels();
                        await writer.WriteLineAsync(FormattableString.Invariant($"{system:F1} {mic:F1}"));
                        await Task.Delay(LevelInterval, _cts.Token);
                    }
                }
                else if (request != null)
                {
                    if (_onRequest != null && OperatingSystem.IsWindows() && GetNamedPipeClientProcessId(server.SafePipeHandle, out uint client))
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
    double _system = Engine.LevelMeter.Silent, _mic = Engine.LevelMeter.Silent;

    public LevelStream() : this(connect: true)
    {
    }

    /// <param name="connect">False: no engine, the levels come from <see cref="Push"/> (tests).</param>
    internal LevelStream(bool connect)
    {
        if (connect)
            new Thread(Run) { IsBackground = true, Name = "Kairos levels" }.Start();
    }

    public double SystemDb => Volatile.Read(ref _system);
    public double MicDb => Volatile.Read(ref _mic);

    /// <summary>A level changed (raised on the reading thread; the same level again raises nothing).</summary>
    public event Action? Changed;

    internal void Push(double system, double mic)
    {
        bool changed = system != Volatile.Read(ref _system) || mic != Volatile.Read(ref _mic);
        Volatile.Write(ref _system, system);
        Volatile.Write(ref _mic, mic);
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
                {
                    string[] parts = line.Split(' ');
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out double s) &&
                        double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out double m))
                        Push(s, m);
                }
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
            }
            Push(Engine.LevelMeter.Silent, Engine.LevelMeter.Silent);
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
