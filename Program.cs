using System.Text.Json;

namespace CodexMeter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--goal-status")
        {
            var result = new CodexGoalController().InspectAsync().GetAwaiter().GetResult();
            File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(result));
            return result.Failed == 0 ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--check")
        {
            try
            {
                var snapshot = new CodexClient().FetchAsync().GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(new { error = ex is UsageException ? ex.Message : "Usage check failed." }));
                return 1;
            }
        }
        if (args.Length == 2 && args[0] == "--preview")
        {
            Preview.Save(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--preview-prompt-rules")
        {
            Preview.SavePromptRules(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--notification-status")
        {
            try
            {
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(DesktopNotifications.Status()));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(new { Error = "Could not read notification status.", ex.HResult }));
                return 1;
            }
        }
        using var singleton = new Mutex(true, @"Local\CodexMeter.Tray.v1", out var created);
        if (!created)
        {
            if (args.Contains("--show") && EventWaitHandle.TryOpenExisting(TrayApp.ShowEventName, out var show))
                using (show) show.Set();
            return 0;
        }
        Application.Run(new TrayApp(args.Contains("--test-notification"), args.Contains("--show")));
        return 0;
    }
}
