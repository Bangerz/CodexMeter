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
    public static Color ColorFor(double? remaining, int? duration = null) => remaining switch
    {
        <= 10 => Red, <= 25 => Amber, _ => duration == 300 ? Green : Blue
    };

    public static Icon Create(UsageSnapshot? snapshot, bool stale)
    {
        using var bitmap = Draw(snapshot, stale, 32);
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }

    public static Bitmap Draw(UsageSnapshot? snapshot, bool stale, int size)
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
        using var font = new Font("Segoe UI", label.Length >= 3 ? 16 : 22, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(stale ? Amber : IsLightTaskbar() ? Color.FromArgb(25, 31, 42) : Color.White);
        using var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(label, font, brush, new RectangleF(-2, -3, 36, 32), format);
        using var rail = new Pen(Color.FromArgb(110, 130, 145), 3);
        graphics.DrawLine(rail, 4, 30, 28, 30);
        if (!stale && window?.Remaining is { } left && left > 0)
        {
            using var fill = new Pen(ColorFor(left, window.DurationMinutes), 3);
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
