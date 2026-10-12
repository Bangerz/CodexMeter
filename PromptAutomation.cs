namespace CodexMeter;

// All mutations run on the tray's UI synchronization context. Polling continues
// while a prompt runs; the queue starts at most one Codex helper at a time.
public sealed class PromptAutomation
{
    private readonly PromptRuleStore store;
    private readonly Func<PromptRule, CancellationToken, Task<PromptRunResult>> run;
    private readonly Queue<PromptTrigger> queue = new();
    private CancellationTokenSource? running;
    private bool draining;
    private bool stopped;
    private UsageSnapshot? latest;
    public PromptRulesDocument Document { get; private set; } = new();
    public string? Error { get; private set; }
    public bool IsRunning => draining;
    public event Action<string, string>? Notice;

    public PromptAutomation(PromptRuleStore store, Func<PromptRule, CancellationToken, Task<PromptRunResult>> run)
    {
        this.store = store;
        this.run = run;
        try
        {
            Document = store.Load();
            if (Document.States.Values.Any(s => s.Status is "Queued" or "Running"))
            {
                Save(Document with { States = Document.States.ToDictionary(p => p.Key, p => p.Value.Status is "Queued" or "Running"
                    ? p.Value with { Status = "Interrupted", LastOutput = "Meter stopped during this attempt. It will not be replayed automatically." } : p.Value) });
            }
        }
        catch (Exception ex) when (StorageError(ex)) { Error = "Prompt rules could not be loaded. Check data/prompt-rules.json. Quota polling continues."; }
    }

    public void Configure(List<PromptRule> rules)
    {
        var states = new Dictionary<Guid, PromptRuleState>();
        foreach (var rule in rules)
        {
            var old = Document.Rules.FirstOrDefault(r => r.Id == rule.Id);
            var state = Document.States.GetValueOrDefault(rule.Id) ?? new();
            if (old is null || !old.SameTriggerAndAction(rule))
            {
                state = state with { OnTriggerSide = null, BaselineKey = null, ObservedAt = null, RunId = null, Status = "Waiting for first reading" };
                if (rule.Enabled && latest is not null && DateTimeOffset.UtcNow - latest.FetchedAt <= TimeSpan.FromMinutes(11)
                    && PromptThresholds.Reading(rule, latest) is { } reading)
                    state = state with { OnTriggerSide = rule.Matches(reading.Remaining), BaselineKey = reading.Key, ObservedAt = latest.FetchedAt };
            }
            states[rule.Id] = state;
        }
        // Let the editor show a useful validation/save error and keep its edits.
        var updated = new PromptRulesDocument { Rules = rules.ToList(), States = states };
        store.Save(updated);
        Document = updated;
        Error = null;
    }

    public void Observe(UsageSnapshot snapshot)
    {
        latest = snapshot;
        if (stopped || Error is not null || !Document.Rules.Any(r => r.Enabled)) return;
        var (updated, triggers) = PromptThresholds.Observe(Document, snapshot);
        // Persist the consumed crossing BEFORE scheduling external work. A crash
        // can lose an attempt, but must not replay it and duplicate side effects.
        if (!Save(updated)) return;
        foreach (var trigger in triggers) queue.Enqueue(trigger);
        _ = DrainAsync();
    }

    public void PollFailed() => latest = null;

    private async Task DrainAsync()
    {
        if (draining || stopped) return;
        draining = true;
        try
        {
            while (!stopped && Error is null && queue.TryDequeue(out var trigger))
            {
                var rule = Document.Rules.FirstOrDefault(r => r.Id == trigger.Rule.Id);
                var state = Document.States.GetValueOrDefault(trigger.Rule.Id);
                if (rule is null || state?.RunId != trigger.RunId) continue;
                if (!rule.Enabled || !rule.SameTriggerAndAction(trigger.Rule) || latest is null
                    || DateTimeOffset.UtcNow - latest.FetchedAt > TimeSpan.FromMinutes(11)
                    || PromptThresholds.Reading(rule, latest) is not { } reading || !rule.Matches(reading.Remaining))
                {
                    Update(trigger, "Skipped", "Quota or rule changed before this queued prompt could start.");
                    continue;
                }
                if (!Update(trigger, "Running", "")) break;
                using var cancellation = new CancellationTokenSource();
                running = cancellation;
                Notice?.Invoke("Codex rule started", $"{rule.Name}: {trigger.Remaining:0.##}% remaining.");
                PromptRunResult result;
                try { result = await run(rule, cancellation.Token); }
                catch (Exception) { result = new("Failed to run", "Unexpected execution error. The crossing will not be retried automatically."); }
                finally { running = null; }
                Update(trigger, result.Status, result.Output);
                if (!stopped) Notice?.Invoke("Codex rule " + result.Status.ToLowerInvariant(), $"{rule.Name}. "
                    + (rule.Action == PromptAction.CodexPrompt ? "Read the response in Prompt rules to confirm the requested action succeeded."
                        : result.Output.Split('\n')[0]));
            }
        }
        finally { draining = false; }
    }

    private bool Update(PromptTrigger trigger, string status, string output)
    {
        if (!Document.States.TryGetValue(trigger.Rule.Id, out var state) || state.RunId != trigger.RunId) return true;
        var states = new Dictionary<Guid, PromptRuleState>(Document.States)
        { [trigger.Rule.Id] = state with { Status = status, LastOutput = output } };
        return Save(Document with { States = states });
    }

    private bool Save(PromptRulesDocument updated)
    {
        try { store.Save(updated); Document = updated; Error = null; return true; }
        catch (Exception ex) when (StorageError(ex))
        {
            Error = "Prompt rules stopped: their state could not be saved. Check the data folder, then save Prompt rules to retry.";
            Notice?.Invoke("Codex prompt rules stopped", Error);
            return false;
        }
    }

    public void CancelRuns(bool shutdown = false)
    {
        stopped |= shutdown;
        running?.Cancel();
        while (queue.TryDequeue(out var trigger)) Update(trigger, "Cancelled", "Cancelled before launch.");
    }

    private static bool StorageError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or System.Text.Json.JsonException or FormatException or ArgumentException or NotSupportedException;
}
