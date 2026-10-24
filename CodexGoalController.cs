using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexMeter;

// Goal control runs in Meter's own helper, outside the prompt's shell sandbox.
// It uses only supported thread APIs, never edits session files or databases.
public sealed class CodexGoalController
{
    public async Task<PromptRunResult> RunAsync(PromptRule rule, CancellationToken token)
    {
        rule.Validate();
        if (rule.Action == PromptAction.CodexPrompt) throw new ArgumentException("Select a goal action.");
        var result = await InspectOrChangeAsync(rule.Action, rule.RecentSessionLimit, token);
        return new(result.Failed == 0 ? "Completed" : result.Changed > 0 ? "Partially completed" : "Failed",
            $"Checked {result.Checked} recent non-archived sessions. {result.Changed} goals {result.Verb}. "
            + $"{result.Unchanged} unchanged or without a matching goal. {result.Failed} failed."
            + (result.Error is null ? "" : "\n" + result.Error)
            + "\nOnly active goals are paused; only paused goals are resumed. Objectives, budgets and usage history are preserved.");
    }

    public Task<GoalActionSummary> InspectAsync(CancellationToken token = default, int recentSessionLimit = 20) =>
        InspectOrChangeAsync(null, recentSessionLimit, token);

    private static async Task<GoalActionSummary> InspectOrChangeAsync(PromptAction? action, int recentSessionLimit, CancellationToken token)
    {
        if (recentSessionLimit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(recentSessionLimit));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var count = 0; var changed = 0; var unchanged = 0; var failed = 0;
        var states = new Dictionary<string, int>();
        var verb = action == PromptAction.ResumeRecentGoals ? "resumed" : "paused";
        try
        {
            await using var rpc = new GoalRpc();
            await rpc.StartAsync(deadline.Token);
            // Finish discovery before any mutations: the server may cap page
            // sizes, and updating goals must not reorder an unfinished scan.
            var ids = new List<string>();
            var seen = new HashSet<string>();
            var cursors = new HashSet<string>();
            string? cursor = null;
            do
            {
                var list = await rpc.CallAsync("thread/list", new { limit = recentSessionLimit - ids.Count, cursor,
                    sortKey = "updated_at", sortDirection = "desc", sourceKinds = new[] { "cli", "vscode", "appServer" },
                    archived = false, useStateDbOnly = true }, deadline.Token);
                if (!list.TryGetProperty("data", out var threads) || threads.ValueKind != JsonValueKind.Array)
                    throw new GoalApiException("Codex returned no session list.");
                foreach (var thread in threads.EnumerateArray())
                {
                    var id = thread.GetProperty("id").GetString() ?? throw new GoalApiException("A session has no ID.");
                    if (seen.Add(id)) ids.Add(id);
                    if (ids.Count == recentSessionLimit) break;
                }
                cursor = list.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
                if (ids.Count >= recentSessionLimit || string.IsNullOrEmpty(cursor)) break;
                if (!cursors.Add(cursor)) throw new GoalApiException("Codex repeated a session-list cursor; scan incomplete.");
            } while (true);
            if (ids.Count == 0)
                throw new GoalApiException("No recent sessions found. Check that Meter is running with your usual Codex profile.");
            foreach (var id in ids)
            {
                deadline.Token.ThrowIfCancellationRequested();
                count++;
                try
                {
                    var result = await rpc.CallAsync("thread/goal/get", new { threadId = id }, deadline.Token);
                    var goal = result.GetProperty("goal");
                    var status = goal.ValueKind == JsonValueKind.Object ? goal.GetProperty("status").GetString() ?? "unknown" : "none";
                    states[status] = states.GetValueOrDefault(status) + 1;
                    var from = action == PromptAction.ResumeRecentGoals ? "paused" : "active";
                    var to = action == PromptAction.ResumeRecentGoals ? "active" : "paused";
                    if (action is null || status != from) { unchanged++; continue; }
                    // A second read avoids knowingly overriding an intervening
                    // completion or pause. No objective/budget is sent to set.
                    var fresh = await rpc.CallAsync("thread/goal/get", new { threadId = id }, deadline.Token);
                    if (fresh.GetProperty("goal").ValueKind != JsonValueKind.Object
                        || fresh.GetProperty("goal").GetProperty("status").GetString() != from) { unchanged++; continue; }
                    // Dispatch is an automatic lifecycle action authorized by a
                    // saved threshold rule. Manual "user" mutations append an
                    // instruction to the session's log, owned by the desktop.
                    var updated = await rpc.CallAsync("thread/goal/set", new { threadId = id, status = to, origin = "automatic" }, deadline.Token);
                    var confirmed = await rpc.CallAsync("thread/goal/get", new { threadId = id }, deadline.Token);
                    if (!PreservedAndChanged(fresh.GetProperty("goal"), updated.GetProperty("goal"), to)
                        || confirmed.GetProperty("goal").GetProperty("status").GetString() != to)
                        throw new GoalApiException("A goal update could not be verified.");
                    changed++;
                }
                catch (Exception ex) when (ex is GoalApiException or JsonException or KeyNotFoundException or InvalidOperationException)
                { failed++; }
            }
            return new(count, changed, unchanged, failed, verb, states,
                failed > 0 ? "Some goal requests failed or could not be verified. Review the sessions before retrying." : null);
        }
        catch (OperationCanceledException)
        { return new(count, changed, unchanged, Math.Max(1, failed), verb, states, token.IsCancellationRequested ? "Goal action cancelled; completed updates remain applied." : "Goal API timed out; completed updates remain applied."); }
        catch (Exception ex) when (ex is GoalApiException or Win32Exception or UsageException or IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
        { return new(count, changed, unchanged, Math.Max(1, failed), verb, states, ex is GoalApiException ? ex.Message : "Could not access Codex goal controls. Check its installation and session-state access."); }
    }

    public static bool PreservedAndChanged(JsonElement before, JsonElement after, string target) =>
        after.ValueKind == JsonValueKind.Object && after.GetProperty("status").GetString() == target
        && before.GetProperty("objective").GetString() == after.GetProperty("objective").GetString()
        && Budget(before) == Budget(after)
        && after.GetProperty("tokensUsed").GetInt64() >= before.GetProperty("tokensUsed").GetInt64()
        && after.GetProperty("timeUsedSeconds").GetInt64() >= before.GetProperty("timeUsedSeconds").GetInt64();

    private static long? Budget(JsonElement goal) => goal.TryGetProperty("tokenBudget", out var budget) && budget.ValueKind == JsonValueKind.Number ? budget.GetInt64() : null;
}

public sealed record GoalActionSummary(int Checked, int Changed, int Unchanged, int Failed, string Verb, Dictionary<string, int> States, string? Error);
internal sealed class GoalApiException(string message) : Exception(message);

internal sealed class GoalRpc : IAsyncDisposable
{
    private readonly Process process = new() { StartInfo = CreateStartInfo() };
    private int nextId;
    private CancellationTokenRegistration cancellation;
    private Task? drain;

    private static ProcessStartInfo CreateStartInfo()
    {
        var info = new ProcessStartInfo(CodexClient.FindExecutable()) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var arg in new[] { "app-server", "--listen", "stdio://", "-c", "analytics.enabled=false" }) info.ArgumentList.Add(arg);
        return info;
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (!process.Start()) throw new GoalApiException("Could not start the goal API helper.");
        cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { } });
        drain = DrainAsync(process.StandardError);
        process.StandardInput.AutoFlush = true;
        await CallAsync("initialize", new { clientInfo = new { name = "codex_meter_goals", version = "0.1.0" }, capabilities = new { experimentalApi = true } }, token);
        await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(), token);
    }

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        var id = ++nextId;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token) ?? throw new GoalApiException("Codex goal connection closed.");
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
                || !responseId.TryGetInt32(out var value) || value != id) continue;
            if (root.TryGetProperty("error", out var error))
                throw new GoalApiException($"Codex rejected {method} (code {error.GetProperty("code").GetInt32()}).");
            return root.GetProperty("result").Clone();
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Dispose();
        try { process.StandardInput.Close(); } catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception kill) when (kill is Win32Exception or InvalidOperationException) { } }
        if (drain is not null) try { await drain.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        process.Dispose();
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
}
