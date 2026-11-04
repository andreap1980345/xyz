using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ProcessLauncher;

// Helper monouso della stessa app: comunica esclusivamente in memoria.
internal static class ElevatedSuspension
{
    private sealed record Request(string Executable, string Arguments, string WorkingDirectory, string? ObserverPipe, uint ObserverFeatures);
    private sealed record Response(SuspendedProcess.Transfer? Process, string? Error);

    public static async Task<SuspendedProcess> CreateAsync(string executable, string arguments,
        string? workingDirectory, CancellationToken cancellationToken = default, string? observerPipe = null, uint observerFeatures = 3)
    {
        var settings = ExplorerLauncher.Validate(executable, workingDirectory);
        string pipeName = $"ProcessLauncher-Elevated-{Guid.NewGuid():N}";
        var identity = WindowsIdentity.GetCurrent();
        using (identity)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
            };
            start.ArgumentList.Add("--suspended-helper");
            start.ArgumentList.Add(pipeName);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var helper = await Task.Run(() => Process.Start(start)
                ?? throw new InvalidOperationException("Impossibile avviare l'helper amministratore."), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer)
                || peer != helper.Id) throw new InvalidOperationException("L'helper connesso non corrisponde a quello avviato con UAC.");
            await WriteAsync(pipe, new Request(settings.Path, arguments, settings.Directory, observerPipe, observerFeatures), token).ConfigureAwait(false);
            var response = await ReadAsync<Response>(pipe, token).ConfigureAwait(false);
            if (response.Process is null) throw new InvalidOperationException(response.Error ?? "Creazione elevata non riuscita.");
            var child = SuspendedProcess.Import(response.Process);
            child.BindLifetime(cancellationToken);
            try
            {
                if (child.Info.CreatorId != helper.Id || child.HasExited)
                    throw new InvalidOperationException("Il processo ricevuto non corrisponde alla creazione richiesta.");
                await WriteAsync(pipe, "accepted", token).ConfigureAwait(false);
                if (await ReadAsync<string>(pipe, token).ConfigureAwait(false) != "transferred")
                    throw new InvalidOperationException("Passaggio della sospensione non confermato.");
                await helper.WaitForExitAsync(token).ConfigureAwait(false);
                if (helper.ExitCode != 0) throw new InvalidOperationException("L'helper amministratore non ha completato l'operazione.");
                return child;
            }
            catch { child.Dispose(); throw; }
        }
    }

    public static async Task<int> RunHelperAsync(string pipeName, int guiPid)
    {
        try
        {
            if (ProcessCatalog.Security(Environment.ProcessId).Elevated != true) return 1;
            if (!string.Equals(ProcessCatalog.ExecutablePath(guiPid), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return 1;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            // Identification impedisce al server di impersonare il client elevato.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != guiPid) return 1;
            try
            {
                var request = await ReadAsync<Request>(pipe, token).ConfigureAwait(false);
                using var child = SuspendedProcess.Create(request.Executable, request.Arguments,
                    request.WorkingDirectory, elevatedLocal: true, observerPipe: request.ObserverPipe, observerReceiverPid: guiPid,
                    observerFeatures: request.ObserverFeatures);
                if (ProcessCatalog.Security(child.Info.Id).Elevated != true)
                    throw new InvalidOperationException("Windows non ha assegnato privilegi elevati al figlio.");
                var transfer = child.DuplicateTo(guiPid);
                await WriteAsync(pipe, new Response(transfer, null), token).ConfigureAwait(false);
                if (await ReadAsync<string>(pipe, token).ConfigureAwait(false) != "accepted") return 1;
                child.ReleaseTransferredOwnership();
                await WriteAsync(pipe, "transferred", token).ConfigureAwait(false);
                return 0;
            }
            catch (Exception error)
            {
                try { await WriteAsync(pipe, new Response(null, error.Message), token).ConfigureAwait(false); }
                catch (Exception) { }
                return 1;
            }
        }
        catch (Exception) { return 1; }
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
        if (body.Length > 1024 * 1024) throw new InvalidDataException("Richiesta troppo grande.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidDataException("Risposta non valida.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidDataException("Risposta vuota.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
