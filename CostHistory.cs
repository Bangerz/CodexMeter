using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexMeter;

public sealed record CostDay(DateOnly Date, double CostUSD, long TotalTokens, bool MissingPricing);
public sealed record CostSession(string Id, string DisplayName, DateTimeOffset LastActivity,
    double CostUSD, long TotalTokens, bool MissingPricing);
public sealed record CostPeriods(double Today, double Week, double Month, double AllTime);

public sealed record CostHistory(DateTimeOffset GeneratedAt, IReadOnlyList<CostDay> Daily,
    IReadOnlyList<CostSession> RecentSessions)
{
    public bool HasMissingPricing => Daily.Any(day => day.MissingPricing) || RecentSessions.Any(session => session.MissingPricing);
    public bool IsStale(DateTimeOffset now) => now - GeneratedAt > TimeSpan.FromDays(7);

    // Daily dates are the collector's local calendar dates. Sessions are never added to
    // these totals: their costs overlap the daily usage.
    public CostPeriods Periods(DateOnly today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateOnly(today.Year, today.Month, 1);
        return new(Daily.Where(day => day.Date == today).Sum(day => day.CostUSD),
            Daily.Where(day => day.Date >= monday && day.Date <= today).Sum(day => day.CostUSD),
            Daily.Where(day => day.Date >= month && day.Date <= today).Sum(day => day.CostUSD),
            Daily.Sum(day => day.CostUSD));
    }

    public static CostHistory Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema != 1)
            throw new FormatException("Unsupported cost-history format.");
        if (Text(root, "provider") != "codex") throw new FormatException("Select a Codex cost-history file.");
        var generatedAt = Timestamp(root, "generatedAt");
        _ = Cost(root, "totalCost");
        var daily = Array(root, "daily").Select(day => new CostDay(Date(day), Cost(day, "totalCost"),
            Tokens(day), Flag(day))).ToArray();
        if (daily.Select(day => day.Date).Distinct().Count() != daily.Length)
            throw new FormatException("Cost-history daily dates must be unique.");
        if (!double.IsFinite(daily.Sum(day => day.CostUSD))) throw new FormatException("Invalid cost-history total.");
        var sessions = Array(root, "recentSessions").Select(session => new CostSession(Text(session, "id"),
            CleanName(Text(session, "displayName")), Timestamp(session, "lastActivity"), Cost(session, "costUSD"),
            Tokens(session), Flag(session)))
            .OrderByDescending(session => session.LastActivity).DistinctBy(session => session.Id).Take(5).ToArray();
        return new(generatedAt, daily, sessions);
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : throw new FormatException($"Missing {name} in cost history.");
    private static string Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw new FormatException($"Invalid {name} in cost history.");
    private static double Cost(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var cost) && double.IsFinite(cost) && cost >= 0
            ? cost : throw new FormatException($"Invalid {name} in cost history.");
    private static long Tokens(JsonElement root) =>
        root.TryGetProperty("totalTokens", out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var tokens) && tokens >= 0 ? tokens : throw new FormatException("Invalid token count in cost history.");
    private static bool Flag(JsonElement root) => !root.TryGetProperty("missingPricing", out var value) ? false
        : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
        : throw new FormatException("Invalid pricing flag in cost history.");
    private static DateOnly Date(JsonElement root) =>
        DateOnly.TryParseExact(Text(root, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new FormatException("Invalid daily date in cost history.");
    private static DateTimeOffset Timestamp(JsonElement root, string name)
    {
        var text = Text(root, name);
        var hasOffset = text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':';
        return hasOffset && DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
            ? timestamp : throw new FormatException($"Invalid {name} in cost history.");
    }
    private static string CleanName(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).Take(512).ToArray());
}

internal static class CostHistoryFile
{
    private const long MaxBytes = 8 * 1024 * 1024;

    public static (CostHistory? History, string? Error) Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaxBytes) return (null, "Cost-history file is too large. Select the compact Codex hover export.");
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 24 });
            return (CostHistory.Parse(document.RootElement), null);
        }
        catch (FileNotFoundException) { return (null, "Cost-history file not found. Choose a file from the tray menu."); }
        catch (DirectoryNotFoundException) { return (null, "Cost-history folder not found. Choose a file from the tray menu."); }
        catch (FormatException ex) { return (null, ex.Message); }
        catch (JsonException) { return (null, "Cost-history JSON could not be read. Run the collector again."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        { return (null, "Cost history is unavailable. Check the selected file or run the collector again."); }
    }
}

internal sealed class CostHistorySettings
{
    private const string Key = @"Software\CodexMeter";
    public const string DefaultPath = @"D:\Tools\UsageCollector\data\codex-hover.json";
    public bool Enabled { get; private set; }
    public string Path { get; private set; } = DefaultPath;

    public static CostHistorySettings Load()
    {
        var settings = new CostHistorySettings();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            settings.Enabled = key?.GetValue("ShowCostHistory") is int enabled && enabled == 1;
            if (key?.GetValue("CostHistoryPath") is string path && !string.IsNullOrWhiteSpace(path)) settings.Path = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return settings;
    }

    public void Save(bool enabled, string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("CostHistoryPath", path, RegistryValueKind.String);
        key.SetValue("ShowCostHistory", enabled ? 1 : 0, RegistryValueKind.DWord);
        Enabled = enabled;
        Path = path;
    }
}
