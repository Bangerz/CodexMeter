using System.Runtime.InteropServices;

namespace CodexMeter;

internal sealed class UsagePopup : Form
{
    private readonly FlowLayoutPanel content;
    private readonly Label plan;
    private readonly Label status;
    private readonly Button refresh;
    private readonly ToolTip tips = new();
    private readonly List<Font> ownedFonts = [];
    public event EventHandler? RefreshRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool KeepOpen { get; set; }
    private static readonly Color Background = Color.FromArgb(25, 28, 34);
    private static readonly Color Muted = Color.FromArgb(164, 173, 188);

    public UsagePopup()
    {
        Text = "Codex usage";
        AccessibleName = "Codex usage limits";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        ForeColor = Color.White;
        Font = MakeFont(10);
        ClientSize = new Size(344, 300);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16), ColumnCount = 1, RowCount = 4 };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        header.Controls.Add(new Label { Text = "Codex", AutoSize = true, Font = MakeFont(14, FontStyle.Bold), Location = Point.Empty });
        plan = new Label { AutoSize = true, ForeColor = Muted, Location = new Point(86, 6) };
        header.Controls.Add(plan);
        var close = new Button { Text = "×", AccessibleName = "Close usage panel", Size = new Size(28, 27), Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat, ForeColor = Muted, BackColor = Background, TabIndex = 1 };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Hide();
        header.Controls.Add(close);
        content = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        status = new Label { Dock = DockStyle.Fill, ForeColor = Muted, Font = MakeFont(9), Margin = new Padding(0, 8, 0, 0) };
        var actions = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        refresh = new Button { Text = "Refresh", AccessibleName = "Refresh usage now", FlatStyle = FlatStyle.Flat,
            Size = new Size(92, 32), Dock = DockStyle.Right, BackColor = Color.FromArgb(46, 53, 65), ForeColor = Color.White,
            TabIndex = 0 };
        refresh.FlatAppearance.BorderColor = Color.FromArgb(69, 78, 92);
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        actions.Controls.Add(refresh);
        shell.Controls.Add(header, 0, 0);
        shell.Controls.Add(content, 0, 1);
        shell.Controls.Add(status, 0, 2);
        shell.Controls.Add(actions, 0, 3);
        Controls.Add(shell);
        Deactivate += (_, _) => { if (!KeepOpen) Hide(); };
    }

    public void UpdateUsage(UsageSnapshot? snapshot, string? error, bool loading, bool stale)
    {
        SuspendLayout();
        foreach (Control child in content.Controls.Cast<Control>().ToArray()) { content.Controls.Remove(child); child.Dispose(); }
        // Per-card fonts belong to the card and are explicitly disposed with it.
        plan.Text = snapshot?.Plan is { Length: > 0 } value ? char.ToUpperInvariant(value[0]) + value[1..] : "Usage";
        var scale = DeviceDpi / 96f;
        var multipleBuckets = snapshot?.Windows.Select(w => w.BucketId).Distinct().Count() > 1;
        var needsScroll = snapshot?.Windows.Count > 3;
        if (snapshot is { Windows.Count: > 0 })
        {
            foreach (var window in snapshot.Windows)
            {
                var card = new UsageCard(window, multipleBuckets, stale) { Width = (int)(300 * scale) - (needsScroll ? SystemInformation.VerticalScrollBarWidth : 0), Height = (int)(124 * scale),
                    Margin = new Padding(0, 0, 0, (int)(7 * scale)) };
                content.Controls.Add(card);
            }
        }
        else
        {
            content.Controls.Add(new Label { Text = loading ? "Checking your limits…" : error ?? "No usage windows were reported.\nSign in to Codex with a ChatGPT account.",
                ForeColor = Muted, Width = (int)(294 * scale), Height = (int)(96 * scale), Padding = new Padding(0, 20, 0, 0) });
        }
        var time = snapshot?.FetchedAt.ToLocalTime().ToString("h:mm tt");
        status.ForeColor = error is null && !stale ? Muted : TrayIcon.Amber;
        status.Text = loading ? "Refreshing…" : error is not null
            ? (snapshot is null ? error : $"Last known · {time}. Refresh failed.\n{error}")
            : snapshot is null ? "Every 15 min"
            : stale ? $"Last known · {time} · update overdue"
            : $"Updated {time} · Every 15 min";
        tips.SetToolTip(status, status.Text);
        refresh.Enabled = !loading;
        var count = Math.Max(1, snapshot?.Windows.Count ?? 0);
        ClientSize = new Size((int)(344 * scale), (int)(Math.Min(580, 140 + count * 131) * scale));
        ResumeLayout(true);
        if (Visible) PositionNearTray();
    }

    public void ShowNearTray()
    {
        PositionNearTray();
        Show();
        Activate();
        refresh.Focus();
    }

    private void PositionNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Height = Math.Min(Height, area.Height - 20);
        Location = new Point(Math.Max(area.Left, Math.Min(Cursor.Position.X - Width / 2, area.Right - Width - 10)),
            Math.Max(area.Top, area.Bottom - Height - 10));
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1, corners = 2;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
    }

    private Font MakeFont(float size, FontStyle style = FontStyle.Regular)
    {
        var font = new Font("Segoe UI", size, style);
        ownedFonts.Add(font);
        return font;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        tips.Dispose();
        foreach (var font in ownedFonts) font.Dispose();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

internal sealed class UsageCard : Control
{
    private readonly UsageWindow window;
    private readonly bool multipleBuckets;
    private readonly bool stale;
    public UsageCard(UsageWindow value, bool multiple, bool isStale)
    {
        window = value; multipleBuckets = multiple; stale = isStale;
        DoubleBuffered = true;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = $"{window.BucketName}, {window.WindowName}, {window.PercentText} remaining. {window.ResetText(DateTimeOffset.UtcNow)}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
        var logicalWidth = Width / (DeviceDpi / 96f);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var color = stale ? Color.FromArgb(164, 173, 188) : TrayIcon.ColorFor(window.Remaining, window.DurationMinutes);
        using var headingFont = new Font("Segoe UI", 13.33f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var numberFont = new Font("Segoe UI", window.Remaining is null ? 28 : 40, FontStyle.Bold, GraphicsUnit.Pixel);
        using var detailFont = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(235, 240, 248));
        using var muted = new SolidBrush(Color.FromArgb(164, 173, 188));
        using var accent = new SolidBrush(color);
        using var track = new SolidBrush(Color.FromArgb(49, 57, 69));
        using var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        var heading = multipleBuckets ? $"{window.BucketName} · {window.WindowName}" : window.WindowName;
        graphics.DrawString(heading, headingFont, text, new RectangleF(0, 0, logicalWidth - 2, 23), ellipsis);
        graphics.DrawString(window.PercentText, numberFont, accent, new RectangleF(-3, 21, 220, 58));
        if (window.Remaining is not null) graphics.DrawString(stale ? "last known" : "remaining", detailFont, muted, logicalWidth - 114, 47);
        graphics.FillRectangle(track, 1, 83, logicalWidth - 4, 5);
        if (window.Remaining is { } remaining) graphics.FillRectangle(accent, 1, 83, (float)((logicalWidth - 4) * remaining / 100), 5);
        graphics.DrawString(window.ResetText(DateTimeOffset.UtcNow), detailFont, muted, new RectangleF(0, 99, logicalWidth, 24), ellipsis);
    }
}

internal sealed class CostHoverPopup : Form
{
    private CostHistory? history;
    private string? error;
    private string quota = "Click the tray icon for usage limits";
    private readonly Font heading = new("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font body = new("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font money = new("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font small = new("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Color Muted = Color.FromArgb(164, 173, 188);

    public CostHoverPopup()
    {
        Text = "Codex cost history";
        AccessibleRole = AccessibleRole.ToolTip;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(25, 28, 34);
        ForeColor = Color.White;
        ClientSize = new Size(440, 500);
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW.
            return parameters;
        }
    }

    public void UpdateHistory(CostHistory? value, string? message, UsageSnapshot? snapshot, bool quotaStale)
    {
        history = value;
        error = message;
        quota = snapshot is { Windows.Count: > 0 }
            ? (quotaStale ? "Last known · " : "") + string.Join(" · ", snapshot.Windows.Take(2).Select(window => $"{window.WindowName}: {window.PercentText} left"))
            : "Click the tray icon for usage limits";
        var scale = DeviceDpi / 96f;
        ClientSize = new Size((int)(440 * scale), (int)((history is null ? 206 : 510) * scale));
        AccessibleName = history is null ? $"Codex cost history. {error}. {quota}"
            : "Codex API-equivalent USD estimates. " + quota + ". "
              + string.Join(". ", history.RecentSessions.Select(session => $"{session.DisplayName}: {Usd(session.CostUSD)}"));
        Invalidate();
    }

    public void ShowNearTray(Point anchor)
    {
        var area = Screen.FromPoint(anchor).WorkingArea;
        Location = new Point(Math.Max(area.Left + 8, Math.Min(anchor.X - Width / 2, area.Right - Width - 8)),
            Math.Max(area.Top + 8, area.Bottom - Height - 10));
        Show();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021) { message.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE / MA_NOACTIVATE.
        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var white = new SolidBrush(Color.FromArgb(235, 240, 248));
        using var muted = new SolidBrush(Muted);
        using var amber = new SolidBrush(TrayIcon.Amber);
        using var track = new SolidBrush(Color.FromArgb(39, 45, 55));
        using var line = new Pen(Color.FromArgb(55, 63, 76));
        using var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var right = new StringFormat { Alignment = StringAlignment.Far, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString("Codex · cost history", heading, white, 20, 17);
        graphics.DrawString("API-equivalent USD estimates · not billed spending", small, muted, 20, 40);
        graphics.DrawString(quota, body, muted, new RectangleF(20, 61, 400, 22), ellipsis);
        graphics.DrawLine(line, 20, 89, 420, 89);
        if (history is null)
        {
            graphics.DrawString(error ?? "No cost history is available.", body, amber, new RectangleF(20, 109, 395, 52));
            graphics.DrawString("Choose cost-history file… in the tray menu", small, muted, 20, 176);
            return;
        }
        var periods = history.Periods(DateOnly.FromDateTime(DateTime.Now));
        var totals = new[] { ("Today", periods.Today), ("This week · Monday start", periods.Week), ("This month", periods.Month), ("All time in file", periods.AllTime) };
        for (var index = 0; index < totals.Length; index++)
        {
            var x = 20 + index % 2 * 205;
            var y = 101 + index / 2 * 65;
            graphics.FillRectangle(track, x, y, 195, 58);
            graphics.DrawString(totals[index].Item1, small, muted, x + 10, y + 6);
            graphics.DrawString(Usd(totals[index].Item2), money, white, new RectangleF(x + 10, y + 24, 177, 31), ellipsis);
        }
        graphics.DrawString("5 most recent sessions", heading, white, 20, 240);
        if (history.RecentSessions.Count == 0)
            graphics.DrawString("No named sessions in the file", body, muted, 20, 273);
        for (var index = 0; index < history.RecentSessions.Count; index++)
        {
            var session = history.RecentSessions[index];
            var y = 271 + index * 37;
            graphics.DrawString(session.DisplayName, body, white, new RectangleF(20, y, 294, 20), ellipsis);
            graphics.DrawString(Usd(session.CostUSD) + (session.MissingPricing ? "*" : ""), body, white, new RectangleF(316, y, 104, 20), right);
            graphics.DrawString(session.LastActivity.ToLocalTime().ToString("MMM d, h:mm tt"), small, muted, 20, y + 19);
        }
        var stale = history.IsStale(DateTimeOffset.UtcNow);
        graphics.DrawLine(line, 20, 462, 420, 462);
        var freshness = $"Collected {history.GeneratedAt.ToLocalTime():MMM d, yyyy h:mm tt}" + (stale ? " · stale (>7 days)" : "");
        graphics.DrawString(freshness, small, stale ? amber : muted, new RectangleF(20, 471, 400, 17), ellipsis);
        graphics.DrawString(history.HasMissingPricing ? "* Some pricing unavailable; estimates are incomplete" : "Daily totals use the collector’s local calendar dates", small,
            history.HasMissingPricing ? amber : muted, new RectangleF(20, 491, 400, 16), ellipsis);
    }

    private static string Usd(double value) => "$" + value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        heading.Dispose();
        body.Dispose();
        money.Dispose();
        small.Dispose();
    }
}
