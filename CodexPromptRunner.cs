using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace CodexMeter;

public sealed record PromptRunResult(string Status, string Output);

public sealed class CodexPromptRunner
{
    public static ProcessStartInfo StartInfo(PromptRule rule)
    {
        rule.Validate();
        var info = new ProcessStartInfo(CodexClient.FindExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = rule.WorkingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "--no-daemon", "--ask-for-approval", "never", "exec", "--sandbox",
            rule.Permissions == PromptPermissions.WorkspaceWrite ? "workspace-write" : "read-only",
            "--cd", rule.WorkingDirectory, "--skip-git-repo-check", "--color", "never", "-c", "analytics.enabled=false", "-" })
            info.ArgumentList.Add(arg);
        return info;
    }

    public async Task<PromptRunResult> RunAsync(PromptRule rule, CancellationToken cancellationToken)
    {
        if (rule.Action != PromptAction.CodexPrompt)
            return await new CodexGoalController().RunAsync(rule, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(rule.TimeoutMinutes));
        Process? process = null;
        Task<string>? output = null;
        Task<string>? errors = null;
        try
        {
            process = new() { StartInfo = StartInfo(rule) };
            if (!process.Start()) return new("Failed to start", "Codex did not start.");
            using var cancellation = deadline.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            });
            output = DrainAsync(process.StandardOutput, 16_000);
            // Codex's progress/error stream can include credentials or tool data.
            // Drain it to prevent deadlocks but do not store it in Meter.
            errors = DrainAsync(process.StandardError, 0);
            await process.StandardInput.WriteAsync(rule.Prompt.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            var text = await output.WaitAsync(deadline.Token);
            await errors.WaitAsync(deadline.Token);
            return new(process.ExitCode == 0 ? "Response received — review result" : $"Failed (exit {process.ExitCode})",
                string.IsNullOrWhiteSpace(text) ? "No final response. Check Codex sign-in, permissions, and the saved CLI session." : text);
        }
        catch (OperationCanceledException)
        { return new(cancellationToken.IsCancellationRequested ? "Cancelled" : "Timed out", "No automatic retry. The rule must cross back before it can trigger again."); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UsageException or FormatException or UnauthorizedAccessException)
        { return new("Failed to run", "Could not run Codex. Check its installation, sign-in, working folder, and permissions."); }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                // Only the owned helper is stopped; never attach to or kill desktop sessions.
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
                process.Dispose();
            }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, int limit)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        var truncated = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
            {
                var keep = Math.Min(count, Math.Max(0, limit - result.Length));
                result.Append(buffer, 0, keep);
                if (keep < count) truncated = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        if (truncated && limit > 0) result.Append("\n[Response truncated by Meter]");
        return result.ToString();
    }
}
