using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class MemoryMap
{
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemFree = 0x10000;
    private const uint ProcessQueryInformation = 0x0400;

    public static int Show(int pid)
    {
        using var handle = OpenProcess(ProcessQueryInformation, false, pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"Impossibile aprire il PID {pid}: {new Win32Exception(error).Message} (errore {error}).");
            return 1;
        }

        Console.WriteLine($"\nMappa della memoria virtuale del PID {pid}");
        Console.WriteLine($"{"INDIRIZZO INIZIALE",18}  {"DIMENSIONE (byte)",20}  {"STATO",12}  {"TIPO",10}  PROTEZIONE");
        Console.WriteLine(new string('-', 100));

        nuint address = 0;
        ulong committed = 0, reserved = 0;
        int regions = 0;

        while (true)
        {
            nuint result = VirtualQueryEx(handle, address, out var info,
                (nuint)Marshal.SizeOf<MemoryBasicInformation>());
            if (result == 0)
            {
                int error = Marshal.GetLastWin32Error();
                // L'indirizzo oltre il limite dello spazio virtuale produce errore 87.
                if (error == 87 && regions > 0)
                    break;

                Console.Error.WriteLine($"Lettura interrotta a 0x{address:X16}: {new Win32Exception(error).Message} (errore {error}). La mappa puo essere incompleta.");
                return 1;
            }

            string state = info.State switch
            {
                MemCommit => "Allocata",
                MemReserve => "Riservata",
                MemFree => "Libera",
                _ => $"0x{info.State:X}"
            };
            string type = info.State == MemFree ? "-" : info.Type switch
            {
                0x20000 => "Privata",
                0x40000 => "Mappata",
                0x1000000 => "Immagine",
                _ => $"0x{info.Type:X}"
            };
            // Protect non e definito per le regioni libere o solo riservate.
            string protection = info.State == MemCommit ? DescribeProtection(info.Protect) : "-";
            Console.WriteLine($"0x{info.BaseAddress:X16}  {info.RegionSize,20:N0}  {state,12}  {type,10}  {protection}");

            regions++;
            if (info.State == MemCommit) committed += (ulong)info.RegionSize;
            if (info.State == MemReserve) reserved += (ulong)info.RegionSize;

            if (info.RegionSize == 0 || info.RegionSize > nuint.MaxValue - info.BaseAddress
                || info.BaseAddress + info.RegionSize <= address)
            {
                Console.Error.WriteLine("Scansione interrotta: intervallo di memoria non valido.");
                return 1;
            }
            address = info.BaseAddress + info.RegionSize;
        }

        Console.WriteLine($"\nRegioni: {regions:N0} | Allocata (commit): {committed:N0} byte | Riservata: {reserved:N0} byte");
        Console.WriteLine("Questi valori descrivono la memoria virtuale, non la RAM occupata.");
        Console.WriteLine("Il processo continua a funzionare: la mappa puo cambiare durante la scansione.");
        return 0;
    }

    private static string DescribeProtection(uint protection)
    {
        string description = (protection & 0xFF) switch
        {
            0x01 => "Nessun accesso",
            0x02 => "Lettura",
            0x04 => "Lettura/Scrittura",
            0x08 => "Lettura/Copia su scrittura",
            0x10 => "Esecuzione",
            0x20 => "Esecuzione/Lettura",
            0x40 => "Esecuzione/Lettura/Scrittura",
            0x80 => "Esecuzione/Lettura/Copia su scrittura",
            _ => $"0x{protection:X}"
        };
        if ((protection & 0x100) != 0) description += " + Guardia";
        if ((protection & 0x200) != 0) description += " + Senza cache";
        if ((protection & 0x400) != 0) description += " + Write combine";
        return description;
    }

    // Layout nativo a 64 bit: padding automatico prima di RegionSize e alla fine.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nuint BaseAddress;
        public nuint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(SafeProcessHandle process, nuint address,
        out MemoryBasicInformation buffer, nuint length);
}
