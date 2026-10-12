namespace CodexMeter;

internal sealed class PromptRulesForm : Form
{
    private readonly PromptAutomation automation;
    private readonly List<PromptRule> rules;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 80 };
    private readonly CheckBox enabled = new() { Text = "Enable this rule", AutoSize = true };
    private readonly ComboBox action = Choice("Run Codex prompt", "Pause recent goals (direct)", "Resume recent goals (direct)");
    private readonly Label actionNote = new() { Dock = DockStyle.Fill };
    private readonly ComboBox quota = Choice("Lowest remaining across all windows", "Codex weekly", "Codex 5-hour");
    private readonly ComboBox direction = Choice("Rises to or above (≥)", "Falls to or below (≤)");
    private readonly NumericUpDown threshold = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Width = 78 };
    private readonly TextBox prompt = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 32_000, AcceptsReturn = true };
    private readonly TextBox folder = new() { Dock = DockStyle.Fill };
    private readonly ComboBox permissions = Choice("Read-only", "Allow edits in working folder");
    private readonly NumericUpDown timeout = new() { Minimum = 1, Maximum = 60, Width = 70 };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly TextBox result = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TableLayoutPanel editor;
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private int selected = -1;
    private bool loading;

    public PromptRulesForm(PromptAutomation automation)
    {
        this.automation = automation;
        rules = automation.Document.Rules.ToList();
        Text = "Codex Meter · Prompt rules";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1010, 740);
        MinimumSize = new Size(920, 680);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 3 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var intro = new Label { Dock = DockStyle.Fill, Text = "Run an action when remaining quota crosses a threshold. A rule runs once, then rearms on the opposite side.\nNew or edited rules take a baseline first. Checked with each successful 10-minute poll; a reserve is not a hard spending cap." };
        shell.Controls.Add(intro, 0, 0); shell.SetColumnSpan(intro, 2);
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
        navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        navigation.Controls.Add(list, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("Add", (_, _) => AddRule())); buttons.Controls.Add(Button("Remove", (_, _) => RemoveRule()));
        navigation.Controls.Add(buttons, 0, 1); shell.Controls.Add(navigation, 0, 1);
        editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 11, AutoScroll = true };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 32, 32, 36, 36, 132, 36, 36, 34, 65, 42 }) editor.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        action.Dock = DockStyle.None; action.Width = 270;
        enabled.Margin = new Padding(3, 6, 8, 3);
        actionRow.Controls.Add(enabled); actionRow.Controls.Add(action);
        AddRow(0, "Rule name", name); AddRow(1, "Action", actionRow); AddRow(2, "Quota", quota);
        var boundary = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        direction.Width = 220; direction.Dock = DockStyle.None;
        boundary.Controls.Add(direction); boundary.Controls.Add(threshold);
        boundary.Controls.Add(new Label { Text = "% remaining", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        AddRow(3, "Trigger", boundary); AddRow(4, "Codex prompt", prompt);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        folderRow.Controls.Add(folder, 0, 0); folderRow.Controls.Add(Button("Browse…", (_, _) => BrowseFolder()), 1, 0);
        AddRow(5, "Working folder", folderRow); AddRow(6, "Permissions", permissions);
        var timeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        timeRow.Controls.Add(timeout); timeRow.Controls.Add(new Label { Text = "minutes maximum per run", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        AddRow(7, "Timeout", timeRow);
        editor.Controls.Add(actionNote, 0, 8); editor.SetColumnSpan(actionNote, 2);
        AddRow(9, "Last attempt", status); AddRow(10, "Final response", result); shell.Controls.Add(editor, 1, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = Button("Save rules", (_, _) => SaveRules()); save.Width = 120;
        var close = Button("Close", (_, _) => Close()); close.Width = 100;
        footer.Controls.Add(save); footer.Controls.Add(close); footer.Controls.Add(Button("Cancel runs", (_, _) => { automation.CancelRuns(); UpdateStatus(); }));
        shell.Controls.Add(footer, 0, 2); shell.SetColumnSpan(footer, 2); Controls.Add(shell);
        list.SelectedIndexChanged += (_, _) => SelectRule(); action.SelectedIndexChanged += (_, _) => UpdateAction();
        refresh.Tick += (_, _) => UpdateStatus(); refresh.Start();
        RefreshList(rules.Count == 0 ? -1 : 0);
    }

    private static ComboBox Choice(params string[] choices)
    {
        var box = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        box.Items.AddRange(choices); box.SelectedIndex = 0; return box;
    }
    private static Button Button(string text, EventHandler action)
    {
        var button = new Button { Text = text, MinimumSize = new Size(95, 32), AutoSize = true };
        button.Click += action; return button;
    }
    private void AddRow(int row, string label, Control control)
    {
        editor.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) }, 0, row);
        editor.Controls.Add(control, 1, row);
    }
    private void CaptureEdits()
    {
        if (loading || selected < 0 || selected >= rules.Count) return;
        rules[selected] = rules[selected] with { Name = name.Text.Trim(), Enabled = enabled.Checked, Action = (PromptAction)action.SelectedIndex, Scope = (QuotaScope)quota.SelectedIndex,
            Direction = (ThresholdDirection)direction.SelectedIndex, Threshold = (double)threshold.Value, Prompt = prompt.Text,
            WorkingDirectory = folder.Text.Trim(), Permissions = (PromptPermissions)permissions.SelectedIndex, TimeoutMinutes = (int)timeout.Value };
    }
    private void SelectRule()
    {
        if (loading) return;
        CaptureEdits(); selected = list.SelectedIndex; loading = true; editor.Enabled = selected >= 0;
        if (selected >= 0)
        {
            var rule = rules[selected]; name.Text = rule.Name; enabled.Checked = rule.Enabled; action.SelectedIndex = (int)rule.Action; quota.SelectedIndex = (int)rule.Scope;
            direction.SelectedIndex = (int)rule.Direction; threshold.Value = (decimal)rule.Threshold; prompt.Text = rule.Prompt;
            folder.Text = rule.WorkingDirectory; permissions.SelectedIndex = (int)rule.Permissions; timeout.Value = rule.TimeoutMinutes;
        }
        loading = false; UpdateAction(); UpdateStatus();
    }
    private void UpdateAction()
    {
        var direct = action.SelectedIndex > 0;
        prompt.Enabled = !direct; folder.Parent!.Enabled = !direct; permissions.Enabled = !direct; timeout.Enabled = !direct;
        actionNote.Text = direct
            ? "Checks 20 recent non-archived local sessions through goal APIs (60-second limit). No model call.\nPreserves objectives and budgets; skips blocked, completed, and budget-limited goals."
            : "Runs a separate CLI session with your Codex login and model; consumes quota.\nDesktop chat tools are not supplied. Extra permissions cannot be approved during a run.";
    }
    private void RefreshList(int index)
    {
        loading = true; list.Items.Clear(); foreach (var rule in rules) list.Items.Add((rule.Enabled ? "● " : "○ ") + rule.Name);
        selected = -1; list.SelectedIndex = index; loading = false; SelectRule();
    }
    private void AddRule() { CaptureEdits(); if (rules.Count >= 32) return; rules.Add(new()); RefreshList(rules.Count - 1); }
    private void RemoveRule() { if (selected < 0) return; var index = selected; rules.RemoveAt(index); RefreshList(Math.Min(index, rules.Count - 1)); }
    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the Codex working folder", UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(folder.Text) ? folder.Text : AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
    }
    private void UpdateStatus()
    {
        if (automation.Error is not null) { status.Text = automation.Error; return; }
        if (selected < 0) { status.Text = "No rule selected"; result.Text = ""; return; }
        var rule = rules[selected]; var state = automation.Document.States.GetValueOrDefault(rule.Id);
        var armed = state?.OnTriggerSide switch { true => "Waiting to cross back", false => "Armed", _ => "Waiting for first reading" };
        status.Text = (rule.Enabled ? armed : "Disabled") + (state?.LastRunAt is { } at ? $" · {state.Status} · {at.ToLocalTime():g}" : "");
        var output = state?.LastOutput ?? ""; if (result.Text != output) result.Text = output;
    }
    private void SaveRules()
    {
        CaptureEdits();
        try { automation.Configure(rules); DialogResult = DialogResult.OK; Close(); }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { MessageBox.Show(this, ex is FormatException ? ex.Message : "Could not save rules. Check access to Meter's data folder.", "Prompt rules", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    internal void CheckPreviewSelections()
    {
        if (selected >= 0 && (quota.SelectedItem is null || direction.SelectedItem is null || permissions.SelectedItem is null || action.SelectedItem is null))
            throw new InvalidOperationException("A rule selector has no selected value.");
    }
    protected override void Dispose(bool disposing) { if (disposing) refresh.Dispose(); base.Dispose(disposing); }
}
