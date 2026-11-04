namespace ProcessLauncher;

// Displays the bounded in-memory native event collection in Monitoraggio.
internal sealed class ApiEventsView : UserControl
{
    private readonly ComboBox api = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245 };
    private readonly TextBox search = new() { Width = 240, PlaceholderText = "Filtra PID, indirizzi, risultati..." };
    private readonly Label state = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10) };
    private IReadOnlyList<ObservedCall> source = Array.Empty<ObservedCall>();
    private List<ObservedCall> rows = new();

    public ApiEventsView()
    {
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        api.Items.AddRange(new object[] { "Tutte le Native API", "NtQueryVirtualMemory", "NtQuerySystemInformation", "NtQueryInformationProcess", "NtReadVirtualMemory", "NtWriteVirtualMemory" });
        api.SelectedIndex = 0;
        tools.Controls.AddRange(new Control[] { api, search });
        layout.Controls.Add(tools, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            Size = new Size(900, 350), SplitterDistance = 230, Panel1MinSize = 70, Panel2MinSize = 50 };
        split.Panel1.Controls.Add(grid);
        split.Panel2.Controls.Add(detail);
        layout.Controls.Add(split, 0, 1);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Ingressi ntdll x86/x64: nei processi x86 precedono WOW64. NTSTATUS e lunghezze sono risposte originali.\nLunghezza restituita: puo indicare lo spazio necessario in caso di errore. Nessun contenuto di memoria acquisito." }, 0, 2);
        layout.Controls.Add(state, 0, 3);
        Controls.Add(layout);
        foreach (var col in new[] { ("Ora", 105), ("PID", 65), ("TID", 65), ("Architettura", 110), ("API", 200), ("Richiesta", 500), ("Risposta", 275), ("Stato", 260), ("Lunghezza restituita", 145), ("Dati risposta", 500) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = col.Item1, Width = col.Item2 });
        grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
            var c = rows[e.RowIndex];
            e.Value = e.ColumnIndex switch { 0 => c.Time.ToString("HH:mm:ss.fff"), 1 => c.Pid, 2 => c.Tid,
                3 => c.Architecture, 4 => c.Api, 5 => c.Parameters, 6 => c.Result, 7 => c.ErrorText, 8 => c.Bytes, 9 => c.Path, _ => null };
        };
        grid.SelectionChanged += (_, _) => ShowDetail();
        api.SelectedIndexChanged += (_, _) => RefreshRows();
        search.TextChanged += (_, _) => RefreshRows();
    }

    public void SetCalls(IReadOnlyList<ObservedCall> calls) { source = calls; RefreshRows(); }
    public void SetStatus(string text) => state.Text = text;
    private void RefreshRows()
    {
        string filter = search.Text.Trim();
        rows = source.Where(c => c.IsNative && (api.SelectedIndex == 0 || c.Api == api.Text)
            && (filter.Length == 0 || $"{c.Pid} {c.Api} {c.Parameters} {c.Result} {c.ErrorText} {c.Path}".Contains(filter, StringComparison.OrdinalIgnoreCase))).TakeLast(1000).ToList();
        grid.RowCount = rows.Count;
        grid.Invalidate();
        ShowDetail();
    }
    private void ShowDetail()
    {
        int index = grid.CurrentCell?.RowIndex ?? -1;
        detail.Text = index >= 0 && index < rows.Count ? $"{rows[index].Api} | {rows[index].Architecture}\r\n{rows[index].Parameters}\r\n{rows[index].Result} | {rows[index].ErrorText} | lunghezza restituita: {rows[index].Bytes}\r\n{rows[index].Path}" : "Seleziona una chiamata per leggere richiesta e risposta complete.";
    }
}
