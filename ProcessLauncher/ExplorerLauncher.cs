using System.Runtime.InteropServices;

namespace ProcessLauncher;

internal static class ExplorerLauncher
{
    internal sealed record CreatedProcess(int Id, int ParentId, int CreatorId, DateTime? StartedUtc,
        string WorkingDirectory);

    public static uint Launch(string path, string arguments, string? workingDirectory = null, bool runAsAdmin = false)
    {
        var launch = Validate(path, workingDirectory);
        int result = LaunchViaExplorerWithOptions(launch.Path, arguments, launch.Directory, runAsAdmin, out uint explorerPid);
        Marshal.ThrowExceptionForHR(result);
        return explorerPid;
    }

    public static CreatedProcess CreateWithExplorerParent(string path, string arguments, string? workingDirectory = null)
    {
        var launch = Validate(path, workingDirectory);
        int result = CreateWithExplorerParentInDirectory(launch.Path, arguments, launch.Directory,
            out uint parentPid, out uint childPid);
        Marshal.ThrowExceptionForHR(result);
        return new CreatedProcess((int)childPid, (int)parentPid, Environment.ProcessId,
            ProcessCatalog.StartTime((int)childPid), launch.Directory);
    }

    internal static (string Path, string Directory) Validate(string path, string? workingDirectory)
    {
        path = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        if (!File.Exists(path) || !string.Equals(System.IO.Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Seleziona un file .exe esistente.");
        string directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? System.IO.Path.GetDirectoryName(path)!
            : System.IO.Path.GetFullPath(workingDirectory.Trim().Trim('"'));
        if (!System.IO.Directory.Exists(directory))
            throw new ArgumentException("Seleziona una cartella di lavoro esistente.");
        return (path, directory);
    }

    [DllImport("ExplorerBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LaunchViaExplorerWithOptions(string executable, string arguments,
        string workingDirectory, [MarshalAs(UnmanagedType.Bool)] bool elevated, out uint explorerPid);

    [DllImport("ExplorerBridge.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CreateWithExplorerParentInDirectory(string executable, string arguments,
        string workingDirectory, out uint parentPid, out uint childPid);
}
