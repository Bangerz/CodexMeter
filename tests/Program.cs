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
