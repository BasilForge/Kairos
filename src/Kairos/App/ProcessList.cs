using System.Runtime.InteropServices;

namespace Kairos.App;

/// <summary>The running processes, read from one snapshot without opening any of them.</summary>
public static class ProcessList
{
    /// <summary>Walks the processes (id and name without ".exe") until <paramref name="visit"/> says stop; true if it did.</summary>
    public static bool Any(Func<uint, string, bool> visit) => Any((pid, _, name) => visit(pid, name));

    /// <summary>Every running process: id, parent id, name without ".exe".</summary>
    public static List<Engine.Audio.ProcInfo> All()
    {
        var all = new List<Engine.Audio.ProcInfo>(400);
        Any((pid, parent, name) =>
        {
            all.Add(new(pid, parent, name));
            return false;
        });
        return all;
    }

    /// <summary>
    /// Whether game capture can open process <paramref name="pid"/> as libobs does (query and wait rights): a game run as
    /// administrator, or one its anti-cheat guards, cannot be hooked by Kairos, which never asks for administrator.
    /// </summary>
    public static bool CanBeHooked(uint pid)
    {
        IntPtr h = OpenProcess(0x0400 /* PROCESS_QUERY_INFORMATION */ | 0x00100000 /* SYNCHRONIZE */, false, pid);
        if (h == IntPtr.Zero)
            return false;
        CloseHandle(h);
        return true;
    }

    /// <summary>Process <paramref name="pid"/>'s name (without ".exe") from a snapshot, which needs no rights on it; null when gone.</summary>
    public static string? NameOf(uint pid)
    {
        string? name = null;
        Any((id, exe) =>
        {
            if (id != pid)
                return false;
            name = exe;
            return true;
        });
        return name;
    }

    /// <summary>When a process started (file time), or null when it is gone or cannot be asked; opens it for that only.</summary>
    public static long? StartedAt(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            return GetProcessTimes(h, out long created, out _, out _, out _) ? created : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>Kairos's own process name (the engine's and the settings window's), without ".exe".</summary>
    public static string SelfName { get; } = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? AppInfo.Name;

    /// <summary>Walks the processes (id, parent id, name without ".exe") until <paramref name="visit"/> says stop.</summary>
    public static bool Any(Func<uint, uint, string, bool> visit)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
            return false;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                if (visit(entry.ProcessId, entry.ParentProcessId, Path.GetFileNameWithoutExtension(entry.ExeFile)))
                    return true;
            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    const uint SnapProcess = 0x2;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
