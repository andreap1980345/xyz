using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessLauncher;

internal sealed record ObservedCall(DateTime Time, int Pid, int Tid, uint Kind, bool Success,
    uint Error, uint Access, uint Disposition, uint Flags, ulong Sequence, ulong Handle,
    ulong Requested, ulong Transferred, ulong Dropped, ulong Address, ulong Buffer, string Path)
{
    public bool IsNative => Kind is >= 8 and <= 12;
    public string Architecture => Disposition == 32 ? "x86 / WOW64" : "x64";
    public string Api => Kind switch { 0 => "Hook attivi", 1 => "CreateFileW", 2 => "ReadFile", 3 => "WriteFile", 4 => "CloseHandle", 5 => "DeleteFileW", 6 => "CreateFileA", 7 => "DeleteFileA", 8 => "NtQueryVirtualMemory", 9 => "NtQuerySystemInformation", 10 => "NtQueryInformationProcess", 11 => "NtReadVirtualMemory", 12 => "NtWriteVirtualMemory", _ => $"Evento {Kind}" };
    public string Parameters => Kind switch
    {
        >= 8 and <= 12 => $"h=0x{Handle:X}; indirizzo=0x{Address:X}; buffer=0x{Buffer:X}; dimensione={Requested}" + (Kind <= 10 ? $"; classe={ClassName} ({Access})" : ""),
        1 or 6 => $"Accesso 0x{Access:X8}; {DispositionName}",
        2 or 3 => $"h=0x{Handle:X}; richiesti {Requested} byte{((Flags & 1) != 0 ? "; asincrono" : "")}",
        4 => $"h=0x{Handle:X}",
        _ => ""
    };
    public string Result => IsNative ? $"NTSTATUS 0x{Error:X8} ({(Success ? "NT_SUCCESS" : "errore/avviso")})"
        : Kind == 0 ? Success ? "ATTIVI" : "ERRORE"
        : !Success && Error == 997 ? "IN ATTESA (I/O)"
        : Kind is 1 or 6 ? Success ? $"HANDLE 0x{Handle:X}" : "INVALID_HANDLE_VALUE"
        : Success ? "TRUE" : "FALSE";
    public string ErrorText => IsNative ? Error switch { 0 => "STATUS_SUCCESS", 0xC0000003 => "STATUS_INVALID_INFO_CLASS", 0xC0000004 => "STATUS_INFO_LENGTH_MISMATCH", 0xC0000005 => "STATUS_ACCESS_VIOLATION", 0xC0000008 => "STATUS_INVALID_HANDLE", 0xC000000D => "STATUS_INVALID_PARAMETER", 0xC0000022 => "STATUS_ACCESS_DENIED", 0xC0000023 => "STATUS_BUFFER_TOO_SMALL", 0x8000000D => "STATUS_PARTIAL_COPY", _ => $"NTSTATUS 0x{Error:X8}" }
        : Error == 0 ? "" : $"{Error}: {new Win32Exception((int)Error).Message}";
    public string Bytes => IsNative ? (Flags & 2) != 0 ? Transferred.ToString() : "Non disponibile"
        : Kind is 2 or 3 ? (Flags & 2) != 0 ? Transferred.ToString() : "Non noto / asincrono" : "";
    private string DispositionName => Disposition switch { 1 => "CREATE_NEW", 2 => "CREATE_ALWAYS", 3 => "OPEN_EXISTING", 4 => "OPEN_ALWAYS", 5 => "TRUNCATE_EXISTING", _ => Disposition.ToString() };
    private string ClassName => (Kind, Access) switch
    {
        (8, 0) => "MemoryBasicInformation", (8, 2) => "MemoryMappedFilenameInformation",
        (9, 0) => "SystemBasicInformation", (9, 5) => "SystemProcessInformation",
        (9, 11) => "SystemModuleInformation", (9, 16) => "SystemHandleInformation",
        (10, 0) => "ProcessBasicInformation", (10, 7) => "ProcessDebugPort",
        (10, 26) => "ProcessWow64Information", (10, 27) => "ProcessImageFileName",
        _ => "classe numerica"
    };
}

internal sealed class ObserverSession : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<ObservedCall> pending = new();
    private readonly Task reader;
    private int queued;
    private int expectedPid;
    private long dropped;
    private long nativeDropped;
    private string state = "Hook predisposto; in attesa di Riprendi";
    private SafeProcessHandle? observedProcess;
    private readonly uint features;
    public string PipeName { get; } = $"ProcessLauncher-Observer-{Guid.NewGuid():N}";
    public int ProcessId => Volatile.Read(ref expectedPid);
    public string State
    {
        get
        {
            if (observedProcess is { IsInvalid: false, IsClosed: false }
                && WaitForSingleObject(observedProcess, 0) == 0 && GetExitCodeProcess(observedProcess, out uint code))
                return $"Processo terminato: uscita 0x{code:X8}{(code == 0xC00000FD ? " (STATUS_STACK_OVERFLOW)" : "")}{(!HookReady && features != 0 ? "; prima della connessione DLL" : "")}";
            return Volatile.Read(ref state);
        }
    }
    public long Dropped => Interlocked.Read(ref dropped) + Interlocked.Read(ref nativeDropped);
    public bool HookReady { get; private set; }

    public ObserverSession(uint features = 3)
    {
        this.features = features;
        if (features == 0) state = "Nessun hook; controllo dello stato del processo";
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 65536, 0, security);
        reader = Task.Run(ReadEventsAsync);
    }

    public void Bind(int pid)
    {
        observedProcess = OpenProcess(0x00101000, false, pid);
        Volatile.Write(ref expectedPid, pid);
    }

    public List<ObservedCall> Drain(int maximum = 512)
    {
        var events = new List<ObservedCall>();
        while (events.Count < maximum && pending.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref queued);
            events.Add(item);
        }
        return events;
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            var token = lifetime.Token;
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != ProcessId)
                throw new InvalidDataException("La DLL connessa non appartiene al processo osservato.");
            Volatile.Write(ref state, "DLL collegata");
            byte[] data = new byte[1128];
            while (!token.IsCancellationRequested)
            {
                await pipe.ReadExactlyAsync(data, token).ConfigureAwait(false);
                uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
                if (U32(0) != 0x414F4253 || U32(4) != data.Length || U32(12) != pid)
                    throw new InvalidDataException("Evento della DLL non valido.");
                var call = new ObservedCall(DateTime.FromFileTimeUtc(checked((long)U64(48))).ToLocalTime(),
                    (int)pid, (int)U32(16), U32(8), U32(20) != 0, U32(24), U32(28), U32(32), U32(36),
                    U64(40), U64(56), U64(64), U64(72), U64(80), U64(88), U64(96), Encoding.Unicode.GetString(data, 104, 1024).TrimEnd('\0'));
                Interlocked.Exchange(ref nativeDropped, checked((long)call.Dropped));
                if (call.Kind == 0) { HookReady = call.Success; Volatile.Write(ref state, $"{call.Path} ({call.Architecture})"); }
                if (Interlocked.Increment(ref queued) <= 4096) pending.Enqueue(call);
                else { Interlocked.Decrement(ref queued); Interlocked.Increment(ref dropped); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (EndOfStreamException) { Volatile.Write(ref state, "Processo terminato / canale chiuso"); }
        catch (Exception e) { if (!lifetime.IsCancellationRequested) Volatile.Write(ref state, $"Osservazione interrotta: {e.Message}"); }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        pipe.Dispose();
        observedProcess?.Dispose();
        // Il task termina per cancellazione; non blocchiamo il thread della GUI.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
}
