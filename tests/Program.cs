using System.Diagnostics;
using System.Text.Json;
using CodexMeter;

internal static class Program
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1789800000);
    private static int checks;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("app-server")) return await FakeServer();
        if (args.Length == 2 && args[0] == "--cost-file")
        {
            using var document = JsonDocument.Parse(File.ReadAllText(args[1]));
            var collectedCosts = CostHistory.Parse(document.RootElement);
            Console.WriteLine($"Parsed Codex export: {collectedCosts.Daily.Count} dates, {collectedCosts.RecentSessions.Count} recent sessions.");
            foreach (var session in collectedCosts.RecentSessions) Console.WriteLine($"{session.DisplayName}: ${session.CostUSD:F2}");
            return 0;
        }
        if (args.Contains("--reset-check"))
        {
            CheckEarlyResets();
            Console.WriteLine($"PASS: {checks} focused early reset checks");
            return 0;
        }
        if (args.Contains("--pace-check"))
        {
            CheckPace();
            Console.WriteLine($"PASS: {checks} focused pace checks");
            return 0;
        }
        CheckPace();
        CheckWeekTime();
        if (args.Contains("--week-time-check"))
        {
            Console.WriteLine($"PASS: {checks} focused weekly time checks");
            return 0;
        }
        CheckEarlyResets();
        var weekly = Parse("""{"rateLimits":{"primary":{"usedPercent":55,"windowDurationMins":10080,"resetsAt":1789852721},"secondary":null,"planType":"pro"}}""");
        Check(weekly.Windows.Count == 1 && weekly.Windows[0].WindowName == "Weekly" && weekly.Windows[0].Remaining == 45,
            "Weekly-only Pro account does not invent a five-hour window");
        var multi = Parse("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":27,"windowDurationMins":300},"secondary":{"usedPercent":81,"windowDurationMins":10080}},"extra":{"limitName":"Other model","primary":{"usedPercent":9,"windowDurationMins":60}}}}""");
        Check(multi.Windows.Count == 3 && multi.MostLimited?.Remaining == 19,
            "All buckets are displayed, preferred over the legacy view, and most restrictive drives the icon");
        Check(multi.Windows[0].WindowName == "5-hour" && multi.Windows[2].BucketName == "Other model", "Duration and bucket labels come from the response");
        var unknown = Parse("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":null,"resetsAt":null},"secondary":null}}""");
        Check(unknown.MostLimited is null && unknown.Windows[0].PercentText == "Unavailable", "Missing percentage is unavailable, never zero usage");
        var clamp = Parse("""{"rateLimits":{"primary":{"usedPercent":105},"secondary":{"usedPercent":-5}}}""");
        Check(clamp.Windows[0].Remaining == 0 && clamp.Windows[1].Remaining == 100, "Remaining percentages stay within 0-100");
        var fraction = Parse("""{"rateLimits":{"primary":{"usedPercent":99.1}}}""");
        Check(fraction.Windows[0].PercentText == "0%", "The display conservatively rounds down remaining quota");
        Check(Parse("{}").Windows.Count == 0, "Empty response does not invent limits");
        Check(Parse("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":20}}}""").MostLimited?.Remaining == 80, "Empty map falls back to the legacy view");
        var invalidReset = Parse("""{"rateLimits":{"primary":{"usedPercent":0,"resetsAt":9223372036854775807}}}""");
        Check(invalidReset.Windows[0].ResetsAt is null, "Invalid timestamp stays unavailable");
        var resetDue = weekly.Windows[0] with { ResetsAt = Now.AddMinutes(-1) };
        Check(resetDue.ResetText(Now).Contains("refresh to check") && resetDue.Remaining == 45, "Passing a reset date does not manufacture replenished quota");

        const string costJson = """
            {"schemaVersion":1,"provider":"codex","generatedAt":"2026-09-29T20:00:00.123Z","totalCost":26,
             "daily":[{"date":"2026-08-15","totalCost":11,"totalTokens":100},
                      {"date":"2026-09-27","totalCost":7,"totalTokens":200},
                      {"date":"2026-09-28","totalCost":5,"totalTokens":300},
                      {"date":"2026-09-29","totalCost":3,"totalTokens":400,"missingPricing":true}],
             "recentSessions":[{"id":"a","displayName":"Old A","lastActivity":"2026-09-20T01:00:00Z","costUSD":1,"totalTokens":1},
                               {"id":"b","displayName":"Second","lastActivity":"2026-09-28T01:00:00Z","costUSD":2,"totalTokens":2},
                               {"id":"a","displayName":"Latest A","lastActivity":"2026-09-29T01:00:00Z","costUSD":3,"totalTokens":3},
                               {"id":"c","displayName":"Third","lastActivity":"2026-09-27T01:00:00Z","costUSD":4,"totalTokens":4},
                               {"id":"d","displayName":"Fourth","lastActivity":"2026-09-26T01:00:00Z","costUSD":5,"totalTokens":5},
                               {"id":"e","displayName":"Fifth","lastActivity":"2026-09-25T01:00:00Z","costUSD":6,"totalTokens":6},
                               {"id":"f","displayName":"Too old","lastActivity":"2026-09-24T01:00:00Z","costUSD":7,"totalTokens":7}]}
            """;
        var costs = ParseCosts(costJson);
        var periods = costs.Periods(new DateOnly(2026, 9, 29));
        Check(periods == new CostPeriods(3, 8, 15, 26), "Cost periods use local daily dates with a Monday week; session costs are never added twice");
        Check(costs.RecentSessions.Count == 5 && costs.RecentSessions[0].DisplayName == "Latest A"
            && costs.RecentSessions.Select(session => session.Id).SequenceEqual(new[] { "a", "b", "c", "d", "e" }),
            "Five recent sessions are sorted by activity and deduplicated by ID using the latest record");
        Check(costs.HasMissingPricing && !costs.IsStale(costs.GeneratedAt.AddDays(7)) && costs.IsStale(costs.GeneratedAt.AddDays(7).AddSeconds(1)),
            "Missing pricing is visible and a cost file becomes stale only after seven days");
        foreach (var (invalid, label) in new[]
        {
            (costJson.Replace("\"provider\":\"codex\"", "\"provider\":\"copilot\""), "Other providers cannot enter Codex cost history"),
            (costJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2"), "Unknown cost schema is rejected"),
            (costJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""), "Malformed schema is rejected without a JSON property exception"),
            (costJson.Replace("\"totalCost\":11", "\"totalCost\":-1"), "Negative costs are rejected"),
            (costJson.Replace("\"totalCost\":11", "\"totalCost\":1e400"), "Non-finite costs are rejected"),
            (costJson.Replace("2026-08-15", "2026-09-29"), "Duplicate daily dates are rejected to prevent double counting"),
            (costJson.Replace("2026-09-29T20:00:00.123Z", "2026-09-29T20:00:00.123"), "Cost freshness timestamps require an explicit timezone")
        })
        {
            try { ParseCosts(invalid); throw new Exception("Expected invalid cost history"); }
            catch (FormatException) { Check(true, label); }
        }

        var priorPath = Environment.GetEnvironmentVariable("CODEX_METER_CODEX_PATH");
        var log = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "protocol-test.json"));
        Environment.SetEnvironmentVariable("CODEX_METER_CODEX_PATH", Environment.ProcessPath);
        Environment.SetEnvironmentVariable("CODEX_METER_TEST_LOG", log);
        try
        {
            var actual = await new CodexClient().FetchAsync();
            var trace = JsonSerializer.Deserialize<string[]>(File.ReadAllText(log))!;
            Check(trace.SequenceEqual(new[] { "initialize", "initialized", "account/rateLimits/read" }),
                "The poll sends only handshake and usage-read messages; no model turn or reset request");
            Check(actual.MostLimited?.Remaining == 45, "The client ignores unrelated notifications and reads the matching response");
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_MODE", "error");
            try { await new CodexClient().FetchAsync(); throw new Exception("Expected sign-in error"); }
            catch (UsageException ex) { Check(ex.Message.StartsWith("Sign in"), "Authentication failures produce an actionable, sanitized message"); }
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_MODE", "wait");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var stopwatch = Stopwatch.StartNew();
            try { await new CodexClient().FetchAsync(cancel.Token); throw new Exception("Expected cancellation"); }
            catch (Exception ex) when (ex is UsageException or OperationCanceledException or IOException)
            { Check(stopwatch.Elapsed < TimeSpan.FromSeconds(8), "Cancellation closes the private helper promptly"); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_METER_CODEX_PATH", priorPath);
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_LOG", null);
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_MODE", null);
        }
        Console.WriteLine($"PASS: {checks} focused checks");
        return 0;
    }

    private static UsageSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return UsageSnapshot.Parse(document.RootElement, Now);
    }

    private static void CheckEarlyResets()
    {
        var weekly = new UsageWindow("codex", "Codex", "primary", 70, 10080, Now.AddDays(3));
        UsageSnapshot Sample(DateTimeOffset at, params UsageWindow[] windows) => new(at, "pro", windows);
        var before = Sample(Now, weekly);
        var reset = weekly with { UsedPercent = 0 };
        var after = Sample(Now.AddMinutes(10), reset);
        IReadOnlyList<UsageReset> Detect(UsageSnapshot? prior, UsageSnapshot next) => UsageResetDetector.FindEarlyResets(prior, next);

        Check(Detect(null, after).Count == 0, "First fetch establishes a baseline without notifying");
        Check(Detect(before, after) is [{ Previous: var oldWindow, Current: var newWindow }]
            && oldWindow == weekly && newWindow == reset, "Early reset to zero is detected even with an unchanged deadline");
        Check(Detect(before, Sample(after.FetchedAt, reset with { UsedPercent = 12, ResetsAt = Now.AddDays(7) })).Count == 1,
            "Early replenishment is detected after usage resumes, using the previous deadline");
        Check(Detect(before, Sample(after.FetchedAt, reset with { UsedPercent = 69.9 })).Count == 1,
            "Fractional replenishment is preserved");
        Check(Detect(after, Sample(after.FetchedAt.AddMinutes(10), reset)).Count == 0,
            "Repeated successful readings do not duplicate notifications");
        var usedAgain = Sample(Now.AddMinutes(20), weekly with { UsedPercent = 20 });
        Check(Detect(usedAgain, Sample(Now.AddMinutes(30), reset)).Count == 1,
            "A later distinct early reset can notify again with the same deadline");
        foreach (var used in new[] { 70.0, 75.0 })
            Check(Detect(before, Sample(after.FetchedAt, weekly with { UsedPercent = used, ResetsAt = Now.AddDays(7) })).Count == 0,
                "A schedule change without replenishment does not notify");
        foreach (var at in new[] { weekly.ResetsAt!.Value, weekly.ResetsAt.Value.AddMinutes(10), weekly.ResetsAt.Value.AddDays(1) })
            Check(Detect(before, Sample(at, reset with { ResetsAt = at.AddDays(7) })).Count == 0,
                "Scheduled rollover or sleep crossing the old deadline stays quiet");
        Check(Detect(before, Sample(weekly.ResetsAt!.Value.AddMinutes(-1), reset)).Count == 0
            && Detect(before, Sample(weekly.ResetsAt.Value.AddMinutes(-1).AddSeconds(-1), reset)).Count == 1,
            "A one-minute clock tolerance suppresses borderline scheduled rollovers");
        Check(Detect(before, Sample(Now.AddHours(2), reset)).Count == 1,
            "A successful check after a polling gap can detect replenishment before the old deadline");
        Check(Detect(before, after with { Plan = "plus" }).Count == 0,
            "Changing plans establishes a new baseline");
        foreach (var window in new[]
        {
            reset with { BucketId = "other" }, reset with { Slot = "secondary" },
            reset with { DurationMinutes = 300 }, reset with { DurationMinutes = null }
        })
            Check(Detect(before, Sample(after.FetchedAt, window)).Count == 0,
                "Different buckets, slots, or durations do not compare unrelated counters");
        foreach (var used in new double?[] { null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 101 })
            Check(Detect(before, Sample(after.FetchedAt, reset with { UsedPercent = used })).Count == 0
                && Detect(Sample(Now, weekly with { UsedPercent = used }), after).Count == 0,
                "Missing or invalid usage on either side cannot trigger a reset");
        Check(Detect(Sample(Now, weekly with { ResetsAt = null }), after).Count == 0,
            "An unknown previous deadline cannot establish an early reset");
        Check(Detect(before, Sample(Now, reset)).Count == 0 && Detect(before, Sample(Now.AddMinutes(-1), reset)).Count == 0,
            "Duplicate or backward observation times do not notify");
        var secondary = weekly with { Slot = "secondary", DurationMinutes = 300, ResetsAt = Now.AddHours(3) };
        var other = weekly with { BucketId = "other", BucketName = "Other" };
        var combined = Detect(Sample(Now, weekly, secondary, other),
            Sample(after.FetchedAt, other with { UsedPercent = 5 }, reset, secondary with { UsedPercent = 10 }));
        Check(combined.Count == 3 && combined[0].Previous == other && combined[2].Previous == secondary,
            "Multiple buckets and slots are matched by identity regardless of order");
        var message = UsageResetDetector.NotificationText(Detect(before, after));
        Check(message.Contains("Codex Weekly") && message.Contains("30% → 100%") && message.Contains("Was due"),
            "Notification identifies replenished quota and the previous reset schedule");
        Check(UsageResetDetector.NotificationText(combined).Contains("Other Weekly"),
            "Affected windows share a notification body");
        var longName = reset with { BucketName = new string('x', 300) };
        var longMessage = UsageResetDetector.NotificationText([new(weekly, longName)]);
        Check(longMessage.Length == 255 && longMessage.EndsWith("…"),
            "Long notification text fits the Windows limit");
    }

    private static void CheckPace()
    {
        var sample = new UsageWindow("codex", "Codex", "primary", 50, 10080, Now.AddDays(3.5));
        foreach (var (quota, expected) in new[]
        {
            (56.0, UsagePace.Ahead), (55.0, UsagePace.OnTrack),
            (50.0, UsagePace.OnTrack), (45.0, UsagePace.OnTrack),
            (44.9, UsagePace.Behind), (35.0, UsagePace.Behind),
            (34.9, UsagePace.FarBehind), (20.0, UsagePace.FarBehind),
            (19.9, UsagePace.Critical), (0.0, UsagePace.Critical)
        })
            Check((sample with { UsedPercent = 100 - quota }).PaceAt(Now) == expected,
                $"Quota {quota}% against 50% time: {expected}");
        Check(sample.PaceAt(Now.AddDays(1)) == UsagePace.Ahead, "Pace advances with time without new quota data");
        Check((sample with { DurationMinutes = 300, ResetsAt = Now.AddMinutes(150) }).PaceAt(Now) == UsagePace.OnTrack,
            "Short windows use their own duration");
        Check((sample with { ResetsAt = null }).PaceAt(Now) == UsagePace.Unknown, "Unknown reset stays neutral");
        Check((sample with { DurationMinutes = 0 }).PaceAt(Now) == UsagePace.Unknown, "Invalid duration stays neutral");
        Check((sample with { UsedPercent = null }).PaceAt(Now) == UsagePace.Unknown, "Unknown quota stays neutral");
        Check((sample with { ResetsAt = Now }).PaceAt(Now) == UsagePace.Unknown, "Expired quota is not shown as ahead");
        Check((sample with { UsedPercent = 100, ResetsAt = Now.AddMinutes(1) }).PaceAt(Now) == UsagePace.Critical,
            "Empty quota is red even near reset");
    }

    private static void CheckWeekTime()
    {
        var halfWeek = new UsageWindow("codex", "Codex", "primary", 69, 10080, Now.AddDays(3.5));
        Check(halfWeek.TimeRemainingPercent(Now) == 50 && halfWeek.WeekTimeText(Now) == "50% of week remaining",
            "Weekly time percentage uses the reset time and duration");
        var fractionalWeek = halfWeek with { ResetsAt = Now.AddDays(3.5).AddMinutes(1) };
        Check(fractionalWeek.WeekTimeText(Now) == "50% of week remaining",
            "Weekly time display conservatively rounds down fractional percentages");
        var fullWeek = halfWeek with { ResetsAt = Now.AddDays(7) };
        var overfullWeek = halfWeek with { ResetsAt = Now.AddDays(8) };
        Check(fullWeek.TimeRemainingPercent(Now) == 100 && overfullWeek.TimeRemainingPercent(Now) == 100
            && overfullWeek.WeekTimeText(Now) == "100% of week remaining",
            "Full and overfull weekly reset times stay at 100 percent");
        var pastReset = halfWeek with { ResetsAt = Now.AddMinutes(-1) };
        Check(pastReset.TimeRemainingPercent(Now) == 0 && pastReset.WeekTimeText(Now) == "0% of week remaining"
            && pastReset.Remaining == 31 && pastReset.PercentText == "31%",
            "An expired timer reaches zero without changing remaining quota");
        var exactReset = halfWeek with { ResetsAt = Now };
        Check(exactReset.TimeRemainingPercent(Now) == 0,
            "The timer reaches zero at the exact reset timestamp");
        foreach (var (window, label) in new[]
        {
            (halfWeek with { DurationMinutes = null }, "Missing duration"),
            (halfWeek with { DurationMinutes = 0 }, "Zero duration"),
            (halfWeek with { DurationMinutes = -1 }, "Negative duration"),
            (halfWeek with { ResetsAt = null }, "Missing reset")
        })
            Check(window.TimeRemainingPercent(Now) is null && window.WeekTimeText(Now) == "Week time unavailable",
                label + " keeps weekly time unavailable");
        var shortWindow = halfWeek with { DurationMinutes = 300, ResetsAt = Now.AddMinutes(150) };
        Check(shortWindow.TimeRemainingPercent(Now) == 50 && shortWindow.WeekTimeText(Now) == "Week time unavailable",
            "Generic timer math supports other windows without labelling them as weeks");
    }

    private static CostHistory ParseCosts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CostHistory.Parse(document.RootElement);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        checks++;
        Console.WriteLine("PASS " + label);
    }

    private static async Task<int> FakeServer()
    {
        var methods = new List<string>();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            var method = message.GetProperty("method").GetString()!;
            methods.Add(method);
            if (method == "initialize") Console.WriteLine("""{"id":1,"result":{}}""");
            else if (method == "account/rateLimits/read")
            {
                File.WriteAllText(Environment.GetEnvironmentVariable("CODEX_METER_TEST_LOG")!, JsonSerializer.Serialize(methods));
                switch (Environment.GetEnvironmentVariable("CODEX_METER_TEST_MODE"))
                {
                    case "error": Console.WriteLine("""{"id":2,"error":{"code":-32000,"message":"401 authentication required"}}"""); break;
                    case "wait": await Task.Delay(TimeSpan.FromMinutes(1)); break;
                    default:
                        Console.WriteLine("""{"method":"account/updated","params":{}}""");
                        Console.WriteLine("""{"id":"unrelated","result":{}}""");
                        Console.WriteLine("""{"id":2,"result":{"rateLimits":{"primary":{"usedPercent":55,"windowDurationMins":10080}}}}""");
                        break;
                }
            }
            else if (method != "initialized") return 2;
            await Console.Out.FlushAsync();
        }
        return 0;
    }
}
