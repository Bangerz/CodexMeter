using System.Text.Json;

namespace CodexMeter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--desktop-chats")
        {
            try
            {
                var chats = new CodexDesktopChatClient().ListAsync().GetAwaiter().GetResult();
                File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(chats));
                return 0;
            }
            catch (Exception)
            { File.WriteAllText(Path.GetFullPath(args[1]), "{\"error\":\"Desktop chat bridge unavailable. Open Codex and retry.\"}"); return 1; }
        }
        if (args.Length == 3 && args[0] == "--test-desktop-chat")
        {
            if (!Guid.TryParse(args[1], out var testThreadId) || testThreadId == Guid.Empty) return 2;
            var rule = new PromptRule { Action = PromptAction.SendToDesktopChat, DesktopThreadId = args[1],
                Prompt = "Codex Meter delivery test. This is the harmless test authorized by the user. Briefly acknowledge receipt and whether desktop chat tools (such as list_threads and send_message_to_thread) are available. Do not invoke them for this test. Continue any current work; do not change goals or files because of this message." };
            var result = new CodexDesktopChatClient().SendAsync(rule, CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText(Path.GetFullPath(args[2]), JsonSerializer.Serialize(result));
            return result.Status == "Submitted to desktop" ? 0 : 1;
        }
        if (args.Length is 2 or 3 && args[0] == "--goal-status")
        {
            var limit = 20;
            if (args.Length == 3 && (!int.TryParse(args[2], out limit) || limit is < 1 or > 100)) return 2;
            var result = new CodexGoalController().InspectAsync(recentSessionLimit: limit).GetAwaiter().GetResult();
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
