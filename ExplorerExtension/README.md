# DLL laboratorio per Explorer

Estensione nativa C++ a 64 bit che implementa `IShellExtInit` e `IContextMenu`.
Aggiunge **Laboratorio memoria** al menu contestuale dello sfondo delle cartelle.
Ogni invocazione incrementa un contatore globale della DLL e mostra:

- PID del processo che ha caricato la DLL;
- valore del contatore;
- indirizzo virtuale del contatore.

Quando la voce viene usata da Explorer, il codice della DLL viene eseguito dentro
quel processo. Il valore modificato appartiene alla DLL stessa. La stessa DLL
caricata dal programma di prova ha il suo contatore separato nel processo di prova.
Il contatore torna a zero quando la libreria viene scaricata e ricaricata.

## Compilazione e prova

Richiede Visual Studio con strumenti C++ x64 e Windows SDK.
Dalla cartella principale, in PowerShell:

```powershell
.\ExplorerExtension\Build.cmd
```

Produce `out\MemoryLab.dll` e `out\TestHost.exe`, poi esegue il test automaticamente.
Produce anche `out\file.exe`, il programma figlio dimostrativo.
Il test carica la DLL in un processo dedicato e verifica COM, menu, incremento del
contatore, indirizzo di memoria e rilascio dei riferimenti, senza aprire finestre.
Invoca anche il comando di avvio: `file.exe` controlla che il PID del padre sia
quello dell'host di prova e restituisce l'esito tramite il codice di uscita.

## Caricamento in Explorer

Lo script seguente registra due chiavi esclusivamente per l'utente corrente in
`HKCU\Software\Classes`: la classe COM e l'handler `Directory\Background`.
Non richiede privilegi amministrativi. La DLL deve restare nel percorso registrato.

```powershell
.\ExplorerExtension\Register.ps1
```

Apri una cartella in Explorer, clic destro in uno spazio vuoto, **Mostra altre opzioni**
(Windows 11), **Laboratorio memoria**. La finestra mostra PID, contatore e indirizzo.
Chiudi la finestra e ripeti per vedere il contatore aumentare, se la DLL e rimasta caricata.
Se la voce non appare, prova una nuova finestra di Explorer; criteri aziendali possono bloccare le estensioni.

Nel nostro esploratore (`dotnet run`) scegli quel PID e trova la regione che contiene
l'indirizzo visualizzato: indirizzo iniziale <= contatore < indirizzo iniziale + dimensione.
La regione dovrebbe essere di tipo Immagine, allocata, con protezione Lettura/Scrittura.
Gli indirizzi possono cambiare tra caricamenti: usa quello mostrato dalla finestra.

## Avviare un processo figlio da Explorer

Lo stesso menu contiene **Avvia file.exe (mostra padre e figlio)**.
Questa voce chiama `CreateProcessW` dal codice della DLL. Quando l'host della DLL
e Explorer, il processo creatore e quindi Explorer stesso. La finestra del figlio
mostra il proprio PID, quello del padre e il nome del padre, ricavati con
`CreateToolhelp32Snapshot` e `PROCESSENTRY32W.th32ParentProcessID`.

```text
explorer.exe
  └─ file.exe
```

`file.exe` e il nostro programma dimostrativo compilato da `Child.cpp`.
`LaunchChild.h` ne ricava il percorso assoluto dalla posizione della DLL:
i due file devono rimanere nella stessa cartella. L'avvio non usa una shell
intermedia e non cerca eseguibili nella PATH. Il comando normale ritorna subito
senza aspettare la chiusura del figlio. Gli handle restituiti vengono chiusi.
Il figlio ha memoria e PID propri e puo continuare a esistere dopo la chiusura del padre.

La registrazione gia esistente resta valida per questa versione, ma un processo
che ha caricato la vecchia DLL deve rilasciarla prima di poter usare la nuova.
Se Windows impedisce di ricompilarla perche in uso, rimuovi la registrazione e
chiudi la sessione Windows prima di ricompilare e registrare la nuova versione.
Salva il lavoro prima di chiudere la sessione.

Il test automatico usa `CMIC_MASK_FLAG_NO_UI`: in questo caso il figlio verifica
il PID atteso senza mostrare la finestra e l'handler ne attende brevemente l'esito.
Il PID padre osservato nel test e quello di `TestHost.exe`; il caso Explorer si prova dal menu.

## Scegliere un exe e crearlo dalla DLL con padre esplicito

La terza voce del menu e **Scegli exe e avvia dalla DLL (padre esplicito)**.
Aprila dal clic destro sullo sfondo di una cartella, sotto **Mostra altre opzioni**:
scegli un `.exe` e conferma. L'annullamento della scelta non avvia programmi.
La DLL mostra poi nome e PID del proprio processo ospite, PID del nuovo processo
e PID richiesto come padre. Puoi inserire il nuovo PID nella GUI e premere **Controlla**.

Quando l'estensione e caricata da `explorer.exe`, la chiamata di creazione viene
eseguita dentro Explorer. Il codice usa `OpenProcess(PROCESS_CREATE_PROCESS)` sul
proprio processo ospite, prepara la lista con `InitializeProcThreadAttributeList`
e imposta `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` prima di `CreateProcessW`.
Il padre richiesto e quindi lo stesso host che sta eseguendo la DLL.
In questa modalita creatore e padre coincidono; nella modalita della GUI
**Crea Y con padre Explorer** il creatore e invece la GUI.

Questa versione carica la DLL tramite la registrazione dell'estensione Shell.
L'iniezione remota di una DLL e una tecnica diversa e non e implementata qui.
L'azione crea un nuovo processo e non modifica quelli gia aperti. Non vengono
ricostruiti argomenti o inizializzazioni di altri launcher; la scelta avvia il file senza argomenti.

`ParentLaunch.h` contiene la funzione condivisa da DLL e GUI, che chiude l'handle
del padre e libera la lista di attributi. Il chiamante chiude gli handle del figlio.
Il test dell'estensione verifica la stessa operazione in `TestHost.exe`, con la
cavia che controlla il PID padre e termina automaticamente, senza finestre o log.
L'interazione con il selettore file si prova dal menu di Explorer dopo la registrazione.

## Rimozione

```powershell
.\ExplorerExtension\Register.ps1 -Uninstall
```

Rimuove solo le due chiavi di questa demo. Una DLL gia caricata puo rimanere in uso
fino al rilascio da parte dell'host o alla fine della sessione. Lo script non riavvia Explorer.
Una DLL eseguita dentro Explorer condivide anche i suoi possibili errori: per questo
la demo mantiene il lavoro del menu minimo e dispone di un host di prova separato.

Riferimenti: [Shell extensions](https://learn.microsoft.com/en-us/windows/win32/shell/shell-exts),
[IContextMenu](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-implement-the-icontextmenu-interface).
Per l'avvio: [CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)
e [PROCESSENTRY32W](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w).
