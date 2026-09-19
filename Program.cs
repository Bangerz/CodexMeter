using System.Text.Json;

namespace CodexMeter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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
        using var singleton = new Mutex(true, @"Local\CodexMeter.Tray.v1", out var created);
        if (!created) return 0;
        Application.Run(new TrayApp());
        return 0;
    }
}
