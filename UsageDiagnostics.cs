using System.Text.Json;

namespace CodexMeter;

public sealed class UsageDiagnostics(string path)
{
    private const long MaxBytes = 256 * 1024;
    private readonly object gate = new();

    public bool Write(string kind, object? detail = null)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Kind = kind, Detail = detail })
                    + Environment.NewLine);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or JsonException or NotSupportedException)
        {
            // Diagnostic storage must never stop polling or notification delivery.
            return false;
        }
    }
}
