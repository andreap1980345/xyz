# Interfaccia del laboratorio processi

Apri `app-exercise\ProcessLauncher.exe` con un doppio clic.
Richiede Windows x64 e .NET Desktop Runtime 10.

## Avvio dalla cartella scelta

1. **Sfoglia**: scegli il programma `.exe`.
2. **Argomenti**: inserisci gli eventuali parametri.
3. **Cartella di lavoro**: scrivi il percorso oppure premi **Cartella…**.
4. Premi **Apri processo tramite explorer.exe** oppure **Crea Y con padre Explorer**.
5. **Controlla** aggiorna PID, padre e figli; clicca un nodo per i dettagli.

La cartella di lavoro iniziale e quella dell'eseguibile. Se scegli una cartella
personalizzata, viene mantenuta anche quando cambi l'eseguibile.
Lasciando il campo vuoto, viene usata la cartella dell'eseguibile.
Un percorso inesistente viene segnalato prima dell'avvio.

Esempio:

```text
Eseguibile:        C:\Andrea\processo.exe
Cartella di lavoro: C:\Andrea
```

Equivale ad avviare il programma dopo questi comandi PowerShell:

```powershell
Set-Location C:\Andrea
.\processo.exe
```

La directory corrente influisce sui percorsi relativi usati dal programma, per
esempio `Settings.json`, `config\` o `data\`. E distinta dal percorso del file exe.
La cavia `file.exe` mostra la propria cartella di lavoro effettiva nella finestra.

## Modalita ripristinate da app-exercise

- **Apri processo tramite explorer.exe** delega l'apertura all'oggetto COM del desktop
  di Explorer e passa la cartella scelta a `IShellDispatch2::ShellExecute`.
- **Crea Y con padre Explorer** crea il nuovo processo dalla GUI, specificando Explorer
  con `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` e passando la cartella a `CreateProcessW`.

## Eseguire come amministratore

Spunta **Esegui come amministratore (conferma UAC)** e premi
**Avvia come amministratore (UAC)**. Windows mostrera la richiesta di conferma.
L'apertura viene delegata a Explorer con il verbo `runas`, mantenendo eseguibile,
argomenti e cartella di lavoro selezionati. Non serve aprire prima la nostra GUI
come amministratore.

Con questa opzione il pulsante di assegnazione esplicita del padre e disabilitato:
per l'avvio elevato usa il pulsante principale. Il padre effettivamente assegnato
da Windows resta visibile nell'albero.
I dettagli mostrano **Amministratore: Si/No** e il livello di **Integrita**
letto dal token del processo (per esempio Medium oppure High).
Se i permessi non consentono la lettura, viene mostrato **Non disponibile**.

L'app usa una richiesta di accesso limitato per leggere il percorso degli eseguibili,
cosi da poter individuare anche i processi elevati quando Windows lo consente.

L'app seleziona automaticamente il nuovo PID quando e identificabile. In caso
di istanze riutilizzate o processi che terminano subito puoi inserire il PID
manualmente o usare **Elenco processi**.
I dettagli riportano la cartella richiesta all'avvio per il processo tracciato.
Il processo potrebbe poi cambiare da solo la propria directory corrente.

## Creare sospeso e riprendere

Nella stessa app `app-exercise`:

1. Scegli eseguibile, argomenti e cartella di lavoro.
2. Spunta **Avvio sospeso (CREATE_SUSPENDED)** e scegli la modalita di hook:
   **Nessun hook**, **Hook: solo file** (predefinita), oppure **Hook: file + Native API**.
   Se servono privilegi elevati,
   spunta anche **Esegui come amministratore (conferma UAC)**.
3. Premi **Crea processo sospeso** oppure **Crea sospeso come amministratore**.
   Nel secondo caso conferma la richiesta UAC. Il nuovo PID viene selezionato e i dettagli
   riportano il thread principale sospeso.
4. Premi **Riprendi** per consentire l'esecuzione, oppure **Termina sospeso**
   per chiudere quella nuova istanza senza riprenderla.

Per una prova visibile scegli il nostro `file.exe`: la sua finestra compare solo
dopo **Riprendi**. Puoi creare piu processi sospesi e selezionare il rispettivo PID
per gestirli. I pulsanti sono abilitati solo per i processi sospesi creati da questa GUI.

La creazione usa `CreateProcessW` con `CREATE_SUSPENDED` e mantiene Explorer come
padre richiesto. La GUI conserva gli handle del processo e del thread principale;
**Riprendi** chiama `ResumeThread` e controlla il contatore restituito.
La sospensione riguarda il thread principale, non un congelamento globale del processo.

## Osservazione API file automatica

Con **Avvio sospeso** e una modalita di hook attiva il processo viene preparato con la DLL
`ApiObserver32.dll` o `ApiObserver64.dll`, secondo l'architettura dell'eseguibile.
Funziona anche selezionando contemporaneamente l'opzione amministratore.
**Nessun hook** crea e riprende il processo senza caricare la DLL; il monitoraggio
mostra soltanto lo stato e il codice di uscita. **Solo file** non installa gli hook
su `ntdll`; **file + Native API** attiva entrambe le categorie. La scelta vale
per le nuove istanze e non modifica gli hook nei processi gia avviati.

Per verificare un problema di compatibilita mantieni uguali eseguibile, argomenti,
cartella e privilegi, confrontando prima **Nessun hook**, poi **Solo file** e infine
**file + Native API**. I filtri della finestra Monitoraggio nascondono soltanto righe:
non disattivano gli hook. Anche preservando gli esiti delle chiamate, l'instrumentazione
puo interferire con software che modifica a sua volta le API o impone vincoli di avvio.
Il monitor mostra il codice di uscita anche dopo che la DLL si e collegata;
`0xC00000FD` viene etichettato `STATUS_STACK_OVERFLOW`.

Il processo resta sospeso fino a **Riprendi**. Alla ripresa il loader carica la DLL,
che installa gli hook prima dell'ingresso normale nel programma. Nella scheda
**Eventi API file** compare **Hook attivi** quando l'attivazione e confermata.
Le operazioni avvenute prima del caricamento della DLL non vengono ricostruite.

La tabella mostra ora, PID, thread, API, percorso, parametri, risposta, codice Windows
e byte restituiti. Le API attualmente osservate sono:

- `CreateFileW` e `CreateFileA`: accesso richiesto, modalita di apertura e handle restituito;
- `ReadFile` e `WriteFile`: byte richiesti, risposta immediata ed eventuali byte trasferiti;
- `CloseHandle`: chiusure di handle di file su disco;
- `DeleteFileW` e `DeleteFileA`: richiesta di cancellazione ed esito.

Un codice Windows non sempre indica fallimento: `OPEN_ALWAYS` puo restituire un
handle valido e contemporaneamente `ERROR_ALREADY_EXISTS` (183).
Per operazioni asincrone viene mostrato l'esito immediato, inclusa l'eventuale
risposta `ERROR_IO_PENDING`; il completamento successivo non e intercettato.

Usa il filtro per cercare un percorso o un'API e il menu per selezionare una specifica API file. **Svuota eventi** cancella la
cronologia mantenuta in memoria. Sono conservati al massimo 4000 eventi nella GUI;
la vista mostra gli ultimi 1000 corrispondenti al filtro. Il contatore degli eventi
persi segnala eventuali overflow dei buffer, senza bloccare le chiamate del processo.
I percorsi sono limitati a 511 caratteri nel protocollo.

La scheda **Native API / WOW64** osserva anche `NtQueryVirtualMemory`,
`NtQuerySystemInformation`, `NtQueryInformationProcess`, `NtReadVirtualMemory`
e `NtWriteVirtualMemory`. Mostra architettura, handle, indirizzo, puntatore al buffer,
classe di informazione, dimensione richiesta, `NTSTATUS` originale e lunghezza restituita.
La lunghezza puo indicare lo spazio necessario quando la richiesta fallisce;
non equivale sempre a byte copiati. Per `MemoryBasicInformation` decodifica base,
dimensione, stato, protezione e tipo della regione; per `ProcessBasicInformation`
mostra PID e indirizzo PEB. Le altre classi sono mostrate con metadati ed esito,
senza decodificare il contenuto arbitrario della risposta.

Il menu delle Native API filtra una specifica funzione; la ricerca include PID,
indirizzi, parametri e risposte. Seleziona una riga per leggerne i dettagli.
Il pulsante **Monitoraggio** dell'interfaccia principale apre una finestra separata,
ridimensionabile e massimizzabile, con entrambe le schede **Eventi API file** e
**Native API / WOW64**. Chiudendola si nasconde soltanto la vista: la raccolta
continua in memoria entro i limiti previsti. Riaprendola ritrovi eventi e filtri.
La chiusura dell'app chiude anche Monitoraggio. I filtri agiscono sulla visualizzazione, non sugli hook.

Nei processi x86 su Windows x64, WOW64 traduce le chiamate verso il sistema nativo.
Gli hook osservano gli ingressi della `ntdll` x86 prima della traduzione; nei processi
x64 osservano `ntdll` x64. Non intercettano il gateway WOW64, syscall dirette,
o chiamate effettuate dalla porzione nativa x64 di un processo WOW64.
Le chiamate interne all'osservatore e quelle annidate nelle API file gia osservate
sono escluse per evitare ricorsione. Un evento appare al ritorno della funzione;
una chiamata che rimane bloccata non produce ancora una risposta.

La DLL inoltra le chiamate originali, preservandone il valore restituito e
`GetLastError`. Per letture e scritture di memoria acquisisce solo metadati,
senza copiare il contenuto trasferito. Non reindirizza i file.
Gli eventi viaggiano su una named pipe privata verificata per PID e restano in memoria.
Chiudendo la GUI il canale termina; un processo gia ripreso continua a funzionare.
Gli hook restano nel processo fino alla sua uscita, inoltrando le API originali.

L'osservazione riguarda il PID creato, non automaticamente i suoi processi figli.
Programmi che avviano un'altra istanza o usano API diverse possono produrre una
traccia parziale. La funzione non costituisce una sandbox di sicurezza.

Alla chiusura della GUI vengono terminati soltanto i processi creati da questa
sessione che risultano ancora sospesi. Dopo la ripresa il processo puo continuare
anche se chiudi la GUI. Non vengono creati log.

Con entrambe le opzioni selezionate, viene avviato un helper monouso della stessa
app tramite UAC. L'helper crea il figlio con il proprio token elevato esplicito,
`CREATE_SUSPENDED` e padre Explorer, usando `CreateProcessAsUserW`.
La cartella e gli argomenti rimangono quelli selezionati nella GUI.

Se la GUI e gia aperta come amministratore, Windows puo avviare l'helper elevato
senza un nuovo popup UAC. Il titolo e la casella amministratore indicano che l'app
e gia elevata; la descrizione dell'avvio sospeso chiarisce che non occorre un
secondo consenso. Per richiedere l'elevazione da un contesto standard, apri la GUI
normalmente da Esplora file, senza usare **Esegui come amministratore**, poi
seleziona entrambe le caselle. Il consenso effettivo dipende dai criteri UAC di Windows.

L'helper duplica verso la GUI gli handle necessari a riprendere o terminare il figlio.
La comunicazione usa una named pipe protetta per l'utente corrente e verifica i PID
di entrambe le parti. Dopo la conferma del trasferimento l'helper termina.
La GUI originale mantiene quindi **Riprendi** e **Termina sospeso** anche quando
non e stata aperta come amministratore. I dettagli mostrano i privilegi effettivi
e il PID del creatore, che in questo caso e quello dell'helper.
Se la GUI si chiude durante la richiesta, la creazione pendente viene annullata
e l'eventuale figlio rimasto sospeso viene terminato. Nessun file di scambio o log viene creato.

## Compilazione e verifica

Dalla cartella principale:

```powershell
.\ProcessLauncher\Build.ps1
& .\ProcessLauncher\app-exercise\ProcessLauncher.exe --self-test | Out-Host
$LASTEXITCODE
```

La compilazione richiede .NET SDK 10, Visual Studio con C++ x64 e Windows SDK.
Mantieni insieme i file di `app-exercise`. Chiudi l'app prima di aggiornare quella cartella.

Il test richiede accesso al desktop. Verifica entrambe le modalita, il padre
registrato e la directory effettiva letta dai figli in una cartella temporanea con spazi.
Le cavie terminano automaticamente e la cartella temporanea viene eliminata.
Output solo nel terminale: non vengono creati file di log.

Il test specifico dell'elevazione si esegue con `--self-test-elevation`: mostra
la normale richiesta UAC, avvia una cavia e ne verifica privilegi e cartella prima
che termini automaticamente. Non avvia il gioco e non crea rapporti su disco.

Il test standard verifica anche la creazione sospesa, il passaggio del contatore
da uno a zero, la prosecuzione del figlio dopo il rilascio degli handle e la
terminazione di una cavia mai ripresa.

Il test combinato si esegue con `--self-test-suspended-elevation-from-explorer`:
avvia il test nel contesto del desktop, richiede UAC e verifica figlio High,
sospensione iniziale, padre Explorer, trasferimento degli handle, ripresa e cartella.

`--self-test-observer` verifica la DLL su cavie a 64 e 32 bit, anche con API ANSI:
apertura di un file mancante, creazione, lettura e scrittura di 5 byte, chiusura,
riapertura e cancellazione. I dati temporanei di prova sono eliminati dalle cavie.
Verifica inoltre tutte le cinque Native API, richieste riuscite e fallite,
risultati delle query, lettura/scrittura di un intero nella sola memoria della cavia,
`NTSTATUS`, architettura e conservazione di `GetLastError`.
`--self-test-observer-elevation-from-explorer` ripete la prova con UAC nel contesto del desktop.
Non vengono creati log o rapporti diagnostici.

Gli hook usano [Microsoft Detours 4.0.1](https://github.com/microsoft/Detours/tree/v4.0.1).
La licenza MIT e distribuita insieme all'app in `Detours-LICENSE.txt`.

Riferimenti: [Shell.ShellExecute](https://learn.microsoft.com/en-us/windows/win32/shell/shell-shellexecute),
[WOW64](https://learn.microsoft.com/en-us/windows/win32/winprog64/wow64-implementation-details),
[NtQueryInformationProcess](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntqueryinformationprocess),
[NtQuerySystemInformation](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation),
[CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw).
