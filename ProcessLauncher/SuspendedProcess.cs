using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcessLauncher;

// Gestisce solo un processo nuovo creato da noi con CREATE_SUSPENDED.
internal sealed class SuspendedProcess : IDisposable
{
    private readonly SafeProcessHandle process;
    private readonly SafeWaitHandle thread;
    private bool disposed;
    private readonly object sync = new();
    private CancellationTokenRegistration lifetimeRegistration;

    public ExplorerLauncher.CreatedProcess Info { get; }
    public int ThreadId { get; }
    public bool IsSuspended { get; private set; } = true;
    public bool HasExited { get { lock (sync) return disposed || WaitForSingleObject(process, 0) == 0; } }

    internal void BindLifetime(CancellationToken token)
    {
        lock (sync)
        {
            if (disposed) return;
            lifetimeRegistration = token.Register(static state => ((SuspendedProcess)state!).Dispose(), this);
            if (disposed) lifetimeRegistration.Unregister();
        }
    }

    internal sealed record Transfer(long ProcessHandle, long ThreadHandle,
        ExplorerLauncher.CreatedProcess Info, int ThreadId);

    private SuspendedProcess(Transfer transfer)
    {
        process = new SafeProcessHandle((nint)transfer.ProcessHandle, true);
        thread = new SafeWaitHandle((nint)transfer.ThreadHandle, true);
        Info = transfer.Info;
        ThreadId = transfer.ThreadId;
    }

    internal static SuspendedProcess Import(Transfer transfer) => new(transfer);

    internal Transfer DuplicateTo(int recipientPid)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var recipient = OpenProcess(0x0040, false, recipientPid);
        if (recipient.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!DuplicateHandle(GetCurrentProcess(), process.DangerousGetHandle(), recipient,
            out nint processCopy, 0x00101001, false, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!DuplicateHandle(GetCurrentProcess(), thread.DangerousGetHandle(), recipient,
            out nint threadCopy, 0x0002, false, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (DuplicateHandle(recipient.DangerousGetHandle(), processCopy, GetCurrentProcess(),
                out nint recovered, 0, false, 1)) CloseHandle(recovered);
            throw new Win32Exception(error);
        }
        return new Transfer((long)processCopy, (long)threadCopy, Info, ThreadId);
    }

    internal void ReleaseTransferredOwnership()
    {
        // Dopo l'ACK gli handle nella GUI diventano proprietari della sospensione.
        IsSuspended = false;
        Dispose();
    }

    private SuspendedProcess(NativeInfo native, int parentPid, string workingDirectory)
    {
        process = new SafeProcessHandle(native.Process, true);
        thread = new SafeWaitHandle(native.Thread, true);
        ThreadId = native.ThreadId;
        Info = new ExplorerLauncher.CreatedProcess(native.ProcessId, parentPid, Environment.ProcessId,
            ProcessCatalog.StartTime(native.ProcessId), workingDirectory);
    }

    public static SuspendedProcess Create(string executable, string arguments, string? directory, bool elevatedLocal = false,
        string? observerPipe = null, int observerReceiverPid = 0, uint observerFeatures = 3)
    {
        var settings = ExplorerLauncher.Validate(executable, directory);
        int parentPid;
        NativeInfo native;
        int result = elevatedLocal
            ? CreateElevatedSuspendedWithExplorerParent(settings.Path, arguments, settings.Directory, out parentPid, out native)
            : CreateSuspendedWithExplorerParent(settings.Path, arguments, settings.Directory, out parentPid, out native);
        Marshal.ThrowExceptionForHR(result);
        var child = new SuspendedProcess(native, parentPid, settings.Directory);
        try
        {
            if (!string.IsNullOrWhiteSpace(observerPipe))
            {
                result = PrepareFileObserver(child.process, observerPipe,
                    observerReceiverPid == 0 ? Environment.ProcessId : observerReceiverPid, observerFeatures);
                Marshal.ThrowExceptionForHR(result);
            }
            return child;
        }
        catch { child.Dispose(); throw; }
    }

    // Restituisce il precedente contatore di sospensione, come ResumeThread.
    public uint Resume()
    {
        lock (sync)
        {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (HasExited) throw new InvalidOperationException("Il processo sospeso e gia terminato.");
        if (!IsSuspended) throw new InvalidOperationException("Il thread principale e gia stato ripreso.");
        uint previous = ResumeThread(thread);
        if (previous == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        IsSuspended = previous > 1;
        return previous;
        }
    }

    public void Terminate()
    {
        lock (sync)
        {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsSuspended) throw new InvalidOperationException("Il processo e gia stato ripreso.");
        if (!HasExited)
        {
            if (!TerminateProcess(process, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (WaitForSingleObject(process, 5000) != 0)
                throw new InvalidOperationException("Il processo non ha ancora completato la terminazione.");
        }
        IsSuspended = false;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
        if (disposed) return;
        // Evita di lasciare processi mai avviati senza un proprietario della sospensione.
        if (IsSuspended && !HasExited)
        {
            TerminateProcess(process, 1);
            WaitForSingleObject(process, 5000);
        }
        thread.Dispose();
        process.Dispose();
        disposed = true;
        lifetimeRegistration.Unregister();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInfo
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("ExplorerBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateSuspendedWithExplorerParent(string executable, string arguments,
        string workingDirectory, out int parentPid, out NativeInfo child);
    [DllImport("ExplorerBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateElevatedSuspendedWithExplorerParent(string executable, string arguments,
        string workingDirectory, out int parentPid, out NativeInfo child);
    [DllImport("ExplorerBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PrepareFileObserver(SafeProcessHandle process, string pipeName, int receiverPid, uint features);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint source, SafeProcessHandle target,
        out nint targetHandle, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint target,
        out nint targetHandle, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
