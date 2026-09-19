using Microsoft.Win32;

namespace CodexMeter;

internal sealed class TrayApp : ApplicationContext
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private readonly NotifyIcon tray;
    private readonly UsagePopup popup = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = (int)PollInterval.TotalMilliseconds };
    private readonly CancellationTokenSource lifetime = new();
    private readonly CodexClient client = new();
    private readonly ToolStripMenuItem refreshItem;
    private readonly EventHandler idleHandler;
    private UsageSnapshot? snapshot;
    private string? error;
    private bool loading;
    private bool exiting;
    private DateTimeOffset nextPoll;
    private Icon? currentIcon;
    private bool IsStale => snapshot is not null && (error is not null || DateTimeOffset.UtcNow - snapshot.FetchedAt > PollInterval + TimeSpan.FromMinutes(1));

    public TrayApp()
    {
        // Ensure a UI handle exists for resume/theme events arriving on background threads.
        _ = popup.Handle;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show limits", null, (_, _) => ShowPopup());
        refreshItem = new ToolStripMenuItem("Refresh now", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Startup.IsEnabled() };
        startup.Click += (_, _) =>
        {
            try { Startup.SetEnabled(!startup.Checked); startup.Checked = Startup.IsEnabled(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { MessageBox.Show("Windows could not update the startup setting.", "Codex Meter", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        tray = new NotifyIcon { Text = "Codex · Checking usage…", ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { if (popup.Visible) popup.Hide(); else ShowPopup(); } };
        popup.RefreshRequested += async (_, _) => await RefreshAsync();
        timer.Tick += async (_, _) => await RefreshAsync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnPreferencesChanged;
        UpdateDisplay();
        tray.Visible = true;
        nextPoll = DateTimeOffset.UtcNow;
        idleHandler = async (_, _) => { Application.Idle -= idleHandler; await RefreshAsync(); };
        Application.Idle += idleHandler;
    }

    private async Task RefreshAsync()
    {
        if (loading || exiting) return;
        loading = true;
        timer.Stop();
        UpdateDisplay();
        try
        {
            snapshot = await client.FetchAsync(lifetime.Token);
            error = null;
        }
        catch (OperationCanceledException) when (exiting) { return; }
        catch (UsageException ex) { error = ex.Message; }
        catch (Exception) { error = "Could not read usage. Open Codex and try Refresh."; }
        finally
        {
            loading = false;
            if (!exiting)
            {
                nextPoll = DateTimeOffset.UtcNow + PollInterval;
                UpdateDisplay();
                timer.Start();
            }
        }
    }

    private void UpdateDisplay()
    {
        var replacement = TrayIcon.Create(snapshot, IsStale || error is not null);
        tray.Icon = replacement;
        currentIcon?.Dispose();
        currentIcon = replacement;
        var summaries = snapshot?.Windows.Select(w => $"{w.WindowName}: {w.PercentText} left").ToArray() ?? [];
        var title = error is not null || IsStale ? "Codex · Update needed" : "Codex · Remaining";
        var tooltip = title + (summaries.Length > 0 ? "\n" + string.Join("\n", summaries) : loading ? "\nChecking…" : "\nClick for details");
        tray.Text = tooltip.Length <= 127 ? tooltip : tooltip[..124] + "…";
        refreshItem.Enabled = !loading;
        popup.UpdateUsage(snapshot, error, loading, IsStale);
    }

    private void ShowPopup()
    {
        UpdateDisplay();
        popup.ShowNearTray();
        if (DateTimeOffset.UtcNow >= nextPoll) _ = RefreshAsync();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !exiting)
            try { popup.BeginInvoke(() => { if (!exiting && DateTimeOffset.UtcNow >= nextPoll) _ = RefreshAsync(); }); }
            catch (InvalidOperationException) { }
    }

    private void OnPreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!exiting) try { popup.BeginInvoke(() => { if (!exiting) UpdateDisplay(); }); }
            catch (InvalidOperationException) { }
    }

    protected override void ExitThreadCore()
    {
        if (exiting) return;
        exiting = true;
        Application.Idle -= idleHandler;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferencesChanged;
        lifetime.Cancel();
        timer.Stop();
        timer.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        currentIcon?.Dispose();
        popup.Dispose();
        base.ExitThreadCore();
    }
}

internal static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "CodexMeter";
    private static string Command => $"\"{Environment.ProcessPath}\"";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, false);
    }
}
