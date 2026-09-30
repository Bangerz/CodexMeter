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
            ["weekly-time-greater"] = new(now, "pro", [new("codex", "Codex", "primary", 69, 10080, now.AddHours(84).AddSeconds(30))]),
            ["weekly-quota-greater"] = new(now, "pro", [new("codex", "Codex", "primary", 20, 10080, now.AddHours(42).AddSeconds(30))]),
            ["weekly-equal"] = new(now, "pro", [new("codex", "Codex", "primary", 50, 10080, now.AddHours(84).AddSeconds(30))]),
            ["weekly-time-unavailable"] = new(now, "pro", [new("codex", "Codex", "primary", 69, 10080, null)]),
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
        var today = DateOnly.FromDateTime(DateTime.Now);
        var costs = new CostHistory(now,
            [new(today, 3.27, 128700, false), new(today.AddDays(-1), 12.64, 408300, false), new(today.AddDays(-8), 7.83, 271800, false)],
            [new("one", "Implement optional cost history", now, 3.27, 128700, false),
             new("two", "Weekly usage collector", now.AddHours(-3), 12.64, 408300, false),
             new("three", "Fix menu focus", now.AddHours(-6), 2.01, 77100, false),
             new("four", "Research a very long session display name that should be clipped safely inside the hover panel", now.AddDays(-1), 4.89, 124800, false),
             new("five", "Quick question", now.AddDays(-2), 0.08, 2900, false)]);
        using (var hover = new CostHoverPopup())
        {
            _ = hover.Handle;
            hover.UpdateHistory(costs, null, samples["two-windows"], false);
            hover.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(hover.Width, hover.Height);
            hover.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, "cost-hover.png"));
            hover.Hide();
        }
        using var icon = TrayIcon.Draw(samples["weekly"], false, 32);
        icon.Save(Path.Combine(directory, "tray-icon.png"));
        using var sheet = new Bitmap(320, 200);
        using var canvas = Graphics.FromImage(sheet);
        canvas.Clear(Color.FromArgb(25, 28, 34));
        using var caption = new Font("Segoe UI", 10, GraphicsUnit.Pixel);
        var row = 0;
        foreach (var remaining in new[] { 4, 40, 44, 100 })
        {
            var sample = new UsageSnapshot(now, "pro",
                [new("codex", "Codex", "primary", 100 - remaining, 10080, now.AddDays(1))]);
            canvas.DrawString(remaining.ToString(), caption, Brushes.White, 2, row * 48 + 20);
            var column = 0;
            foreach (var size in new[] { 16, 20, 24, 32 })
            {
                using var rendered = TrayIcon.Draw(sample, false, size);
                canvas.DrawString(size + "px", caption, Brushes.LightGray, 45 + column * 65, row * 48);
                canvas.DrawImageUnscaled(rendered, 45 + column * 65, row * 48 + 15);
                column++;
            }
            row++;
        }
        sheet.Save(Path.Combine(directory, "icon-sizes.png"));
    }
}
