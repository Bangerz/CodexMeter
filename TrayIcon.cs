using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexMeter;

internal static class TrayIcon
{
    public static readonly Color Blue = Color.FromArgb(105, 172, 255);
    public static readonly Color Green = Color.FromArgb(89, 205, 164);
    public static readonly Color Amber = Color.FromArgb(247, 183, 83);
    public static readonly Color Red = Color.FromArgb(247, 112, 116);
    public static Color ColorFor(UsageWindow? window, DateTimeOffset now, bool lightBackground = false) =>
        (window?.PaceAt(now) ?? UsagePace.Unknown, lightBackground) switch
        {
            (UsagePace.Ahead, false) => Color.FromArgb(68, 220, 235),
            (UsagePace.OnTrack, false) => Green,
            (UsagePace.Behind, false) => Color.FromArgb(245, 215, 74),
            (UsagePace.FarBehind, false) => Color.FromArgb(255, 153, 64),
            (UsagePace.Critical, false) => Red,
            (UsagePace.Ahead, true) => Color.FromArgb(0, 111, 126),
            (UsagePace.OnTrack, true) => Color.FromArgb(0, 117, 66),
            (UsagePace.Behind, true) => Color.FromArgb(135, 105, 0),
            (UsagePace.FarBehind, true) => Color.FromArgb(173, 75, 0),
            (UsagePace.Critical, true) => Color.FromArgb(189, 38, 48),
            (_, true) => Color.FromArgb(65, 73, 85),
            _ => Color.FromArgb(164, 173, 188)
        };

    public static Icon Create(UsageSnapshot? snapshot, bool stale)
    {
        using var bitmap = Draw(snapshot, stale, 32);
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }

    public static Bitmap Draw(UsageSnapshot? snapshot, bool stale, int size, DateTimeOffset? at = null, bool? lightBackground = null)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(size / 32f, size / 32f);
        var window = snapshot?.MostLimited;
        // A stale number must not masquerade as current usage.
        var label = stale ? "!" : window?.Remaining is { } remaining ? $"{Math.Floor(remaining):0}" : "–";
        var color = ColorFor(window, at ?? DateTimeOffset.UtcNow, lightBackground ?? IsLightTaskbar());
        using var brush = new SolidBrush(stale ? Amber : color);
        // Fit the actual glyph outlines instead of relying on DrawString layout,
        // which can wrap or clip digits in a tiny DPI-scaled icon.
        using var family = new FontFamily("Segoe UI");
        using var glyphs = new GraphicsPath();
        glyphs.AddString(label, family, (int)FontStyle.Bold, 24, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = glyphs.GetBounds();
        var fit = Math.Min(28f / bounds.Width, 24f / bounds.Height);
        using var transform = new Matrix(fit, 0, 0, fit,
            (32 - bounds.Width * fit) / 2 - bounds.X * fit,
            1 + (24 - bounds.Height * fit) / 2 - bounds.Y * fit);
        glyphs.Transform(transform);
        graphics.FillPath(brush, glyphs);
        using var rail = new Pen(Color.FromArgb(110, 130, 145), 3);
        graphics.DrawLine(rail, 4, 30, 28, 30);
        if (!stale && window?.Remaining is { } left && left > 0)
        {
            using var fill = new Pen(color, 3);
            graphics.DrawLine(fill, 4, 30, 4 + 24 * (float)(left / 100), 30);
        }
        return bitmap;
    }

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch (System.Security.SecurityException) { return false; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
