using System.Runtime.InteropServices;
using System.Text;
using Kairos.Native;

namespace Kairos.Engine;

/// <summary>Routes libobs and Kairos log lines into one rotating file.</summary>
public static class ObsLog
{
    const ulong StandardSnprintfBehavior = 0x2;
    const int KeepFiles = 5;

    static readonly object Gate = new();
    static StreamWriter? _writer;
    static ObsLogHandler? _handler;

    public static string? CurrentPath { get; private set; }

    public static string FileName(DateTime time, string prefix = "kairos") =>
        $"{prefix}-{time.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}.log";

    /// <param name="prefix">"kairos" for the engine; "settings" for the settings window, so each keeps its own last files.</param>
    public static void Open(string logDir, string prefix = "kairos")
    {
        if (_writer != null)
            return;
        Directory.CreateDirectory(logDir);
        foreach (var old in new DirectoryInfo(logDir).GetFiles(prefix + "-*.log")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles - 1))
        {
            try { old.Delete(); } catch (IOException) { }
        }

        CurrentPath = Path.Combine(logDir, FileName(DateTime.Now, prefix));
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = new StreamWriter(new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    public static void HookLibobs()
    {
        _handler = OnObsLog;
        Obs.base_set_log_handler(_handler, IntPtr.Zero);
    }

    public static void Info(string message) => Write("info", message);
    public static void Warn(string message) => Write("warn", message);
    public static void Error(string message) => Write("error", message);

    static void Write(string level, string message)
    {
        lock (Gate)
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {Redact(message, Profile)}");
    }

    /// <summary>The user's folder (C:\Users\name): its name is personal, and logs are sent to us.</summary>
    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// <paramref name="line"/> without the user's folder <paramref name="profile"/> (either slash, any case, or as JSON
    /// writes it): a log or report sent for help carries no one's name. Kairos's and libobs's lines alike.
    /// </summary>
    public static string Redact(string line, string profile)
    {
        if (profile.Length < 4)
            return line;
        return line.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile.Replace(@"\", @"\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>NVENC's test said the NVIDIA driver is older than the SDK libobs needs (NVENC is then not there).</summary>
    public static bool NvencOutdated { get; private set; }

    /// <summary>The sound devices libobs reports trouble with (read from its own log lines).</summary>
    public static readonly AudioDeviceTrouble AudioDevices = new();

    static unsafe void OnObsLog(int level, IntPtr format, IntPtr args, IntPtr param)
    {
        if (level > ObsConst.LogInfo)
            return;
        try
        {
            byte* buffer = stackalloc byte[4096];
            int n = __stdio_common_vsprintf(StandardSnprintfBehavior, buffer, 4096, format, IntPtr.Zero, args);
            string text = n < 0 ? Marshal.PtrToStringUTF8(format) ?? "" : Encoding.UTF8.GetString(buffer, Math.Min(n, 4095));
            Write(level switch { ObsConst.LogError => "obs-error", ObsConst.LogWarning => "obs-warn", _ => "obs" }, text);
            if (text.StartsWith("WASAPI: Device", StringComparison.Ordinal) || text.StartsWith("Device '", StringComparison.Ordinal))
                AudioDevices.Note(text);
            else if (text.Contains("outdated_driver", StringComparison.Ordinal))
                NvencOutdated = true; // "[NVENC] Test process failed: outdated_driver": NVENC did not load
        }
        catch
        {
            // Never let an exception cross back into libobs.
        }
    }

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern unsafe int __stdio_common_vsprintf(ulong options, byte* buffer, nuint bufferCount, IntPtr format, IntPtr locale, IntPtr args);
}
