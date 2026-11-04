using System.ComponentModel;
using System.Diagnostics;

namespace ProcessLauncher;

internal sealed class MainForm : Form
{
    private readonly TextBox executable = new() { Dock = DockStyle.Fill };
    private readonly TextBox arguments = new() { Dock = DockStyle.Fill, PlaceholderText = "Argomenti facoltativi" };
    private readonly TextBox workingDirectory = new() { Dock = DockStyle.Fill, PlaceholderText = "Cartella da cui avviare il programma" };
    private readonly CheckBox runAsAdmin = new() { Text = "Esegui come amministratore (conferma UAC)", AutoSize = true };
    private readonly CheckBox createSuspended = new() { Text = "Avvio sospeso (CREATE_SUSPENDED)", AutoSize = true };
    private readonly ComboBox hookMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Enabled = false };
    private readonly Button resume = new() { Text = "Riprendi", AutoSize = true, Enabled = false };
    private readonly Button stopSuspended = new() { Text = "Termina sospeso", AutoSize = true, Enabled = false };
    private readonly Button launch = new() { Text = "Apri processo tramite explorer.exe", AutoSize = true, BackColor = Color.FromArgb(36, 91, 219), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 5, 10, 5) };
    private readonly Button experiment = new() { Text = "Crea Y con padre Explorer", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
    private readonly Button check = new() { Text = "Controlla", AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
    private readonly Label creationInfo = new() { Dock = DockStyle.Fill, Text = "Scegli l'eseguibile e la cartella di lavoro da usare all'avvio.", AutoEllipsis = true };
    private readonly NumericUpDown pid = new() { Minimum = 0, Maximum = int.MaxValue, Width = 110 };
    private readonly ComboBox candidates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290 };
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, ItemHeight = 28 };
    private readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 10) };
    private readonly Label verdict = new() { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(8), Text = "Scegli un eseguibile per cominciare.", ForeColor = Color.FromArgb(36, 91, 219) };
    private readonly Label status = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), Text = "Pronto. Puoi anche inserire il PID di un processo gia aperto." };
    private int? trackedPid;
    private DateTime? trackedStart;
    private bool busy;
    private readonly bool appIsElevated;
    private ExplorerLauncher.CreatedProcess? exercise;
    private string? lastExecutableDirectory;
    private string? requestedWorkingDirectory;
    private readonly Dictionary<int, SuspendedProcess> suspendedProcesses = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<ObserverSession> observers = new();
    private readonly List<ObservedCall> observedCalls = new();
    private List<ObservedCall> displayedCalls = new();
    private readonly ApiEventsView nativeEvents = new();
    private readonly Form monitoringWindow = new() { Text = "Monitoraggio", Size = new Size(1300, 800),
        MinimumSize = new Size(940, 560), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
    private readonly ComboBox fileApiFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly System.Windows.Forms.Timer observerTimer = new() { Interval = 200 };
    private readonly DataGridView eventsGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, VirtualMode = true };
    private readonly TextBox eventFilter = new() { Width = 330, PlaceholderText = "Filtra percorso o nome API" };
    private readonly Label observerStatus = new() { Dock = DockStyle.Fill, Text = "Spunta Avvio sospeso per predisporre automaticamente la DLL hook." };

    public MainForm()
    {
        appIsElevated = ProcessCatalog.Security(Environment.ProcessId).Elevated == true;
        Text = "Laboratorio processi · Explorer";
        if (appIsElevated)
        {
            Text += " (amministratore)";
            runAsAdmin.Text = "Esegui come amministratore (app gia elevata)";
        }
        Size = new Size(1100, 920);
        MinimumSize = new Size(940, 820);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        monitoringWindow.Font = Font;
        monitoringWindow.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                monitoringWindow.Hide();
            }
        };
        BackColor = Color.FromArgb(241, 245, 249);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 305));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        Controls.Add(layout);
        var header = new Label { Dock = DockStyle.Fill, Text = "Laboratorio processi\nScegli la cartella di lavoro, avvia e controlla il processo.", Font = new Font("Segoe UI", 14), ForeColor = Color.FromArgb(15, 23, 42) };
        layout.Controls.Add(header, 0, 0);

        var launchGroup = new GroupBox { Text = "1 · Scegli e apri", Dock = DockStyle.Fill };
        var inputs = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 3, RowCount = 6 };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        inputs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var browse = new Button { Text = "Sfoglia…", Dock = DockStyle.Fill };
        inputs.Controls.Add(new Label { Text = "Eseguibile", AutoSize = true }, 0, 0);
        inputs.Controls.Add(executable, 1, 0);
        inputs.Controls.Add(browse, 2, 0);
        inputs.Controls.Add(new Label { Text = "Argomenti", AutoSize = true }, 0, 1);
        inputs.Controls.Add(arguments, 1, 1);
        inputs.SetColumnSpan(arguments, 2);
        inputs.Controls.Add(new Label { Text = "Cartella di lavoro", AutoSize = true }, 0, 2);
        inputs.Controls.Add(workingDirectory, 1, 2);
        var browseDirectory = new Button { Text = "Cartella…", Dock = DockStyle.Fill };
        inputs.Controls.Add(browseDirectory, 2, 2);
        hookMode.Items.AddRange(new object[] { "Nessun hook", "Hook: solo file", "Hook: file + Native API" });
        hookMode.SelectedIndex = 1;
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        options.Controls.Add(runAsAdmin);
        options.Controls.Add(createSuspended);
        options.Controls.Add(hookMode);
        inputs.Controls.Add(options, 1, 3);
        inputs.SetColumnSpan(options, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(launch);
        actions.Controls.Add(experiment);
        inputs.Controls.Add(actions, 1, 4);
        inputs.SetColumnSpan(actions, 2);
        inputs.Controls.Add(creationInfo, 0, 5);
        inputs.SetColumnSpan(creationInfo, 3);
        launchGroup.Controls.Add(inputs);
        layout.Controls.Add(launchGroup, 0, 1);

        var monitor = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 15, 0, 0), WrapContents = true };
        monitor.Controls.Add(new Label { Text = "2 · PID", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        monitor.Controls.Add(pid);
        monitor.Controls.Add(check);
        monitor.Controls.Add(resume);
        monitor.Controls.Add(stopSuspended);
        monitor.Controls.Add(candidates);
        var refresh = new Button { Text = "Elenco processi", AutoSize = true };
        monitor.Controls.Add(refresh);
        var showMonitoring = new Button { Text = "Monitoraggio", AutoSize = true };
        monitor.Controls.Add(showMonitoring);
        showMonitoring.Click += (_, _) =>
        {
            if (!monitoringWindow.Visible) monitoringWindow.Show(this);
            if (monitoringWindow.WindowState == FormWindowState.Minimized) monitoringWindow.WindowState = FormWindowState.Normal;
            monitoringWindow.Activate();
        };
        layout.Controls.Add(monitor, 0, 2);
        layout.Controls.Add(verdict, 0, 3);
        var split = new SplitContainer { Size = new Size(940, 320), Dock = DockStyle.Fill, SplitterDistance = 380, BackColor = Color.White, Panel1MinSize = 200, Panel2MinSize = 200 };
        split.Panel1.Padding = new Padding(12);
        split.Panel2.Padding = new Padding(12);
        split.Panel1.Controls.Add(tree);
        split.Panel2.Controls.Add(details);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        layout.Controls.Add(split, 0, 4);
        var eventsPage = new TabPage("Eventi API file");
        var eventLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(6) };
        eventLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        eventLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        eventLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        var eventTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        var clearEvents = new Button { Text = "Svuota eventi", AutoSize = true };
        eventTools.Controls.Add(eventFilter);
        fileApiFilter.Items.AddRange(new object[] { "Tutte le API file", "CreateFileW", "CreateFileA", "ReadFile", "WriteFile", "CloseHandle", "DeleteFileW", "DeleteFileA" });
        fileApiFilter.SelectedIndex = 0;
        eventTools.Controls.Add(fileApiFilter);
        eventTools.Controls.Add(clearEvents);
        eventLayout.Controls.Add(eventTools, 0, 0);
        eventLayout.Controls.Add(eventsGrid, 0, 1);
        eventLayout.Controls.Add(observerStatus, 0, 2);
        foreach (var column in new[] { ("Ora", 105), ("PID", 65), ("Thread", 65), ("API", 110), ("Percorso", 330),
            ("Parametri", 250), ("Risposta", 150), ("Codice Windows", 290), ("Byte restituiti", 115) })
            eventsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Item1, Width = column.Item2 });
        eventsGrid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= displayedCalls.Count) return;
            var call = displayedCalls[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => call.Time.ToString("HH:mm:ss.fff"), 1 => call.Pid, 2 => call.Tid,
                3 => call.Api, 4 => call.Path, 5 => call.Parameters, 6 => call.Result,
                7 => call.ErrorText, 8 => call.Bytes, _ => null
            };
        };
        eventsPage.Controls.Add(eventLayout);
        tabs.TabPages.Add(eventsPage);
        var nativePage = new TabPage("Native API / WOW64");
        nativePage.Controls.Add(nativeEvents);
        tabs.TabPages.Add(nativePage);
        monitoringWindow.Controls.Add(tabs);
        clearEvents.Click += (_, _) => { observedCalls.Clear(); RefreshEventRows(); };
        eventFilter.TextChanged += (_, _) => RefreshEventRows();
        fileApiFilter.SelectedIndexChanged += (_, _) => RefreshEventRows();
        observerTimer.Tick += (_, _) => PollObservers();
        observerTimer.Start();
        layout.Controls.Add(status, 0, 5);
        executable.Text = Path.Combine(AppContext.BaseDirectory, "file.exe");
        lastExecutableDirectory = Path.GetDirectoryName(executable.Text);
        workingDirectory.Text = lastExecutableDirectory;
        executable.TextChanged += (_, _) => UpdateDefaultWorkingDirectory();
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Programmi Windows (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) executable.Text = dialog.FileName;
        };
        browseDirectory.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Scegli la cartella di lavoro del processo", UseDescriptionForTitle = true };
            if (Directory.Exists(workingDirectory.Text)) dialog.SelectedPath = workingDirectory.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK) workingDirectory.Text = dialog.SelectedPath;
        };
        launch.Click += async (_, _) => await LaunchAsync();
        runAsAdmin.CheckedChanged += (_, _) =>
        {
            UpdateLaunchOptions();
        };
        createSuspended.CheckedChanged += (_, _) => UpdateLaunchOptions();
        hookMode.SelectedIndexChanged += (_, _) => UpdateLaunchOptions();
        pid.ValueChanged += (_, _) => UpdateSuspendedControls();
        resume.Click += (_, _) => Guard(ResumeSelected);
        stopSuspended.Click += (_, _) => Guard(StopSelected);
        experiment.Click += (_, _) => Guard(RunExercise);
        check.Click += (_, _) => Guard(CheckSelected);
        refresh.Click += (_, _) => Guard(RefreshProcesses);
        candidates.SelectedIndexChanged += (_, _) =>
        {
            if (candidates.SelectedItem is ProcessEntry entry) pid.Value = entry.Id;
        };
        tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is ProcessEntry entry) details.Text = Describe(entry);
        };
    }

    private async Task LaunchAsync()
    {
        if (busy) return;
        if (createSuspended.Checked)
        {
            await CreateSuspendedProcessAsync();
            return;
        }
        busy = true;
        launch.Enabled = check.Enabled = experiment.Enabled = false;
        runAsAdmin.Enabled = false;
        createSuspended.Enabled = false;
        UpdateSuspendedControls();
        try
        {
            exercise = null;
            tree.Nodes.Clear();
            details.Clear();
            string path = Path.GetFullPath(executable.Text.Trim().Trim('"'));
            string parameters = arguments.Text;
            bool elevated = runAsAdmin.Checked;
            var settings = ExplorerLauncher.Validate(path, workingDirectory.Text);
            requestedWorkingDirectory = settings.Directory;
            var before = ProcessCatalog.Snapshot().Select(p => p.Id).ToHashSet();
            uint explorerPid = ExplorerLauncher.Launch(settings.Path, parameters, settings.Directory, elevated);
            creationInfo.Text = $"{(elevated ? "Richiesta amministratore (UAC)" : "Avvio standard")} | Explorer: PID {explorerPid} | Cartella: {settings.Directory}";
            verdict.Text = "Avvio in corso...";
            trackedPid = null;
            trackedStart = null;
            candidates.Items.Clear();
            var found = new List<ProcessEntry>();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(4))
            {
                await Task.Delay(200);
                if (IsDisposed) return;
                found = ProcessCatalog.FindLaunched(settings.Path, before);
                if (found.Count > 0) break;
            }
            SetCandidates(found);
            if (found.Count == 1)
            {
                trackedPid = found[0].Id;
                trackedStart = ProcessCatalog.StartTime(found[0].Id);
                InspectProcess(found[0].Id);
                if (elevated)
                {
                    var security = ProcessCatalog.Security(found[0].Id);
                    status.Text = security.Elevated switch
                    {
                        true => "Avvio amministratore verificato. Il padre effettivo e mostrato nell'albero.",
                        false => "Il processo rilevato non risulta elevato. Controlla la richiesta UAC e il PID selezionato.",
                        null => "Processo rilevato; privilegi non leggibili con i permessi correnti."
                    };
                }
            }
            else
            {
                verdict.Text = "Padre ancora da verificare.";
                status.Text = found.Count > 1
                    ? "Piu processi rilevati: scegli quello da controllare e premi Controlla."
                    : "Nessun nuovo PID identificato: l'app potrebbe riusare un'istanza o terminare subito. Inserisci un PID e premi Controlla.";
            }
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x800704C7))
        {
            verdict.Text = "Avvio annullato nella richiesta UAC.";
            status.Text = "Nessun nuovo avvio richiesto dopo l'annullamento.";
        }
        catch (Exception e) { ShowError(e); }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                launch.Enabled = check.Enabled = true;
                UpdateLaunchOptions(updateDescription: false);
                UpdateSuspendedControls();
            }
        }
    }

    private void RunExercise()
    {
        if (busy || runAsAdmin.Checked) return;
        exercise = ExplorerLauncher.CreateWithExplorerParent(executable.Text, arguments.Text, workingDirectory.Text);
        trackedPid = exercise.Id;
        trackedStart = exercise.StartedUtc;
        requestedWorkingDirectory = exercise.WorkingDirectory;
        creationInfo.Text = $"Creatore X: PID {exercise.CreatorId} | Y: PID {exercise.Id} | Padre: Explorer PID {exercise.ParentId}";
        SetCandidates(ProcessCatalog.Snapshot().Where(p => p.Id == exercise.Id).ToList());
        InspectProcess(exercise.Id);
    }

    private void UpdateLaunchOptions(bool updateDescription = true)
    {
        runAsAdmin.Enabled = createSuspended.Enabled = !busy;
        hookMode.Enabled = !busy && createSuspended.Checked;
        experiment.Enabled = !busy && !runAsAdmin.Checked && !createSuspended.Checked;
        launch.Text = createSuspended.Checked ? runAsAdmin.Checked ? "Crea sospeso come amministratore" : "Crea processo sospeso"
            : runAsAdmin.Checked ? "Avvia come amministratore (UAC)" : "Apri processo tramite explorer.exe";
        if (!updateDescription) return;
        creationInfo.Text = createSuspended.Checked
            ? runAsAdmin.Checked ? appIsElevated
                    ? "App gia amministratore: Windows non richiede un nuovo consenso UAC. Il figlio sara elevato e sospeso."
                    : $"UAC + sospensione; {hookMode.Text}. Premi Riprendi dopo la creazione."
                : "Avvio sospeso. Scegli nessun hook, solo file oppure file + Native API."
            : runAsAdmin.Checked ? "Windows chiedera la conferma UAC. Controlla mostrera privilegi e padre effettivi."
            : "Scegli l'eseguibile e la cartella di lavoro da usare all'avvio.";
    }

    private async Task CreateSuspendedProcessAsync()
    {
        if (busy) return;
        busy = true;
        launch.Enabled = check.Enabled = experiment.Enabled = runAsAdmin.Enabled = createSuspended.Enabled = hookMode.Enabled = false;
        UpdateSuspendedControls();
        ObserverSession? observation = null;
        try
        {
            bool elevated = runAsAdmin.Checked;
            uint features = hookMode.SelectedIndex switch { 0 => 0u, 1 => 1u, _ => 3u };
            observation = new ObserverSession(features);
            string? observerPipe = features == 0 ? null : observation.PipeName;
            creationInfo.Text = elevated
                ? appIsElevated ? "App gia amministratore: creazione del processo elevato sospeso..."
                    : "Richiesta UAC e creazione del processo elevato sospeso..."
                : "Creazione del processo sospeso...";
            var child = elevated
                ? await ElevatedSuspension.CreateAsync(executable.Text, arguments.Text, workingDirectory.Text, lifetime.Token, observerPipe, features)
                : SuspendedProcess.Create(executable.Text, arguments.Text, workingDirectory.Text, observerPipe: observerPipe, observerFeatures: features);
            if (IsDisposed) { child.Dispose(); return; }
            observation.Bind(child.Info.Id);
            observers.Add(observation);
            observation = null;
            if (suspendedProcesses.Remove(child.Info.Id, out var stale)) stale.Dispose();
            suspendedProcesses.Add(child.Info.Id, child);
            exercise = child.Info;
            trackedPid = child.Info.Id;
            trackedStart = child.Info.StartedUtc;
            requestedWorkingDirectory = child.Info.WorkingDirectory;
            SetCandidates(ProcessCatalog.Snapshot().Where(p => p.Id == child.Info.Id).ToList());
            InspectProcess(child.Info.Id);
            creationInfo.Text = $"PID {child.Info.Id}: sospeso{(elevated ? " come amministratore" : "")}; {hookMode.Text}. Premi Riprendi, poi apri Monitoraggio.";
        }
        catch (Exception e) when (e is Win32Exception { NativeErrorCode: 1223 }
            || e.HResult == unchecked((int)0x800704C7))
        {
            if (!IsDisposed) { verdict.Text = "Creazione annullata nella richiesta UAC."; status.Text = "Nessun figlio avviato."; }
        }
        catch (Exception e) { if (!IsDisposed) ShowError(e); }
        finally
        {
            observation?.Dispose();
            busy = false;
            if (!IsDisposed)
            {
                launch.Enabled = check.Enabled = true;
                UpdateLaunchOptions(updateDescription: false);
                UpdateSuspendedControls();
            }
        }
    }

    private void UpdateSuspendedControls()
    {
        foreach (var entry in suspendedProcesses.Where(p => p.Value.HasExited).ToArray())
        {
            suspendedProcesses.Remove(entry.Key);
            entry.Value.Dispose();
        }
        int selected = (int)pid.Value;
        bool available = suspendedProcesses.TryGetValue(selected, out var child) && child.IsSuspended && !child.HasExited;
        resume.Enabled = stopSuspended.Enabled = !busy && available;
    }

    private void ResumeSelected()
    {
        int selected = (int)pid.Value;
        if (!suspendedProcesses.TryGetValue(selected, out var child)) return;
        uint previous = child.Resume();
        if (!child.IsSuspended)
        {
            suspendedProcesses.Remove(selected);
            child.Dispose();
        }
        CheckSelected();
        status.Text = previous > 1 ? $"Il thread resta sospeso: contatore {previous - 1}."
            : $"Thread principale del PID {selected} ripreso.";
        creationInfo.Text = $"PID {selected}: {(previous > 1 ? "ancora sospeso" : "ripreso; esecuzione consentita")}.";
        UpdateSuspendedControls();
    }

    private void StopSelected()
    {
        int selected = (int)pid.Value;
        if (!suspendedProcesses.TryGetValue(selected, out var child)) return;
        child.Terminate();
        suspendedProcesses.Remove(selected);
        child.Dispose();
        CheckSelected();
        creationInfo.Text = $"Processo sospeso PID {selected} terminato.";
        UpdateSuspendedControls();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        observerTimer.Stop();
        observerTimer.Dispose();
        monitoringWindow.Dispose();
        foreach (var observer in observers) observer.Dispose();
        observers.Clear();
        lifetime.Cancel();
        foreach (var child in suspendedProcesses.Values) child.Dispose();
        suspendedProcesses.Clear();
        lifetime.Dispose();
        base.OnFormClosed(e);
    }

    private void PollObservers()
    {
        bool changed = false;
        foreach (var observer in observers)
        {
            var incoming = observer.Drain();
            if (incoming.Count == 0) continue;
            observedCalls.AddRange(incoming);
            changed = true;
        }
        if (observedCalls.Count > 4000) observedCalls.RemoveRange(0, observedCalls.Count - 4000);
        if (changed) RefreshEventRows();
        var current = observers.LastOrDefault(o => o.ProcessId == (int)pid.Value) ?? observers.LastOrDefault();
        observerStatus.Text = current is null ? "Spunta Avvio sospeso per predisporre la DLL hook. Nessun log su disco."
            : $"PID {current.ProcessId}: {current.State} | eventi in memoria: {observedCalls.Count}/4000 | eventi persi: {observers.Sum(o => o.Dropped)}";
        nativeEvents.SetStatus(observerStatus.Text);
    }

    private void RefreshEventRows()
    {
        string filter = eventFilter.Text.Trim();
        displayedCalls = observedCalls.Where(e => !e.IsNative && (fileApiFilter.SelectedIndex == 0 || e.Api == fileApiFilter.Text)
            && (filter.Length == 0 || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Api.Contains(filter, StringComparison.OrdinalIgnoreCase))).TakeLast(1000).ToList();
        eventsGrid.RowCount = displayedCalls.Count;
        eventsGrid.Invalidate();
        nativeEvents.SetCalls(observedCalls);
    }

    private void UpdateDefaultWorkingDirectory()
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(executable.Text.Trim().Trim('"')))!;
            if (string.IsNullOrWhiteSpace(workingDirectory.Text)
                || string.Equals(workingDirectory.Text, lastExecutableDirectory, StringComparison.OrdinalIgnoreCase))
                workingDirectory.Text = directory;
            lastExecutableDirectory = directory;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
    }

    private void CheckSelected()
    {
        int selectedPid = (int)pid.Value;
        var entries = ProcessCatalog.Snapshot();
        UpdateSuspendedControls();
        var selected = entries.FirstOrDefault(p => p.Id == selectedPid);
        tree.Nodes.Clear();
        details.Clear();
        if (selected is null)
        {
            verdict.Text = $"PID {selectedPid}: processo terminato o non presente.";
            status.Text = "L'elenco mostra solo processi attualmente presenti.";
            return;
        }
        var started = ProcessCatalog.StartTime(selectedPid);
        if (trackedPid == selectedPid && trackedStart.HasValue && started.HasValue && trackedStart != started)
        {
            verdict.Text = $"PID {selectedPid} riutilizzato: il processo avviato in precedenza e terminato.";
            return;
        }
        var parent = entries.FirstOrDefault(p => p.Id == selected.ParentId);
        bool verified = parent is not null && ProcessCatalog.IsVerifiedParent(selected, parent);
        bool explorer = verified && string.Equals(parent!.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase);
        verdict.ForeColor = explorer ? Color.FromArgb(15, 118, 74) : Color.FromArgb(146, 88, 15);
        verdict.Text = parent is null
            ? $"Padre non presente nello snapshot · PID registrato: {selected.ParentId}"
            : verified ? $"Padre verificato: {parent.Name} · PID {parent.Id}" : $"Padre non verificabile · PID registrato: {selected.ParentId}";
        var node = new TreeNode($"{selected.Name} · PID {selected.Id}  [selezionato]") { Tag = selected };
        if (verified)
        {
            var root = new TreeNode($"{parent!.Name} · PID {parent.Id}  [padre]") { Tag = parent };
            root.Nodes.Add(node);
            tree.Nodes.Add(root);
        }
        else tree.Nodes.Add(node);
        int children = 0;
        foreach (var child in entries.Where(p => p.ParentId == selectedPid && p.Id != selectedPid).OrderBy(p => p.Name))
        {
            // Esclude collegamenti causati dal riutilizzo di un PID.
            if (!ProcessCatalog.IsVerifiedParent(child, selected)) continue;
            node.Nodes.Add(new TreeNode($"{child.Name} · PID {child.Id}  [figlio]") { Tag = child });
            children++;
        }
        tree.ExpandAll();
        tree.SelectedNode = node;
        details.Text = Describe(selected);
        status.Text = $"Aggiornato alle {DateTime.Now:HH:mm:ss} · {children} figli diretti verificati. Clicca un nodo per i dettagli.";
    }

    internal void InspectProcess(int processId)
    {
        pid.Value = processId;
        CheckSelected();
    }

    private string Describe(ProcessEntry entry)
    {
        var lines = new List<string>
        {
            $"Nome:       {entry.Name}", $"PID:        {entry.Id}", $"PID padre:  {entry.ParentId}",
            "", $"Percorso:\r\n{ProcessCatalog.ExecutablePath(entry.Id) ?? "Non disponibile"}", ""
        };
        var security = ProcessCatalog.Security(entry.Id);
        if (suspendedProcesses.TryGetValue(entry.Id, out var paused) && paused.IsSuspended && !paused.HasExited)
        {
            lines.Add("Stato: thread principale sospeso (gestito da questa app)");
            lines.Add($"Thread principale: {paused.ThreadId}");
            lines.Add("");
        }
        lines.Add($"Amministratore: {(security.Elevated is true ? "Si" : security.Elevated is false ? "No" : "Non disponibile")}");
        lines.Add($"Integrita:      {security.Integrity}");
        lines.Add("");
        if (trackedPid == entry.Id && trackedStart.HasValue
            && trackedStart == ProcessCatalog.StartTime(entry.Id) && requestedWorkingDirectory is not null)
        {
            lines.Add($"Cartella di lavoro all'avvio:\r\n{requestedWorkingDirectory}");
            lines.Add("");
        }
        if (exercise?.Id == entry.Id && exercise.StartedUtc.HasValue
            && exercise.StartedUtc == ProcessCatalog.StartTime(entry.Id))
        {
            lines.Add($"Creatore X: questa app, PID {exercise.CreatorId}");
            lines.Add($"Padre richiesto: Explorer, PID {exercise.ParentId}");
            lines.Add("Il creatore e noto perche l'avvio e avvenuto qui.");
            lines.Add("");
        }
        try
        {
            using var p = Process.GetProcessById(entry.Id);
            lines.Add($"Avvio:      {p.StartTime:dd/MM/yyyy HH:mm:ss}");
            lines.Add($"RAM:        {p.WorkingSet64 / 1024.0 / 1024.0:N1} MiB");
            lines.Add($"Thread:     {p.Threads.Count}");
            lines.Add($"Sessione:   {p.SessionId}");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        { lines.Add("Dettagli non disponibili: processo terminato o accesso negato."); }
        lines.Add("\r\nRelazioni ricavate da Windows.\r\nI PID possono essere riutilizzati; i legami\r\nsono verificati anche con gli orari di avvio.\r\nI figli terminati non sono elencati.");
        return string.Join("\r\n", lines);
    }

    private void SetCandidates(List<ProcessEntry> entries)
    {
        candidates.Items.Clear();
        foreach (var entry in entries.OrderBy(p => p.Name).ThenBy(p => p.Id)) candidates.Items.Add(entry);
        candidates.SelectedIndex = entries.Count == 1 ? 0 : -1;
    }
    private void RefreshProcesses()
    {
        SetCandidates(ProcessCatalog.Snapshot());
        status.Text = "Scegli un processo dall'elenco oppure inserisci il PID, poi premi Controlla.";
    }
    private void Guard(Action action)
    {
        try { action(); } catch (Exception e) { ShowError(e); }
    }
    private void ShowError(Exception error)
    {
        verdict.Text = "Operazione non riuscita.";
        creationInfo.Text = "Avvio non completato. Controlla eseguibile e cartella di lavoro.";
        status.Text = error.Message;
        MessageBox.Show(this, error.Message, "Laboratorio processi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
