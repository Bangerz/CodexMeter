using System.Text.Json;

namespace CodexMeter;

public enum UsagePace { Unknown, Ahead, OnTrack, Behind, FarBehind, Critical }

public sealed record UsageWindow(string BucketId, string BucketName, string Slot,
    double? UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double? Remaining => UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
    public string PercentText => Remaining is { } remaining ? $"{Math.Floor(remaining):0}%" : "Unavailable";
    public double? TimeRemainingPercent(DateTimeOffset now) =>
        ResetsAt is { } reset && DurationMinutes is > 0
            ? Math.Clamp((reset - now).TotalMinutes / DurationMinutes.Value * 100, 0, 100)
            : null;

    public UsagePace PaceAt(DateTimeOffset now)
    {
        if (Remaining is not { } quota) return UsagePace.Unknown;
        if (quota <= 0) return UsagePace.Critical;
        if (ResetsAt is not { } reset || reset <= now || TimeRemainingPercent(now) is not { } time)
            return UsagePace.Unknown;
        var difference = quota - time;
        return difference switch
        {
            > 5 => UsagePace.Ahead,
            >= -5 => UsagePace.OnTrack,
            >= -15 => UsagePace.Behind,
            >= -30 => UsagePace.FarBehind,
            _ => UsagePace.Critical
        };
    }

    public string WeekTimeText(DateTimeOffset now) =>
        DurationMinutes == 10080 && TimeRemainingPercent(now) is { } remaining
            ? $"{Math.Floor(remaining):0}% of week remaining"
            : "Week time unavailable";

    public string WindowName => DurationMinutes switch
    {
        300 => "5-hour", 10080 => "Weekly", 1440 => "Daily",
        > 0 when DurationMinutes % 1440 == 0 => $"{DurationMinutes / 1440}-day",
        > 0 when DurationMinutes % 60 == 0 => $"{DurationMinutes / 60}-hour",
        > 0 => $"{DurationMinutes}-minute",
        _ => Slot == "primary" ? "Primary window" : "Secondary window"
    };

    public string ResetText(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset) return "Reset time unavailable";
        if (reset <= now) return "Reset due · refresh to check";
        var left = reset - now;
        var relative = left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m"
            : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
        return $"Resets in {relative} · {reset.ToLocalTime():ddd, MMM d, h:mm tt}";
    }
}

public sealed record UsageSnapshot(DateTimeOffset FetchedAt, string? Plan, IReadOnlyList<UsageWindow> Windows)
{
    public UsageWindow? MostLimited => Windows.Where(w => w.Remaining.HasValue)
        .OrderBy(w => w.Remaining).FirstOrDefault();

    public static UsageSnapshot Parse(JsonElement result, DateTimeOffset now)
    {
        var buckets = new List<(string Id, JsonElement Value)>();
        if (Get(result, "rateLimitsByLimitId") is { ValueKind: JsonValueKind.Object } map)
            buckets.AddRange(map.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
                .Select(p => (p.Name, p.Value)));
        if (buckets.Count == 0 && Get(result, "rateLimits") is { ValueKind: JsonValueKind.Object } legacy)
            buckets.Add((String(legacy, "limitId") ?? "codex", legacy));

        var windows = new List<UsageWindow>();
        string? plan = null;
        foreach (var (id, bucket) in buckets.OrderBy(b => b.Id == "codex" ? 0 : 1).ThenBy(b => b.Id))
        {
            plan ??= String(bucket, "planType");
            var name = String(bucket, "limitName") ?? (id == "codex" ? "Codex" : id);
            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (Get(bucket, slot) is not { ValueKind: JsonValueKind.Object } window) continue;
                double? used = Get(window, "usedPercent") is { ValueKind: JsonValueKind.Number } p
                    && p.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
                int? duration = Get(window, "windowDurationMins") is { ValueKind: JsonValueKind.Number } d
                    && d.TryGetInt32(out var minutes) ? minutes : null;
                DateTimeOffset? reset = null;
                if (Get(window, "resetsAt") is { ValueKind: JsonValueKind.Number } r && r.TryGetInt64(out var seconds))
                {
                    try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                    catch (ArgumentOutOfRangeException) { /* An unknown reset must never become a fake date. */ }
                }
                windows.Add(new(id, name, slot, used, duration, reset));
            }
        }
        return new(now, plan, windows);
    }

    private static JsonElement? Get(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : null;
    private static string? String(JsonElement value, string key) =>
        Get(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
}
