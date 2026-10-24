namespace CodexMeter;

internal sealed class PromptRulesForm : Form
{
    private readonly PromptAutomation automation;
    private readonly List<PromptRule> rules;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 80 };
    private readonly CheckBox enabled = new() { Text = "Enable this rule", AutoSize = true };
    private readonly ComboBox action = Choice("Run CLI prompt in folder", "Pause recent goals (direct)", "Resume recent goals (direct)", "Send to desktop chat");
    private readonly Label actionNote = new() { Dock = DockStyle.Fill };
    private readonly ComboBox quota = Choice("Lowest remaining across all windows", "Codex weekly", "Codex 5-hour");
    private readonly ComboBox direction = Choice("Rises to or above (≥)", "Falls to or below (≤)");
    private readonly NumericUpDown threshold = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Width = 78 };
    private readonly TextBox prompt = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 32_000, AcceptsReturn = true };
    private readonly NumericUpDown recentSessions = new() { Minimum = 1, Maximum = 100, Value = 20, Width = 78 };
    private readonly FlowLayoutPanel goalSettings = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label actionSettingsLabel;
    private readonly TextBox folder = new() { Dock = DockStyle.Fill };
    private readonly TextBox desktopThread = new() { Dock = DockStyle.Fill, PlaceholderText = "Chat UUID or codex://threads/... link" };
    private readonly Button chooseChat;
    private readonly Control folderRow;
    private readonly Control desktopRow;
    private readonly Label destinationLabel;
    private string desktopTitle = "";
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox permissions = Choice("Read-only", "Allow edits in working folder");
    private readonly Label desktopPermissions = new() { Dock = DockStyle.Fill, Text = "Uses the destination chat's settings and available tools.", Padding = new Padding(0, 5, 0, 0) };
    private readonly NumericUpDown timeout = new() { Minimum = 1, Maximum = 60, Width = 70 };
    private readonly Control timeRow;
    private readonly Label desktopTimeout = new() { Dock = DockStyle.Fill, Text = "30 seconds to confirm submission; chat runs independently.", Padding = new Padding(0, 5, 0, 0) };
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
        AddRow(3, "Trigger", boundary);
        var sessionRow = new FlowLayoutPanel { Width = 520, Height = 38, WrapContents = false };
        sessionRow.Controls.Add(new Label { Text = "Recent sessions to check", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        sessionRow.Controls.Add(recentSessions);
        goalSettings.Controls.Add(sessionRow);
        goalSettings.Controls.Add(new Label { Width = 520, Height = 78,
            Text = "Checks recent local desktop, CLI and IDE sessions, newest first.\nUses your Codex profile directly; no prompt or model call." });
        var actionSettings = new Panel { Dock = DockStyle.Fill };
        actionSettings.Controls.Add(prompt); actionSettings.Controls.Add(goalSettings);
        actionSettingsLabel = AddRow(4, "Codex prompt", actionSettings);
        var folders = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        folders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); folders.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        folders.Controls.Add(folder, 0, 0); folders.Controls.Add(Button("Browse…", (_, _) => BrowseFolder()), 1, 0); folderRow = folders;
        var chats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        chats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); chats.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        chooseChat = Button("Choose…", async (_, _) => await ChooseDesktopChatAsync());
        chats.Controls.Add(desktopThread, 0, 0); chats.Controls.Add(chooseChat, 1, 0); desktopRow = chats;
        var destination = new Panel { Dock = DockStyle.Fill }; destination.Controls.Add(folderRow); destination.Controls.Add(desktopRow);
        destinationLabel = AddRow(5, "Working folder", destination);
        var permissionSettings = new Panel { Dock = DockStyle.Fill }; permissionSettings.Controls.Add(permissions); permissionSettings.Controls.Add(desktopPermissions);
        AddRow(6, "Permissions", permissionSettings);
        timeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        timeRow.Controls.Add(timeout); timeRow.Controls.Add(new Label { Text = "minutes maximum per run", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        var timeSettings = new Panel { Dock = DockStyle.Fill }; timeSettings.Controls.Add(timeRow); timeSettings.Controls.Add(desktopTimeout);
        AddRow(7, "Timeout", timeSettings);
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
    private Label AddRow(int row, string label, Control control)
    {
        var caption = new Label { Text = label, Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) };
        editor.Controls.Add(caption, 0, row);
        editor.Controls.Add(control, 1, row);
        return caption;
    }
    private void CaptureEdits()
    {
        if (loading || selected < 0 || selected >= rules.Count) return;
        rules[selected] = rules[selected] with { Name = name.Text.Trim(), Enabled = enabled.Checked, Action = (PromptAction)action.SelectedIndex, Scope = (QuotaScope)quota.SelectedIndex,
            Direction = (ThresholdDirection)direction.SelectedIndex, Threshold = (double)threshold.Value, Prompt = prompt.Text, RecentSessionLimit = (int)recentSessions.Value,
            DesktopThreadId = ParseDesktopThreadId(desktopThread.Text), DesktopThreadTitle = desktopTitle,
            WorkingDirectory = folder.Text.Trim(), Permissions = (PromptPermissions)permissions.SelectedIndex, TimeoutMinutes = (int)timeout.Value };
    }
    private void SelectRule()
    {
        if (loading) return;
        CaptureEdits(); selected = list.SelectedIndex; loading = true; editor.Enabled = selected >= 0;
        if (selected >= 0)
        {
            var rule = rules[selected]; name.Text = rule.Name; enabled.Checked = rule.Enabled; action.SelectedIndex = (int)rule.Action; quota.SelectedIndex = (int)rule.Scope;
            direction.SelectedIndex = (int)rule.Direction; threshold.Value = (decimal)rule.Threshold; prompt.Text = rule.Prompt; recentSessions.Value = rule.RecentSessionLimit;
            folder.Text = rule.WorkingDirectory; permissions.SelectedIndex = (int)rule.Permissions; timeout.Value = rule.TimeoutMinutes;
            desktopThread.Text = rule.DesktopThreadId; desktopTitle = rule.DesktopThreadTitle;
        }
        loading = false; UpdateAction(); UpdateStatus();
    }
    private void UpdateAction()
    {
        var direct = (PromptAction)action.SelectedIndex is PromptAction.PauseRecentGoals or PromptAction.ResumeRecentGoals;
        var desktop = (PromptAction)action.SelectedIndex == PromptAction.SendToDesktopChat;
        prompt.Visible = !direct; goalSettings.Visible = direct;
        actionSettingsLabel.Text = direct ? "Goal action" : "Codex prompt";
        folderRow.Visible = !desktop; desktopRow.Visible = desktop; destinationLabel.Text = desktop ? "Desktop chat" : "Working folder";
        folderRow.Enabled = !direct; permissions.Visible = !desktop; permissions.Enabled = !direct; desktopPermissions.Visible = desktop;
        timeRow.Visible = !desktop; timeout.Enabled = !direct; desktopTimeout.Visible = desktop;
        actionNote.Text = desktop ? "Sends once to an existing local Codex chat through the open desktop app. Consumes quota.\nNo automatic retry. Submission is not completion; read the response in that chat."
            : direct
            ? "Direct goal API action (60-second limit). Preserves objectives and budgets.\nSkips blocked, completed, usage-limited and budget-limited goals."
            : "Runs a separate CLI session and consumes quota; desktop chat tools are unavailable.\nTo pause or resume goals across sessions, select a direct goal action above.";
    }
    private void RefreshList(int index)
    {
        loading = true; list.Items.Clear(); foreach (var rule in rules) list.Items.Add((rule.Enabled ? "● " : "○ ") + rule.Name + (rule.Action switch { PromptAction.CodexPrompt => " · CLI", PromptAction.SendToDesktopChat => " · desktop", _ => " · direct" }));
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
    private async Task ChooseDesktopChatAsync()
    {
        chooseChat.Enabled = false;
        try
        {
            var chats = await new CodexDesktopChatClient().ListAsync(lifetime.Token);
            if (IsDisposed) return;
            using var picker = new Form { Text = "Choose a local Codex desktop chat", Size = new Size(720, 450), StartPosition = FormStartPosition.CenterParent,
                Font = Font, MinimizeBox = false, MaximizeBox = false };
            var choices = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            choices.Items.AddRange(chats.Cast<object>().ToArray());
            var identity = new Label { Dock = DockStyle.Bottom, Height = 30, Text = "Select a chat to see its UUID." };
            choices.SelectedIndexChanged += (_, _) => identity.Text = (choices.SelectedItem as DesktopChat)?.Id ?? "";
            var select = Button("Select chat", (_, _) => { if (choices.SelectedItem is not null) picker.DialogResult = DialogResult.OK; });
            select.Dock = DockStyle.Bottom;
            picker.Controls.Add(choices); picker.Controls.Add(identity); picker.Controls.Add(select); picker.AcceptButton = select;
            choices.DoubleClick += (_, _) => { if (choices.SelectedItem is not null) picker.DialogResult = DialogResult.OK; };
            if (picker.ShowDialog(this) == DialogResult.OK && choices.SelectedItem is DesktopChat chat)
            { desktopThread.Text = chat.Id; desktopTitle = chat.Title; }
        }
        catch (Exception ex) when (ex is DesktopBridgeException or GoalApiException or UsageException or IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (!IsDisposed && !lifetime.IsCancellationRequested) MessageBox.Show(this, "Could not list desktop chats. Open Codex and try again, or paste a local chat's UUID/link.", "Desktop chats", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { if (!IsDisposed) chooseChat.Enabled = true; }
    }
    internal static string ParseDesktopThreadId(string text)
    {
        text = text.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "codex" && uri.Host == "threads") text = uri.AbsolutePath.Trim('/');
        return Guid.TryParse(text, out var id) ? id.ToString() : text;
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
        if (selected >= 0)
        {
            var before = rules[selected];
            CaptureEdits();
            if (rules[selected] != before) throw new InvalidOperationException("The editor changed a rule without user edits.");
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { lifetime.Cancel(); refresh.Dispose(); lifetime.Dispose(); } base.Dispose(disposing); }
}
