using System.Text.Json;
using CodexMeter;

internal static class GoalChecks
{
    private static int checks;
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
    public static async Task<int> Run()
    {
        var oldBinary = Environment.GetEnvironmentVariable("CODEX_METER_CODEX_PATH");
        var root = Path.Combine(AppContext.BaseDirectory, "goal-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "trace.json");
        Environment.SetEnvironmentVariable("CODEX_METER_CODEX_PATH", Environment.ProcessPath);
        Environment.SetEnvironmentVariable("METER_GOAL_TRACE", tracePath);
        try
        {
            var rule = new PromptRule { Action = PromptAction.PauseRecentGoals, WorkingDirectory = root };
            Environment.SetEnvironmentVariable("METER_GOAL_TEST", "normal");
            var result = await new CodexPromptRunner().RunAsync(rule, CancellationToken.None);
            Check(result.Status == "Completed" && result.Output.Contains("1 goals paused"), "Pause reports confirmed updates");
            var trace = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(tracePath))!;
            var set = trace.Single(t => t.GetProperty("method").GetString() == "thread/goal/set").GetProperty("params");
            Check(set.GetProperty("threadId").GetString() == "active" && set.GetProperty("status").GetString() == "paused", "Only active goals are paused");
            Check(set.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "origin", "status", "threadId" }), "Status updates omit objective and budget");
            Check(set.GetProperty("origin").GetString() == "automatic", "Threshold actions use automatic lifecycle provenance");
            Check(!trace.Any(t => t.GetProperty("method").GetString() is "thread/start" or "thread/resume" or "turn/start"), "Goal control starts no model turn");
            var list = trace.Single(t => t.GetProperty("method").GetString() == "thread/list").GetProperty("params");
            Check(list.GetProperty("limit").GetInt32() == 20 && !list.GetProperty("archived").GetBoolean()
                && list.GetProperty("sortKey").GetString() == "updated_at", "Only the 20 recently active non-archived sessions are selected");
            Check(list.GetProperty("sourceKinds").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "cli", "vscode", "appServer" }), "Subagents and exec helpers do not displace desktop sessions");
            result = await new CodexPromptRunner().RunAsync(rule with { Action = PromptAction.ResumeRecentGoals }, CancellationToken.None);
            Check(result.Status == "Completed" && result.Output.Contains("1 goals resumed"), "Resume reports confirmed updates");
            trace = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(tracePath))!;
            set = trace.Single(t => t.GetProperty("method").GetString() == "thread/goal/set").GetProperty("params");
            Check(set.GetProperty("threadId").GetString() == "paused" && set.GetProperty("status").GetString() == "active", "Blocked and budget-limited goals remain untouched");
            var inspected = await new CodexGoalController().InspectAsync();
            Check(inspected.Checked == 7 && inspected.Changed == 0 && inspected.States["paused"] == 1, "Read-only inspection returns states without updates");
            Environment.SetEnvironmentVariable("METER_GOAL_TEST", "race");
            result = await new CodexPromptRunner().RunAsync(rule, CancellationToken.None);
            Check(result.Status == "Completed" && result.Output.Contains("0 goals paused"), "An intervening completion is respected");
            Environment.SetEnvironmentVariable("METER_GOAL_TEST", "error");
            result = await new CodexPromptRunner().RunAsync(rule, CancellationToken.None);
            Check(result.Status == "Failed" && result.Output.Contains("1 failed"), "API errors are explicit failures");
            Environment.SetEnvironmentVariable("METER_GOAL_TEST", "bad-budget");
            result = await new CodexPromptRunner().RunAsync(rule, CancellationToken.None);
            Check(result.Status == "Failed", "An update that changes the budget is not reported as verified");
            Check(PromptRule.Examples().Select(r => r.Action).SequenceEqual(new[] { PromptAction.ResumeRecentGoals, PromptAction.PauseRecentGoals }), "New goal examples use direct actions");
            Console.WriteLine($"PASS: {checks} focused goal checks (no real goals changed)"); return 0;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_METER_CODEX_PATH", oldBinary);
            Environment.SetEnvironmentVariable("METER_GOAL_TEST", null); Environment.SetEnvironmentVariable("METER_GOAL_TRACE", null);
        }
    }

    public static async Task<int> FakeServer()
    {
        var mode = Environment.GetEnvironmentVariable("METER_GOAL_TEST");
        var states = new Dictionary<string, string?> { ["active"] = "active", ["paused"] = "paused", ["blocked"] = "blocked",
            ["budget"] = "budgetLimited", ["usage"] = "usageLimited", ["done"] = "complete", ["none"] = null };
        var reads = new Dictionary<string, int>(); var trace = new List<JsonElement>();
        object? Goal(string id) => states[id] is { } status ? new { threadId = id, status, objective = "Keep the existing goal", tokenBudget = mode == "bad-budget" && status == "paused" && id == "active" ? 999 : 100,
            tokensUsed = 40, timeUsedSeconds = 10, createdAt = 1, updatedAt = 2 } : null;
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            using var doc = JsonDocument.Parse(line); var request = doc.RootElement;
            trace.Add(request.Clone());
            if (!request.TryGetProperty("id", out var requestId)) continue;
            var method = request.GetProperty("method").GetString(); var p = request.GetProperty("params"); object? result;
            switch (method)
            {
                case "initialize": result = new { }; break;
                case "thread/list": result = new { data = states.Keys.Select(id => new { id }).ToArray() }; break;
                case "thread/goal/get":
                    var readId = p.GetProperty("threadId").GetString()!;
                    reads[readId] = reads.GetValueOrDefault(readId) + 1;
                    if (mode == "race" && readId == "active" && reads[readId] == 2) states[readId] = "complete";
                    result = new { goal = Goal(readId) }; break;
                case "thread/goal/set":
                    if (mode == "error") { Console.WriteLine(JsonSerializer.Serialize(new { id = requestId.GetInt32(), error = new { code = -32000 } })); continue; }
                    var id = p.GetProperty("threadId").GetString()!; states[id] = p.GetProperty("status").GetString();
                    result = new { goal = Goal(id) }; break;
                default: throw new Exception("Unexpected goal request: " + method);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { id = requestId.GetInt32(), result }));
        }
        File.WriteAllText(Environment.GetEnvironmentVariable("METER_GOAL_TRACE")!, JsonSerializer.Serialize(trace));
        return 0;
    }
}
