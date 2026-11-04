using System.ComponentModel;
using System.Diagnostics;

if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
{
    Console.Error.WriteLine("Questa versione richiede Windows e un processo a 64 bit.");
    return 1;
}

if (args.Length > 0)
{
    if (args.Length != 1 || !int.TryParse(args[0], out int selectedPid) || selectedPid < 0)
    {
        Console.Error.WriteLine("Uso: dotnet run -- <PID>");
        return 1;
    }

    return MemoryMap.Show(selectedPid);
}

// Salviamo solo nome e PID: i processi possono terminare durante la lettura.
var entries = new List<(int Pid, string Name)>();

foreach (var process in Process.GetProcesses())
{
    using (process)
    {
        int pid = process.Id;
        string name;

        try
        {
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            name = "[nome non disponibile]";
        }

        entries.Add((pid, name));
    }
}

Console.WriteLine($"{"PID",8}  NOME PROCESSO");
Console.WriteLine(new string('-', 50));

foreach (var entry in entries.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(p => p.Pid))
{
    Console.WriteLine($"{entry.Pid,8}  {entry.Name}");
}

while (true)
{
    Console.Write("\nInserisci un PID per esplorare la memoria (invio per uscire): ");
    string? input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
        return 0;

    if (!int.TryParse(input, out int selectedPid) || selectedPid < 0)
    {
        Console.WriteLine("Inserisci un PID numerico valido.");
        continue;
    }

    MemoryMap.Show(selectedPid);
}
