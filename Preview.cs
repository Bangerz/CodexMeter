using System.Text.Json;

namespace CodexMeter;

internal static class Preview
{
    public static void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var samples = new Dictionary<string, UsageSnapshot>
        {
            ["weekly"] = new(now, "pro", [new("codex", "Codex", "primary", 55, 10080, now.AddHours(23))]),
            ["two-windows"] = new(now, "plus", [new("codex", "Codex", "primary", 27, 300, now.AddHours(2)), new("codex", "Codex", "secondary", 81, 10080, now.AddDays(3))]),
            ["unavailable"] = new(now, null, [new("codex", "Codex", "primary", null, null, null)])
        };
        foreach (var (name, snapshot) in samples)
        {
            using var popup = new UsagePopup { KeepOpen = true };
            _ = popup.Handle;
            popup.UpdateUsage(snapshot, null, false, false);
            popup.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(popup.Width, popup.Height);
            popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, name + ".png"));
            popup.Hide();
        }
        using var icon = TrayIcon.Draw(samples["weekly"], false, 32);
        icon.Save(Path.Combine(directory, "tray-icon.png"));
    }
}
