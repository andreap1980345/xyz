using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcessLauncher;

internal static class SelfTest
{
    public static int RunFromExplorer(string testMode = "--self-test")
    {
        try
        {
            Application.OleRequired();
            string path = Environment.ProcessPath!;
            var before = ProcessCatalog.Snapshot().Select(p => p.Id).ToHashSet();
            uint explorer = ExplorerLauncher.Launch(path, testMode);
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var entry = ProcessCatalog.FindLaunched(path, before)
                    .Where(p => p.ParentId == explorer)
                    .OrderBy(p => ProcessCatalog.StartTime(p.Id)).FirstOrDefault();
                if (entry is not null)
                {
                    using var process = Process.GetProcessById(entry.Id);
                    var handle = process.SafeHandle;
                    if (!process.WaitForExit(45000)) throw new Exception("Timeout del test nel desktop.");
                    if (!GetExitCodeProcess(handle, out uint exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    Console.WriteLine(exitCode == 0
                        ? $"OK: test nel desktop completato ({testMode})."
                        : $"FAIL: il test nel desktop ha restituito {exitCode}.");
                    return (int)exitCode;
                }
                Thread.Sleep(50);
            }
            throw new Exception("Processo di test nel desktop non rilevato.");
        }
        catch (Exception e) { Console.Error.WriteLine($"FAIL: {e}"); return 1; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    public static int Run()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), $"ProcessLauncher cwd {Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(testDirectory);
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var form = new MainForm();
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            var monitorButton = Descendants(form).OfType<Button>().Single(b => b.Text == "Monitoraggio");
            monitorButton.PerformClick();
            Application.DoEvents();
            var monitoring = form.OwnedForms.Single(f => f.Text == "Monitoraggio");
            var nativeView = Descendants(monitoring).OfType<ApiEventsView>().Single();
            var nativePage = (TabPage)nativeView.Parent!;
            var nativeTabs = (TabControl)nativePage.Parent!;
            if (nativeTabs.TabPages.Count != 2 || nativeTabs.TabPages[0].Text != "Eventi API file"
                || Descendants(form).OfType<TabControl>().Any())
                throw new Exception("Le due schede API non sono nella sola finestra Monitoraggio.");
            nativeTabs.SelectedTab = nativePage;
            var sample = new ObservedCall(DateTime.Now, 123, 456, 11, true, 0, 0, 32, 2,
                1, uint.MaxValue, 4, 4, 0, 0x1234, 0x5678, "");
            nativeView.SetCalls(new[] { sample, sample with { Kind = 12 }, sample with { Kind = 2 } });
            var nativeGrid = Descendants(nativeView).OfType<DataGridView>().Single();
            var nativeApiFilter = Descendants(nativeView).OfType<ComboBox>().Single();
            if (nativeGrid.RowCount != 2) throw new Exception("La vista Native API include eventi file.");
            nativeApiFilter.SelectedItem = "NtWriteVirtualMemory";
            if (nativeGrid.RowCount != 1) throw new Exception("Filtro Native API non applicato.");
            Descendants(nativeView).OfType<TextBox>().Single(t => !t.Multiline).Text = "nessuna-corrispondenza";
            if (nativeGrid.RowCount != 0) throw new Exception("Ricerca Native API non applicata.");
            monitoring.Close();
            Application.DoEvents();
            if (monitoring.Visible || monitoring.IsDisposed) throw new Exception("Chiusura Monitoraggio non conserva la vista.");
            monitorButton.PerformClick();
            Application.DoEvents();
            if (!monitoring.Visible || nativeApiFilter.Text != "NtWriteVirtualMemory" || nativeView.IsDisposed)
                throw new Exception("Riapertura Monitoraggio non conserva i filtri.");
            nativeView.SetCalls(Array.Empty<ObservedCall>());
            nativeTabs.SelectedIndex = 0;
            monitoring.Close();
            Console.WriteLine("OK: popup Monitoraggio con entrambe le schede, filtri, ricerca, chiusura e riapertura senza perdita dello stato.");
            var checkboxes = Descendants(form).OfType<CheckBox>().ToList();
            var adminOption = checkboxes.Single(c => c.Text.StartsWith("Esegui come amministratore", StringComparison.Ordinal));
            var suspendedOption = checkboxes.Single(c => c.Text.StartsWith("Avvio sospeso", StringComparison.Ordinal));
            suspendedOption.Checked = adminOption.Checked = true;
            if (!adminOption.Enabled || !suspendedOption.Enabled || !adminOption.Checked || !suspendedOption.Checked)
                throw new Exception("Le opzioni UAC e sospensione non sono selezionabili insieme.");
            suspendedOption.Checked = adminOption.Checked = false;
            var before = ProcessCatalog.Snapshot();
            if (!before.Any(p => p.Id == Environment.ProcessId)) throw new Exception("Processo corrente assente dallo snapshot.");
            var ownSecurity = ProcessCatalog.Security(Environment.ProcessId);
            if (ownSecurity.Elevated is null || ownSecurity.Integrity == "Non disponibile")
                throw new Exception("Informazioni del token del processo corrente non disponibili.");
            string path = Environment.ProcessPath!;
            string parameters = $"--probe-child \"{testDirectory}\"";
            uint explorer = ExplorerLauncher.Launch(path, parameters, testDirectory);
            var previousPids = before.Select(p => p.Id).ToHashSet();
            var timer = Stopwatch.StartNew();
            ProcessEntry? child = null;
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                child = ProcessCatalog.FindLaunched(path, previousPids).FirstOrDefault(p => p.ParentId == explorer);
                if (child is not null) break;
                Thread.Sleep(50);
            }
            if (child is null) throw new Exception("Nessun processo figlio di Explorer rilevato.");
            var snapshot = ProcessCatalog.Snapshot();
            var parent = snapshot.Single(p => p.Id == explorer);
            if (!string.Equals(parent.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                || !ProcessCatalog.IsVerifiedParent(child, parent)) throw new Exception("Relazione Explorer-figlio non verificata.");
            var detected = ProcessCatalog.FindLaunched(path, previousPids);
            if (!detected.Any(p => p.Id == child.Id)) throw new Exception("Rilevamento automatico del nuovo processo fallito.");
            form.InspectProcess(child.Id);
            VerifyDirectoryInChild(child.Id);
            var exercise = ExplorerLauncher.CreateWithExplorerParent(path, parameters, testDirectory);
            var exerciseSnapshot = ProcessCatalog.Snapshot();
            var exerciseChild = exerciseSnapshot.Single(p => p.Id == exercise.Id);
            var exerciseParent = exerciseSnapshot.Single(p => p.Id == exercise.ParentId);
            if (exercise.CreatorId != Environment.ProcessId || exercise.ParentId == exercise.CreatorId
                || exerciseChild.ParentId != exercise.ParentId
                || !string.Equals(exerciseParent.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                || !ProcessCatalog.IsVerifiedParent(exerciseChild, exerciseParent))
                throw new Exception("L'esercizio non ha prodotto un padre Explorer distinto dal creatore X.");
            form.InspectProcess(exerciseChild.Id);
            VerifyDirectoryInChild(exerciseChild.Id);
            VerifySuspension(path, parameters, testDirectory);
            var defaults = ExplorerLauncher.Validate(path, null);
            if (!string.Equals(defaults.Directory, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                throw new Exception("La cartella predefinita non coincide con quella dell'eseguibile.");
            try { ExplorerLauncher.Validate(path, Path.Combine(testDirectory, "nonexistent")); throw new Exception("Cartella inesistente accettata."); }
            catch (ArgumentException) { }
            try { ExplorerLauncher.Launch(Path.Combine(AppContext.BaseDirectory, "nonexistent.exe"), ""); throw new Exception("File inesistente accettato."); }
            catch (ArgumentException) { }
            Console.WriteLine($"OK: form, snapshot, avvio tramite Explorer PID {explorer}, figlio PID {child.Id}, verifica padre e rilevamento automatico.");
            Console.WriteLine($"OK: padre esplicito Explorer PID {exerciseChild.ParentId}; cartella personalizzata con spazi verificata dentro entrambi i processi figli.");
            Console.WriteLine($"OK: lettura privilegi, integrita {ownSecurity.Integrity} e percorso eseguibile con accesso limitato.");
            Console.WriteLine("OK: creazione sospesa, contatore iniziale, ripresa, cartella del figlio e pulizia di un processo mai ripreso.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAIL: {e}");
            return 1;
        }
        finally
        {
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory);
        }
    }

    private static void VerifyDirectoryInChild(int pid)
    {
        using var handle = OpenProcess(0x00101000, false, pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (WaitForSingleObject(handle, 8000) != 0) throw new Exception("La cavia non e terminata.");
        if (!GetExitCodeProcess(handle, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (code != 0) throw new Exception("La cavia non ha confermato cartella di lavoro o privilegi richiesti.");
    }

    private static void VerifySuspension(string path, string parameters, string directory)
    {
        using (var paused = SuspendedProcess.Create(path, parameters, directory))
        {
            Thread.Sleep(200);
            if (paused.HasExited || !paused.IsSuspended)
                throw new Exception("La cavia non e rimasta sospesa.");
            var entries = ProcessCatalog.Snapshot();
            var child = entries.Single(p => p.Id == paused.Info.Id);
            var parent = entries.Single(p => p.Id == paused.Info.ParentId);
            if (!string.Equals(parent.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                || !ProcessCatalog.IsVerifiedParent(child, parent))
                throw new Exception("Padre della cavia sospesa non verificato.");
            uint previous = paused.Resume();
            if (previous != 1 || paused.IsSuspended)
                throw new Exception("Il contatore di sospensione non e passato da uno a zero.");
            // Il rilascio degli handle dopo la ripresa deve lasciare il figlio in esecuzione.
            paused.Dispose();
            VerifyDirectoryInChild(child.Id);
        }
        using var stopped = SuspendedProcess.Create(path, parameters, directory);
        using var observer = OpenProcess(0x00101000, false, stopped.Info.Id);
        if (observer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var cancellation = new CancellationTokenSource();
        stopped.BindLifetime(cancellation.Token);
        cancellation.Cancel();
        if (WaitForSingleObject(observer, 3000) != 0)
            throw new Exception("Un processo mai ripreso e rimasto attivo dopo il rilascio.");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (var descendant in Descendants(control)) yield return descendant;
        }
    }

    public static int RunElevation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ProcessLauncher UAC {Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            Application.OleRequired();
            string path = Environment.ProcessPath!;
            var before = ProcessCatalog.Snapshot().Select(p => p.Id).ToHashSet();
            ExplorerLauncher.Launch(path, $"--probe-child \"{directory}\" --require-elevation", directory, runAsAdmin: true);
            var timer = Stopwatch.StartNew();
            ProcessEntry? child = null;
            while (timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                child = ProcessCatalog.FindLaunched(path, before).FirstOrDefault();
                if (child is not null) break;
                Thread.Sleep(50);
            }
            if (child is null) throw new Exception("Nessuna cavia elevata rilevata: la richiesta UAC potrebbe essere stata annullata.");
            var security = ProcessCatalog.Security(child.Id);
            if (security.Elevated != true) throw new Exception("La cavia non risulta elevata.");
            VerifyDirectoryInChild(child.Id);
            Console.WriteLine($"OK UAC: PID {child.Id}, padre effettivo PID {child.ParentId}, amministratore Si, integrita {security.Integrity}; cartella confermata dal figlio.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"FAIL UAC: {e}"); return 1; }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    public static int RunSuspendedElevationLocal()
    {
        try
        {
            string directory = AppContext.BaseDirectory;
            string parameters = $"--probe-child \"{directory.TrimEnd('\\')}\" --require-elevation";
            using var child = SuspendedProcess.Create(Environment.ProcessPath!, parameters, directory, elevatedLocal: true);
            var security = ProcessCatalog.Security(child.Info.Id);
            if (security.Elevated != true || child.HasExited)
                throw new Exception("Il figlio non e elevato e sospeso.");
            if (child.Resume() != 1) throw new Exception("Contatore sospensione inatteso.");
            VerifyDirectoryInChild(child.Info.Id);
            Console.WriteLine($"OK: figlio elevato sospeso, integrita {security.Integrity}, padre PID {child.Info.ParentId}, ripresa e cartella verificate.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"FAIL: {e}"); return 1; }
    }

    public static int RunSuspendedElevation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ProcessLauncher suspended UAC {Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string parameters = $"--probe-child \"{directory}\" --require-elevation";
            using var child = ElevatedSuspension.CreateAsync(Environment.ProcessPath!, parameters, directory).GetAwaiter().GetResult();
            var security = ProcessCatalog.Security(child.Info.Id);
            var snapshot = ProcessCatalog.Snapshot();
            var actual = snapshot.Single(p => p.Id == child.Info.Id);
            var parent = snapshot.Single(p => p.Id == child.Info.ParentId);
            if (child.HasExited || !child.IsSuspended || security.Elevated != true || security.Integrity != "High"
                || !string.Equals(parent.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                || !ProcessCatalog.IsVerifiedParent(actual, parent))
                throw new Exception("Elevazione, sospensione o padre del figlio non verificati.");
            if (child.Resume() != 1) throw new Exception("Contatore iniziale di sospensione inatteso.");
            VerifyDirectoryInChild(child.Info.Id);
            Console.WriteLine($"OK: GUI {ProcessCatalog.Security(Environment.ProcessId).Integrity}; figlio High e sospeso, padre Explorer PID {parent.Id}; handle trasferiti, ripresa e cartella confermate.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"FAIL: {e}"); return 1; }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    public static int RunObserver(bool elevated = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ProcessLauncher observer {Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var test in new[] { (Filename: "file.exe", Ansi: false, Features: 3u),
                (Filename: "file32.exe", Ansi: false, Features: 3u), (Filename: "file32.exe", Ansi: true, Features: 3u),
                (Filename: "file32.exe", Ansi: false, Features: 1u), (Filename: "file32.exe", Ansi: false, Features: 0u) })
            {
                string filename = test.Filename;
                string executable = Path.Combine(AppContext.BaseDirectory, filename);
                string arguments = $"{(test.Ansi ? "--observer-test-ansi" : "--observer-test")} \"{directory}\"";
                using var observation = new ObserverSession(test.Features);
                string? observerPipe = test.Features == 0 ? null : observation.PipeName;
                using var child = elevated
                    ? ElevatedSuspension.CreateAsync(executable, arguments, directory, observerPipe: observerPipe, observerFeatures: test.Features).GetAwaiter().GetResult()
                    : SuspendedProcess.Create(executable, arguments, directory, observerPipe: observerPipe, observerFeatures: test.Features);
                observation.Bind(child.Info.Id);
                Thread.Sleep(100);
                if (observation.Drain().Count != 0 || child.HasExited)
                    throw new Exception("Il processo non e rimasto sospeso prima della ripresa.");
                if (elevated && ProcessCatalog.Security(child.Info.Id).Elevated != true)
                    throw new Exception("La cavia osservata non e elevata.");
                if (child.Resume() != 1) throw new Exception("Sospensione iniziale inattesa.");
                var calls = new List<ObservedCall>();
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(8))
                {
                    calls.AddRange(observation.Drain());
                    if (child.HasExited) break;
                    Thread.Sleep(50);
                }
                calls.AddRange(observation.Drain());
                if (test.Features != 0 && !observation.HookReady)
                    throw new Exception($"DLL non collegata per {filename}: {observation.State}");
                try { VerifyDirectoryInChild(child.Info.Id); }
                catch (Exception e) { throw new Exception($"{e.Message} | Eventi: {string.Join(", ", calls.Select(c => $"{c.Api}:{c.Result}:{c.Bytes}"))}", e); }
                if (!observation.State.Contains("uscita 0x00000000")) throw new Exception("Codice di uscita non mostrato nel monitoraggio.");
                if (test.Features == 0)
                {
                    if (calls.Count != 0 || observation.HookReady) throw new Exception("Nessun hook ha prodotto eventi.");
                    Console.WriteLine("OK: avvio sospeso senza DLL, ripresa, uscita 0 e zero eventi.");
                    continue;
                }
                bool Has(uint kind, string suffix, bool success, uint error = 0) => calls.Any(c =>
                    c.Kind == kind && c.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && c.Success == success && c.Error == error);
                uint openKind = test.Ansi ? 6u : 1u, deleteKind = test.Ansi ? 7u : 5u;
                if (!Has(openKind, "observer-missing.tmp", false, 2)
                    || !Has(openKind, "observer-data.tmp", true)
                    || !Has(openKind, "observer-data.tmp", true, 183)
                    || !Has(deleteKind, "observer-data.tmp", true)
                    || !calls.Any(c => c.Kind == 2 && c.Success && c.Transferred == 5)
                    || !calls.Any(c => c.Kind == 3 && c.Success && c.Transferred == 5)
                    || !Has(4, "observer-data.tmp", true))
                    throw new Exception($"Eventi incompleti per {filename}: {string.Join(", ", calls.Select(c => $"{c.Api}:{c.Result}:{c.Error}:{c.Transferred}"))}");
                if (calls.Any(c => c.Path.Contains(observation.PipeName, StringComparison.Ordinal)))
                    throw new Exception("La DLL sta osservando il proprio canale di comunicazione.");
                if (test.Features == 1)
                {
                    if (calls.Any(c => c.IsNative) || !calls.Any(c => c.Kind == 0 && c.Access == 1))
                        throw new Exception("La modalita solo file ha attivato Native API.");
                    Console.WriteLine("OK: modalita solo file x86, eventi file presenti e zero eventi Native API; uscita 0.");
                    continue;
                }
                uint bits = filename == "file32.exe" ? 32u : 64u;
                foreach (uint kind in new uint[] { 8, 9, 10, 11, 12 })
                    if (!calls.Any(c => c.Kind == kind && c.Success && c.Error == 0 && c.Disposition == bits))
                        throw new Exception($"Manca Native API {kind} ({bits} bit).");
                if (!calls.Any(c => c.Kind == 9 && !c.Success && c.Error == 0xC0000004 && c.Transferred > 0)
                    || !calls.Any(c => c.Kind == 8 && c.Path.Contains("RegionSize="))
                    || !calls.Any(c => c.Kind == 10 && c.Path.Contains($"PID={child.Info.Id};"))
                    || !calls.Any(c => c.Kind == 11 && c.Transferred == 4 && c.Address != 0 && c.Buffer != 0)
                    || !calls.Any(c => c.Kind == 12 && c.Transferred == 4))
                    throw new Exception("Risposte Native API incomplete.");
                Console.WriteLine($"OK Native API {bits} bit: tutte le 5 API, NTSTATUS successo/errore, output query, byte e LastError originali verificati.");
                Console.WriteLine($"OK hook {filename}{(test.Ansi ? " ANSI" : " Unicode")}{(elevated ? " High" : "")}: {calls.Count} eventi; richieste, HANDLE, errori 2/183, lettura/scrittura 5 byte e cancellazione verificate. Nessun log.");
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine($"FAIL hook: {e}"); return 1; }
        finally
        {
            string data = Path.Combine(directory, "observer-data.tmp");
            if (File.Exists(data)) File.Delete(data);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint timeout);
}
