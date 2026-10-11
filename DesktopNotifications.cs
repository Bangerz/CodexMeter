using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace CodexMeter;

public sealed class DesktopNotifications(UsageDiagnostics diagnostics) : IDisposable
{
    public const string AppId = "Bangerz.CodexMeter";
    private const string ActivatorId = "{31D28E52-1CBB-4B37-A288-9A0AEEA5DE40}";
    private readonly Dictionary<string, ToastNotification> active = [];
    private readonly ToastClassFactory factory = new();
    private uint registration;

    public bool RegisterSender()
    {
        try
        {
            Register();
            if (registration == 0)
            {
                var classId = typeof(ToastActivation).GUID;
                Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref classId, factory, 4, 1, out registration));
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            diagnostics.Write("toast_registration_failed", new { AppId, HResult = ex.HResult });
            return false;
        }
    }

    public static void Register()
    {
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
        using (var identity = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
        {
            identity.SetValue("DisplayName", "Codex Meter");
            identity.SetValue("CustomActivator", ActivatorId);
        }
        using var command = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + ActivatorId + @"\LocalServer32");
        command.SetValue("", $"\"{Environment.ProcessPath}\" --show");
    }

    public static string Content(string title, string text) =>
        new XElement("toast", new XAttribute("activationType", "foreground"), new XAttribute("launch", "show"),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", title), new XElement("text", text)))).ToString(SaveOptions.DisableFormatting);

    public bool Show(string kind, string title, string text)
    {
        var stage = "register";
        try
        {
            if (!RegisterSender()) return false;
            stage = "create_notifier";
            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            stage = "read_setting";
            var setting = ReadSetting(notifier);
            diagnostics.Write("toast_requested", new { AppId, Type = kind, Setting = setting?.ToString() ?? "NotInitialized", Title = title, Text = text });
            if (setting.HasValue && setting != NotificationSetting.Enabled) return false;
            stage = "create_toast";
            var xml = new XmlDocument();
            xml.LoadXml(Content(title, text));
            var toast = new ToastNotification(xml)
            {
                Tag = kind == "test" ? "test" : "reset", Group = "usage",
                ExpirationTime = DateTimeOffset.UtcNow.AddDays(1)
            };
            toast.Failed += (_, args) => diagnostics.Write("toast_failed", new { Type = kind, HResult = args.ErrorCode.HResult });
            toast.Dismissed += (_, args) => diagnostics.Write("toast_dismissed", new { Type = kind, Reason = args.Reason.ToString() });
            active[toast.Tag] = toast;
            stage = "submit";
            notifier.Show(toast);
            diagnostics.Write("toast_submitted", new { AppId, Type = kind, toast.Tag });
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            diagnostics.Write("toast_send_failed", new { Type = kind, Stage = stage, HResult = ex.HResult });
            return false;
        }
    }

    private static NotificationSetting? ReadSetting(ToastNotifier notifier)
    {
        try { return notifier.Setting; }
        // Windows doesn't expose settings for a new unpackaged sender until its
        // first toast. A preflight setting read must not block that first toast.
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { return null; }
    }

    public static object Status()
    {
        var setting = ReadSetting(ToastNotificationManager.CreateToastNotifier(AppId));
        IReadOnlyList<ToastNotification> history;
        try { history = ToastNotificationManager.History.GetHistory(AppId); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490)) { history = []; }
        return new
        {
            AppId, Setting = setting?.ToString() ?? "NotInitialized",
            Notifications = history.Select(toast => new { toast.Tag, toast.Group, Xml = toast.Content.GetXml() }).ToArray()
        };
    }

    public void Dispose()
    {
        if (registration != 0) { CoRevokeClassObject(registration); registration = 0; }
        active.Clear();
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid classId, [MarshalAs(UnmanagedType.Interface)] IToastClassFactory factory,
        uint context, uint flags, out uint registration);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint registration);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
