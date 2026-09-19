using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexMeter;

public sealed class UsageException(string message) : Exception(message);

public sealed class CodexClient
{
    // Deliberately no thread/start, turn/start, login, logout, or reset operation.
    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var start = CreateStartInfo();
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new UsageException("Could not start Codex. Open Codex and try Refresh.");
        }
        catch (Win32Exception)
        {
            throw new UsageException("Codex could not start. Open Codex or install the Codex CLI, then refresh.");
        }
        // Drain without storing stderr; logs can contain account or environment details.
        var drain = DrainAsync(process.StandardError);
        using var cancelHelper = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        });
        try
        {
            var input = process.StandardInput;
            input.AutoFlush = true;
            await SendAsync(input, new { id = 1, method = "initialize", @params = new {
                clientInfo = new { name = "codex_meter", title = "Codex Meter", version = "0.1.0" }
            } }, deadline.Token);
            await ReadResultAsync(process.StandardOutput, 1, deadline.Token);
            await SendAsync(input, new { method = "initialized", @params = new { } }, deadline.Token);
            await SendAsync(input, new { id = 2, method = "account/rateLimits/read" }, deadline.Token);
            var result = await ReadResultAsync(process.StandardOutput, 2, deadline.Token);
            return UsageSnapshot.Parse(result, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsageException("Usage check timed out. Check your connection and try Refresh.");
        }
        catch (JsonException)
        {
            throw new UsageException("Codex returned an unreadable response. Update Codex and try again.");
        }
        catch (IOException)
        {
            throw new UsageException("The Codex connection closed. Open Codex and try Refresh.");
        }
        finally
        {
            // Close only the helper this poll started, never the user's existing Codex process.
            try { process.StandardInput.Close(); } catch (IOException) { }
            try
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            try { await drain.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        }
    }

    private static async Task SendAsync(StreamWriter stream, object message, CancellationToken token) =>
        await stream.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);

    private static async Task<JsonElement> ReadResultAsync(StreamReader reader, int id, CancellationToken token)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null) throw new UsageException("Codex closed before returning usage. Open Codex and refresh.");
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id)
                continue;
            if (message.TryGetProperty("error", out var error))
            {
                var detail = error.TryGetProperty("message", out var text) ? text.GetString() ?? "" : "";
                if (detail.Contains("auth", StringComparison.OrdinalIgnoreCase) || detail.Contains("login", StringComparison.OrdinalIgnoreCase)
                    || detail.Contains("sign", StringComparison.OrdinalIgnoreCase) || detail.Contains("401"))
                    throw new UsageException("Sign in to Codex with your ChatGPT account, then refresh.");
                throw new UsageException("Usage is unavailable. Check your connection and Codex sign-in, then refresh.");
            }
            if (!message.TryGetProperty("result", out var result)) throw new UsageException("Codex returned no usage response.");
            return result.Clone();
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var explicitPath = Environment.GetEnvironmentVariable("CODEX_METER_CODEX_PATH");
        string? binary = null;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!Path.IsPathFullyQualified(explicitPath) || !File.Exists(explicitPath)
                || !string.Equals(Path.GetExtension(explicitPath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new UsageException("CODEX_METER_CODEX_PATH must point to an existing codex.exe.");
            binary = explicitPath;
        }
        // Prefer the same executable as the running desktop app, without inspecting its arguments or credentials.
        if (binary is null)
            foreach (var running in Process.GetProcessesByName("codex"))
            {
                using (running)
                {
                    try { binary ??= running.MainModule?.FileName; }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
        // The desktop app keeps versioned binaries here, including while the app is closed.
        if (binary is null)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(root)) binary = Directory.EnumerateDirectories(root)
                    .Select(d => Path.Combine(d, "codex.exe")).Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        if (binary is null)
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                if (File.Exists(candidate)) { binary = candidate; break; }
                // npm's native optional dependency; do not run a command through a shell.
                foreach (var architecture in new[] { "x64", "arm64" })
                {
                    var target = architecture == "x64" ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc";
                    candidate = Path.Combine(directory.Trim('"'), "node_modules", "@openai", "codex", "node_modules",
                        "@openai", $"codex-win32-{architecture}", "vendor", target, "codex", "codex.exe");
                    if (File.Exists(candidate)) { binary = candidate; break; }
                }
                if (binary is not null) break;
            }
        }
        if (binary is null) throw new UsageException("Open the Codex desktop app, then click Refresh.");
        var info = new ProcessStartInfo(binary)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var arg in new[] { "app-server", "--listen", "stdio://", "-c", "analytics.enabled=false" })
            info.ArgumentList.Add(arg);
        return info;
    }
}
