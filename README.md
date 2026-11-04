# Esploratore dei processi

Per l'interfaccia grafica aggiornata apri `ProcessLauncher\app-exercise\ProcessLauncher.exe`.
Permette di scegliere eseguibile e cartella di lavoro, aprire il processo tramite Explorer e controllare PID,
padre e figli. Istruzioni in [ProcessLauncher/README.md](ProcessLauncher/README.md).

La cartella [ExplorerExtension](ExplorerExtension/README.md) contiene anche una DLL
C++ dimostrativa per il menu contestuale di Explorer, con contatore e indirizzo in memoria.

Applicazione da terminale in C#: elenca i processi locali con PID e nome,
poi permette di esplorare le loro regioni di memoria virtuale.

## Avvio

Richiede Windows a 64 bit e .NET SDK 10 a 64 bit. Dalla cartella del progetto:

```powershell
dotnet run
```

Il programma stampa la lista: inserisci un PID per mostrarne la mappa della memoria.
Puoi inserire altri PID o premere invio per uscire.
Puoi anche aprire direttamente la mappa di un PID:

```powershell
dotnet run -- 1234
```

Sostituisci `1234` con un PID attualmente in esecuzione.
Il PID identifica un processo in esecuzione; Windows puo riutilizzarlo dopo la sua chiusura.

## Come funziona

`Process.GetProcesses()` restituisce i processi locali. Per ciascuno leggiamo
`Id` (PID) e `ProcessName` (nome), poi liberiamo l'oggetto `Process` con `using`.
Liberare l'oggetto non chiude il processo: rilascia solo le risorse usate per osservarlo.
Se il nome non e disponibile, la riga conserva il PID e mostra un segnaposto.

Documentazione: [Process.GetProcesses](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.getprocesses?view=net-10.0).

## Mappa della memoria

`MemoryMap.cs` richiama le API native tramite P/Invoke. `OpenProcess` richiede
solo `PROCESS_QUERY_INFORMATION`, poi `VirtualQueryEx` percorre le regioni
avanzando di volta in volta di `RegionSize`. Il relativo handle viene chiuso con `using`.

Per ogni regione vengono mostrati indirizzo iniziale esadecimale, dimensione in byte,
stato (libera, riservata, allocata), tipo e protezioni. Le protezioni non sono
definite per le regioni libere o riservate: in quei casi viene mostrato `-`.
Il riepilogo di memoria allocata (commit) e riservata non misura la RAM residente.
La mappa cambia mentre il processo funziona e non costituisce una fotografia atomica.

Alcuni processi impediscono l'accesso; il programma mostra l'errore di Windows.
Anche un processo che termina durante la scansione puo causare un errore.
Questa versione mostra i metadati delle regioni; non legge o modifica i loro byte.

Documentazione: [VirtualQueryEx](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualqueryex),
[MEMORY_BASIC_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-memory_basic_information).
