using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessLauncher;

internal sealed record ProcessEntry(int Id, int ParentId, string Name)
{
    public override string ToString() => $"{Name}  ·  PID {Id}";
}

internal static class ProcessCatalog
{
    public static List<ProcessEntry> Snapshot()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var native = new NativeEntry { Size = (uint)Marshal.SizeOf<NativeEntry>() };
        var result = new List<ProcessEntry>();
        if (!Process32FirstW(snapshot, ref native))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            result.Add(new ProcessEntry((int)native.Id, (int)native.ParentId, native.Name ?? "[sconosciuto]"));
        } while (Process32NextW(snapshot, ref native));
        int error = Marshal.GetLastWin32Error();
        if (error != 18) throw new Win32Exception(error);
        return result;
    }

    public static DateTime? StartTime(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    public static string? ExecutablePath(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        return QueryFullProcessImageNameW(process, 0, path, ref length) ? path.ToString() : null;
    }

    internal sealed record SecurityInfo(bool? Elevated, string Integrity);

    public static SecurityInfo Security(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token))
            return new SecurityInfo(null, "Non disponibile");
        using (token)
        {
            bool? elevated = null;
            nint buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (GetTokenInformation(token, 20, buffer, sizeof(int), out _))
                    elevated = Marshal.ReadInt32(buffer) != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }

            GetTokenInformation(token, 25, 0, 0, out uint needed);
            if (needed == 0) return new SecurityInfo(elevated, "Non disponibile");
            buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetTokenInformation(token, 25, buffer, needed, out _))
                    return new SecurityInfo(elevated, "Non disponibile");
                nint sid = Marshal.ReadIntPtr(buffer);
                nint countPointer = GetSidSubAuthorityCount(sid);
                if (countPointer == 0) return new SecurityInfo(elevated, "Non disponibile");
                byte count = Marshal.ReadByte(countPointer);
                if (count == 0) return new SecurityInfo(elevated, "Non disponibile");
                nint ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
                if (ridPointer == 0) return new SecurityInfo(elevated, "Non disponibile");
                uint rid = unchecked((uint)Marshal.ReadInt32(ridPointer));
                string integrity = rid switch
                {
                    >= 0x5000 => "Protected",
                    >= 0x4000 => "System",
                    >= 0x3000 => "High",
                    >= 0x2100 => "Medium Plus",
                    >= 0x2000 => "Medium",
                    >= 0x1000 => "Low",
                    _ => "Untrusted"
                };
                return new SecurityInfo(elevated, integrity);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public static bool IsVerifiedParent(ProcessEntry child, ProcessEntry parent)
    {
        if (child.ParentId != parent.Id) return false;
        var childStart = StartTime(child.Id);
        var parentStart = StartTime(parent.Id);
        return childStart.HasValue && parentStart.HasValue && parentStart <= childStart;
    }

    public static List<ProcessEntry> FindLaunched(string path, HashSet<int> previousPids) =>
        Snapshot().Where(p => !previousPids.Contains(p.Id)
            && string.Equals(p.Name, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
            && string.Equals(ExecutablePath(p.Id), path, StringComparison.OrdinalIgnoreCase)).ToList();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeEntry
    {
        public uint Size, Usage, Id;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? Name;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        StringBuilder name, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        nint buffer, uint length, out uint needed);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref NativeEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref NativeEntry entry);
}
