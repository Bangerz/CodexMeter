namespace CodexMeter;

public sealed record UsageReset(UsageWindow Previous, UsageWindow Current);

public static class UsageResetDetector
{
    // Avoid treating a scheduled rollover near the boundary as an early reset.
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<UsageReset> FindEarlyResets(UsageSnapshot? previous, UsageSnapshot current)
    {
        if (previous is null || current.FetchedAt <= previous.FetchedAt
            || !string.Equals(previous.Plan, current.Plan, StringComparison.Ordinal)) return [];

        var resets = new List<UsageReset>();
        foreach (var window in current.Windows)
        {
            var prior = previous.Windows.FirstOrDefault(w => w.BucketId == window.BucketId && w.Slot == window.Slot);
            if (prior is null || prior.DurationMinutes is not > 0 || prior.DurationMinutes != window.DurationMinutes
                || prior.ResetsAt is not { } scheduled || scheduled - current.FetchedAt <= ClockTolerance
                || !ValidPercent(prior.UsedPercent) || !ValidPercent(window.UsedPercent)) continue;

            // A reset may keep its original deadline, and usage can resume between polls.
            // Compare actual quota, rather than requiring zero usage or a changed deadline.
            if (window.UsedPercent < prior.UsedPercent) resets.Add(new(prior, window));
        }
        return resets;
    }

    public static string NotificationText(IReadOnlyList<UsageReset> resets)
    {
        var text = string.Join("\n", resets.Select(reset =>
            $"{reset.Current.BucketName} {reset.Current.WindowName}: {reset.Previous.PercentText} → {reset.Current.PercentText} left. "
            + $"Was due {reset.Previous.ResetsAt!.Value.ToLocalTime():ddd, MMM d, h:mm tt}."));
        // Windows limits the notification body to 255 characters.
        return text.Length <= 255 ? text : text[..254] + "…";
    }

    private static bool ValidPercent(double? value) => value is { } number
        && double.IsFinite(number) && number is >= 0 and <= 100;
}
