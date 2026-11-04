namespace ProcessLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--suspended-helper" && int.TryParse(args[2], out int guiPid))
            return ElevatedSuspension.RunHelperAsync(args[1], guiPid).GetAwaiter().GetResult();
        if (args.Length is 1 or 2 or 3 && args[0] == "--probe-child")
        {
            bool directoryMatches = args.Length == 1 || string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1])),
                Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory), StringComparison.OrdinalIgnoreCase);
            bool elevationMatches = args.Length < 3 || args[2] == "--require-elevation"
                && ProcessCatalog.Security(Environment.ProcessId).Elevated == true;
            Thread.Sleep(3000);
            return directoryMatches && elevationMatches ? 0 : 1;
        }
        if (args.Length == 1 && args[0] == "--self-test")
            return SelfTest.Run();
        if (args.Length == 1 && args[0] == "--self-test-from-explorer")
            return SelfTest.RunFromExplorer();
        if (args.Length == 1 && args[0] == "--self-test-elevation")
            return SelfTest.RunElevation();
        if (args.Length == 1 && args[0] == "--self-test-suspended-elevation-local")
            return SelfTest.RunSuspendedElevationLocal();
        if (args.Length == 1 && args[0] == "--self-test-suspended-elevation")
            return SelfTest.RunSuspendedElevation();
        if (args.Length == 1 && args[0] == "--self-test-suspended-elevation-from-explorer")
            return SelfTest.RunFromExplorer("--self-test-suspended-elevation");
        if (args.Length == 1 && args[0] == "--self-test-observer")
            return SelfTest.RunObserver();
        if (args.Length == 1 && args[0] == "--self-test-observer-elevation")
            return SelfTest.RunObserver(elevated: true);
        if (args.Length == 1 && args[0] == "--self-test-observer-elevation-from-explorer")
            return SelfTest.RunFromExplorer("--self-test-observer-elevation");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
