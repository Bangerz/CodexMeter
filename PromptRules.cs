using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexMeter;

public enum QuotaScope { MostLimited, CodexWeekly, CodexFiveHour }
public enum ThresholdDirection { RisesToOrAbove, FallsToOrBelow }
public enum PromptPermissions { ReadOnly, WorkspaceWrite }
public enum PromptAction { CodexPrompt, PauseRecentGoals, ResumeRecentGoals }

public sealed record PromptRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "New rule";
    public bool Enabled { get; init; }
    public PromptAction Action { get; init; }
    public QuotaScope Scope { get; init; }
    public ThresholdDirection Direction { get; init; }
    public double Threshold { get; init; } = 80;
    public string Prompt { get; init; } = "";
    public string WorkingDirectory { get; init; } = AppContext.BaseDirectory;
    public PromptPermissions Permissions { get; init; }
    public int TimeoutMinutes { get; init; } = 10;

    public bool Matches(double remaining) => Direction == ThresholdDirection.RisesToOrAbove
        ? remaining >= Threshold : remaining <= Threshold;

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 80)
            throw new FormatException("Each rule needs an ID and a name of at most 80 characters.");
        if (!Enum.IsDefined(Scope) || !Enum.IsDefined(Direction) || !Enum.IsDefined(Permissions) || !Enum.IsDefined(Action)
            || !double.IsFinite(Threshold) || Threshold is < 0 or > 100 || TimeoutMinutes is < 1 or > 60)
            throw new FormatException($"{Name}: select a valid quota, direction, threshold (0–100), and timeout (1–60 minutes).");
        if (Action == PromptAction.CodexPrompt && (string.IsNullOrWhiteSpace(Prompt) || Prompt.Length > 32_000))
            throw new FormatException($"{Name}: enter a prompt of at most 32,000 characters.");
        if (Action == PromptAction.CodexPrompt && (string.IsNullOrWhiteSpace(WorkingDirectory) || !Path.IsPathFullyQualified(WorkingDirectory)))
            throw new FormatException($"{Name}: choose an absolute working folder.");
        if (Action == PromptAction.CodexPrompt && Enabled && !Directory.Exists(WorkingDirectory))
            throw new FormatException($"{Name}: the working folder does not exist.");
    }

    public bool SameTriggerAndAction(PromptRule other) => this with { Name = other.Name } == other;

    public static List<PromptRule> Examples() =>
    [
        new() { Name = "Resume goals", Action = PromptAction.ResumeRecentGoals, Threshold = 80, Direction = ThresholdDirection.RisesToOrAbove,
            Prompt = "Search the last 20 active sessions for paused goals and resume them. Use supported session/goal controls. If those controls are unavailable, report that limitation; do not edit session files or databases directly." },
        new() { Name = "Pause goals", Action = PromptAction.PauseRecentGoals, Threshold = 20, Direction = ThresholdDirection.FallsToOrBelow,
            Prompt = "Search the last 20 active sessions for active goals and pause them. Use supported session/goal controls. If those controls are unavailable, report that limitation; do not edit session files or databases directly." }
    ];
}

public sealed record PromptRuleState
{
    public bool? OnTriggerSide { get; init; }
    public string? BaselineKey { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public Guid? RunId { get; init; }
    public string Status { get; init; } = "Waiting for first reading";
    public DateTimeOffset? LastRunAt { get; init; }
    public string LastOutput { get; init; } = "";
}

public sealed record PromptRulesDocument
{
    public int Version { get; init; } = 1;
    public List<PromptRule> Rules { get; init; } = [];
    public Dictionary<Guid, PromptRuleState> States { get; init; } = [];
}

public sealed record PromptTrigger(PromptRule Rule, Guid RunId, double Remaining);

public static class PromptThresholds
{
    // Never let a missing/invalid window look like replenished quota. When the
    // set of windows changes, establish a baseline for the new measurement.
    public static (double Remaining, string Key)? Reading(PromptRule rule, UsageSnapshot snapshot)
    {
        var windows = snapshot.Windows.Where(w => rule.Scope switch
        {
            QuotaScope.CodexWeekly => w.BucketId == "codex" && w.DurationMinutes == 10080,
            QuotaScope.CodexFiveHour => w.BucketId == "codex" && w.DurationMinutes == 300,
            _ => true
        }).OrderBy(w => w.BucketId).ThenBy(w => w.Slot).ToArray();
        if (windows.Length == 0 || windows.Any(w => w.UsedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100)) return null;
        var key = JsonSerializer.Serialize(new { Plan = snapshot.Plan?.ToLowerInvariant(), Windows = windows.Select(w => new { w.BucketId, w.Slot, w.DurationMinutes }) });
        return (windows.Min(w => 100 - w.UsedPercent!.Value), key);
    }

    public static (PromptRulesDocument Document, List<PromptTrigger> Triggers) Observe(PromptRulesDocument document, UsageSnapshot snapshot)
    {
        var states = new Dictionary<Guid, PromptRuleState>(document.States);
        var triggers = new List<PromptTrigger>();
        foreach (var rule in document.Rules.Where(r => r.Enabled))
        {
            var state = states.GetValueOrDefault(rule.Id) ?? new();
            if (state.ObservedAt is { } at && snapshot.FetchedAt <= at) continue;
            if (Reading(rule, snapshot) is not { } reading) continue;
            var matches = rule.Matches(reading.Remaining);
            var baseline = state.OnTriggerSide is null || state.BaselineKey != reading.Key;
            if (!baseline && state.OnTriggerSide == false && matches)
            {
                var id = Guid.NewGuid();
                triggers.Add(new(rule, id, reading.Remaining));
                state = state with { RunId = id, Status = "Queued", LastRunAt = snapshot.FetchedAt, LastOutput = "" };
            }
            states[rule.Id] = state with { OnTriggerSide = matches, BaselineKey = reading.Key, ObservedAt = snapshot.FetchedAt };
        }
        return (document with { States = states }, triggers);
    }
}

public sealed class PromptRuleStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public PromptRulesDocument Load()
    {
        if (!File.Exists(path)) return new() { Rules = PromptRule.Examples() };
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new FormatException("Prompt settings exceed 4 MiB.");
        var result = JsonSerializer.Deserialize<PromptRulesDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new FormatException("Prompt settings are empty.");
        Validate(result);
        return result;
    }

    public static void Validate(PromptRulesDocument document)
    {
        if (document.Version != 1 || document.Rules is null || document.States is null || document.Rules.Count > 32
            || document.Rules.Any(r => r is null) || document.States.Any(s => s.Value is null)
            || document.Rules.Select(r => r.Id).Distinct().Count() != document.Rules.Count)
            throw new FormatException("Invalid prompt settings. Use up to 32 rules with unique IDs.");
        foreach (var rule in document.Rules) rule.Validate();
    }

    public void Save(PromptRulesDocument document)
    {
        Validate(document);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, document, JsonOptions);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
