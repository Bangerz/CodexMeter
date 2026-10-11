using System.Text.Json;
using CodexMeter;

internal static class PromptChecks
{
    private static int checks;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static UsageSnapshot Sample(double? remaining, int sequence, string plan = "pro") =>
        new(Now.AddSeconds(sequence), plan, [new("codex", "Codex", "primary", remaining is { } n ? 100 - n : null, 10080, Now.AddDays(5))]);

    public static async Task<int> Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "prompt-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rule = new PromptRule { Enabled = true, Prompt = "Quote \" and $()\nSecond line — 中文", WorkingDirectory = root };
        var doc = new PromptRulesDocument { Rules = [rule] };
        (doc, var hits) = PromptThresholds.Observe(doc, Sample(70, 1));
        Check(hits.Count == 0 && doc.States[rule.Id].OnTriggerSide == false, "First reading establishes an armed baseline");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(80, 2));
        Check(hits.Count == 1, "Upward equality triggers");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(98, 3)); Check(hits.Count == 0, "Staying above does not retrigger");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(80, 4)); Check(hits.Count == 0, "Equality does not rearm");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(79.9, 5)); Check(hits.Count == 0, "Crossing below rearms without running");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(80, 6)); Check(hits.Count == 1, "Recrossing runs once again");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(60, 5)); Check(doc.States[rule.Id].OnTriggerSide == true, "Out-of-order readings ignored");
        var store = new PromptRuleStore(Path.Combine(root, "rules.json")); store.Save(doc); doc = store.Load();
        (doc, hits) = PromptThresholds.Observe(doc, Sample(90, 7)); Check(hits.Count == 0, "Latch survives save/reload");
        var down = rule with { Id = Guid.NewGuid(), Direction = ThresholdDirection.FallsToOrBelow, Threshold = 20 };
        var both = new PromptRulesDocument { Rules = [rule, down] };
        (both, _) = PromptThresholds.Observe(both, Sample(50, 10));
        (both, hits) = PromptThresholds.Observe(both, Sample(20, 11)); Check(hits.Count == 1 && hits[0].Rule.Id == down.Id, "Downward equality triggers the correct rule");
        (both, hits) = PromptThresholds.Observe(both, Sample(10, 12)); Check(hits.Count == 0, "Staying below is latched");
        (both, hits) = PromptThresholds.Observe(both, Sample(98, 13)); Check(hits.Count == 1 && hits[0].Rule.Id == rule.Id && both.States[down.Id].OnTriggerSide == false, "Reset resumes and rearms the reserve rule");
        (both, hits) = PromptThresholds.Observe(both, Sample(19, 14)); Check(hits.Count == 1 && hits[0].Rule.Id == down.Id, "Next consumption cycle pauses once");
        (doc, _) = PromptThresholds.Observe(new() { Rules = [rule] }, Sample(50, 20));
        (doc, hits) = PromptThresholds.Observe(doc, Sample(null, 21)); Check(hits.Count == 0 && doc.States[rule.Id].ObservedAt == Sample(50, 20).FetchedAt, "Missing quota leaves baseline unchanged");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(110, 22)); Check(hits.Count == 0, "Invalid percentages cannot trigger");
        (doc, hits) = PromptThresholds.Observe(doc, Sample(95, 23, "plus")); Check(hits.Count == 0, "Known plan changes establish a new baseline");
        var extra = Sample(98, 24, "plus") with { Windows = [new("codex", "Codex", "primary", 2, 300, Now.AddHours(2))] };
        (doc, hits) = PromptThresholds.Observe(doc, extra); Check(hits.Count == 0, "Window changes establish a new baseline");
        Check(PromptThresholds.Reading(rule with { Scope = QuotaScope.CodexWeekly }, extra) is null, "Unavailable selected quota never falls back");
        var multi = Sample(99, 25) with { Windows = [new("codex", "Codex", "primary", 10, 10080, Now.AddDays(5)), new("other", "Other", "primary", 85, 300, Now.AddHours(1))] };
        Check(PromptThresholds.Reading(rule, multi)?.Remaining == 15 && PromptThresholds.Reading(rule with { Scope = QuotaScope.CodexWeekly }, multi)?.Remaining == 90, "Most-limited and weekly choices measure the selected scope");
        (doc, hits) = PromptThresholds.Observe(new() { Rules = [rule with { Enabled = false }] }, Sample(90, 30)); Check(hits.Count == 0 && doc.States.Count == 0, "Disabled rules never arm or run");
        (doc, hits) = PromptThresholds.Observe(new() { Rules = [rule] }, Sample(90, 31)); Check(hits.Count == 0 && doc.States[rule.Id].OnTriggerSide == true, "First reading on trigger side does not execute");
        Check(PromptRule.Examples().All(r => !r.Enabled), "Examples are opt-in");
        try { store.Save(new() { Rules = [rule, rule] }); throw new Exception("Duplicate rules accepted"); } catch (FormatException) { Check(true, "Duplicate IDs rejected"); }

        // Exercise real process plumbing against a fake native executable, not a model.
        var oldBinary = Environment.GetEnvironmentVariable("CODEX_METER_CODEX_PATH");
        var oldLog = Environment.GetEnvironmentVariable("METER_PROMPT_TEST_LOG");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_METER_CODEX_PATH", Environment.ProcessPath);
            Environment.SetEnvironmentVariable("METER_PROMPT_TEST_LOG", Path.Combine(root, "exec.json"));
            var info = CodexPromptRunner.StartInfo(rule);
            Check(!info.UseShellExecute && info.CreateNoWindow && !info.ArgumentList.Contains(rule.Prompt) && info.ArgumentList.Contains("--no-daemon"), "Prompt bypasses shell and command line and owns its helper");
            Check(info.ArgumentList.Contains("read-only") && info.ArgumentList.Contains("never") && !info.ArgumentList.Any(a => a.Contains("bypass")), "No approval or sandbox bypass flags");
            var runner = new CodexPromptRunner(); var result = await runner.RunAsync(rule, CancellationToken.None);
            Check(result.Status == "Finished" && result.Output.Contains("FAKE RESULT"), "Exec completion and final response captured");
            using (var log = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "exec.json"))))
                Check(log.RootElement.GetProperty("Prompt").GetString() == rule.Prompt, "Multiline Unicode prompt arrives unchanged on stdin");
            Environment.SetEnvironmentVariable("METER_PROMPT_TEST_MODE", "fail");
            result = await runner.RunAsync(rule, CancellationToken.None); Check(result.Status == "Failed (exit 7)", "Nonzero exit is visible");
            Environment.SetEnvironmentVariable("METER_PROMPT_TEST_MODE", "large");
            result = await runner.RunAsync(rule, CancellationToken.None);
            Check(result.Status == "Finished" && result.Output.Length < 16_100 && result.Output.EndsWith("[Response truncated by Meter]"), "Large output is drained and bounded without blocking");
            Environment.SetEnvironmentVariable("METER_PROMPT_TEST_MODE", "wait");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            result = await runner.RunAsync(rule, cancellation.Token); Check(result.Status == "Cancelled", "Cancellation stops the owned child");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_METER_CODEX_PATH", oldBinary); Environment.SetEnvironmentVariable("METER_PROMPT_TEST_LOG", oldLog);
            Environment.SetEnvironmentVariable("METER_PROMPT_TEST_MODE", null);
        }

        var calls = new List<Guid>(); var first = new TaskCompletionSource<PromptRunResult>();
        var serviceStore = new PromptRuleStore(Path.Combine(root, "service.json"));
        var service = new PromptAutomation(serviceStore, (r, _) => { calls.Add(r.Id); return calls.Count == 1 ? first.Task : Task.FromResult(new PromptRunResult("Finished", "ok")); });
        var second = rule with { Id = Guid.NewGuid(), Name = "Second" };
        service.Configure([rule, second]); service.Observe(Sample(70, 40)); service.Observe(Sample(90, 41));
        Check(calls.Count == 1 && serviceStore.Load().States[rule.Id].Status == "Running", "Crossing persisted and only one prompt runs at a time");
        service.Observe(Sample(60, 42)); first.SetResult(new("Finished", "ok"));
        for (var i = 0; service.IsRunning && i < 100; i++) await Task.Delay(10);
        Check(calls.Count == 1 && service.Document.States[second.Id].Status == "Skipped", "Queued prompt skipped when quota crossed back");
        service.Configure([rule with { Prompt = "Edited" }]); service.Observe(Sample(60, 43));
        Check(calls.Count == 1, "Editing only establishes a baseline");
        service.CancelRuns(shutdown: true); service.Observe(Sample(90, 44)); Check(calls.Count == 1, "Shutdown blocks future starts");
        serviceStore.Save(new() { Rules = [rule], States = new() { [rule.Id] = new() { Status = "Running", OnTriggerSide = true } } });
        var recovered = new PromptAutomation(serviceStore, (_, _) => throw new Exception("Must not replay"));
        Check(recovered.Document.States[rule.Id].Status == "Interrupted", "Interrupted runs never auto-replay");
        var waiting = new TaskCompletionSource<PromptRunResult>(); var afterFailureCalls = 0;
        var afterFailure = new PromptAutomation(new PromptRuleStore(Path.Combine(root, "failed-poll.json")), (_, _) => { afterFailureCalls++; return waiting.Task; });
        afterFailure.Configure([rule, second]); afterFailure.Observe(Sample(60, 45)); afterFailure.Observe(Sample(90, 46)); afterFailure.PollFailed();
        waiting.SetResult(new("Finished", "ok"));
        for (var i = 0; afterFailure.IsRunning && i < 100; i++) await Task.Delay(10);
        Check(afterFailureCalls == 1 && afterFailure.Document.States[second.Id].Status == "Skipped", "A failed poll invalidates quota for queued work");
        var blockedPath = Path.Combine(root, "blocked.json"); var blockedStore = new PromptRuleStore(blockedPath);
        var blocked = new PromptAutomation(blockedStore, (_, _) => throw new Exception("Must not launch")); blocked.Configure([rule]); blocked.Observe(Sample(70, 50));
        Directory.CreateDirectory(blockedPath + ".tmp"); blocked.Observe(Sample(90, 51));
        Check(blocked.Error is not null && blocked.Document.States[rule.Id].OnTriggerSide == false, "Persistence failure prevents dispatch and retains the prior latch");
        Console.WriteLine($"PASS: {checks} focused prompt checks (no model calls)");
        return 0;
    }

    public static async Task<int> FakeCodex(string[] args)
    {
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        var prompt = await Console.In.ReadToEndAsync();
        File.WriteAllText(Environment.GetEnvironmentVariable("METER_PROMPT_TEST_LOG")!, JsonSerializer.Serialize(new { Args = args, Prompt = prompt }));
        if (Environment.GetEnvironmentVariable("METER_PROMPT_TEST_MODE") == "wait") await Task.Delay(TimeSpan.FromMinutes(2));
        if (Environment.GetEnvironmentVariable("METER_PROMPT_TEST_MODE") == "large")
        {
            Console.Error.Write(new string('e', 100_000));
            Console.Write(new string('a', 20_000));
        }
        Console.WriteLine("FAKE RESULT");
        return Environment.GetEnvironmentVariable("METER_PROMPT_TEST_MODE") == "fail" ? 7 : 0;
    }
}
