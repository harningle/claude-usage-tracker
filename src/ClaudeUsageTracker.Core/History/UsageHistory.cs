using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeUsageTracker.Core.Billing;
using ClaudeUsageTracker.Core.Json;
using ClaudeUsageTracker.Core.Providers;
using ClaudeUsageTracker.Core.Usage;

namespace ClaudeUsageTracker.Core.History;

/// <summary>
/// A profile's usage history. Port of upstream <c>UsageHistoryData</c>, plus the times of the last
/// periodic samples, which upstream kept in separate preference keys.
/// </summary>
public sealed class UsageHistoryData
{
    public List<UsageSnapshot> Snapshots { get; } = [];

    public DateTimeOffset? LastSessionRecordAt { get; set; }

    public DateTimeOffset? LastWeeklyRecordAt { get; set; }

    /// <summary>Session snapshots, newest first.</summary>
    public IReadOnlyList<UsageSnapshot> SessionSnapshots => Of(ResetType.SessionReset);

    /// <summary>Weekly snapshots, newest first.</summary>
    public IReadOnlyList<UsageSnapshot> WeeklySnapshots => Of(ResetType.WeeklyReset);

    /// <summary>Billing-cycle snapshots, newest first.</summary>
    public IReadOnlyList<UsageSnapshot> BillingCycleSnapshots => Of(ResetType.BillingCycle);

    /// <summary>
    /// Snapshots of one type, newest first. Upstream's validity filter is kept: a snapshot whose
    /// triggering reset lies more than a minute after its own timestamp is ignored.
    /// </summary>
    private List<UsageSnapshot> Of(ResetType type) =>
        [.. Snapshots
            .Where(s => s.ResetType == type && s.TriggeringResetTime <= s.Timestamp.AddSeconds(60))
            .OrderByDescending(s => s.Timestamp)];
}

/// <summary>
/// Decides what to record after each successful fetch. Port of upstream <c>UsageHistoryService</c>'s
/// sampling (every 10 minutes for the session, every 2 hours for the week), its retention limits and
/// <c>MenuBarManager</c>'s reset detection.
/// </summary>
public static class UsageHistoryRecorder
{
    public const int MaxSessionSnapshots = 1000;
    public const int MaxWeeklySnapshots = 500;
    public static readonly TimeSpan SessionRecordingInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan WeeklyRecordingInterval = TimeSpan.FromHours(2);

    /// <summary>
    /// Records what a new usage observation calls for and returns whether the history changed. A changed
    /// session or weekly reset time records the previous usage (when it had any); otherwise a periodic
    /// sample is taken when its interval has passed. A value the provider did not report is never
    /// recorded as zero.
    /// </summary>
    public static bool RecordUsage(
        UsageHistoryData history, UsageData? previous, UsageData current, Provider provider, DateTimeOffset now)
    {
        var changed = false;

        var sessionReset = previous is not null && ResetChanged(previous.SessionResetTime, current.SessionResetTime);
        if (sessionReset && (previous!.SessionTokensUsed > 0 || previous.SessionPercentage > 0))
        {
            history.Snapshots.Add(UsageSnapshot.FromSessionReset(previous, previous.SessionResetTime ?? now, provider, now));
            changed = true;
        }

        var weeklyReset = previous is not null && ResetChanged(previous.WeeklyResetTime, current.WeeklyResetTime);
        if (weeklyReset && (previous!.WeeklyTokensUsed > 0 || previous.WeeklyPercentage > 0))
        {
            history.Snapshots.Add(UsageSnapshot.FromWeeklyReset(previous, previous.WeeklyResetTime ?? now, provider, now));
            changed = true;
        }

        // Upstream skips the periodic sample in the cycle that detected a reset.
        if (!sessionReset && current.SessionPercentage is not null && Due(history.LastSessionRecordAt, SessionRecordingInterval, now))
        {
            history.Snapshots.Add(UsageSnapshot.FromSessionReset(current, now, provider, now));
            history.LastSessionRecordAt = now;
            changed = true;
        }

        if (!weeklyReset && current.WeeklyPercentage is not null && Due(history.LastWeeklyRecordAt, WeeklyRecordingInterval, now))
        {
            history.Snapshots.Add(UsageSnapshot.FromWeeklyReset(current, now, provider, now));
            history.LastWeeklyRecordAt = now;
            changed = true;
        }

        if (changed)
        {
            Prune(history, history.SessionSnapshots, MaxSessionSnapshots);
            Prune(history, history.WeeklySnapshots, MaxWeeklySnapshots);
        }

        return changed;
    }

    /// <summary>Records the previous billing cycle when its reset time changed and it had any spend.</summary>
    public static bool RecordBilling(UsageHistoryData history, ApiUsage? previous, ApiUsage current, DateTimeOffset now)
    {
        if (previous is null || !ResetChanged(previous.ResetsAt, current.ResetsAt) || previous.CurrentSpendCents <= 0)
        {
            return false;
        }

        history.Snapshots.Add(UsageSnapshot.FromBillingCycleReset(previous, previous.ResetsAt ?? now, now));
        return true;
    }

    /// <summary>
    /// Whether a window's reset time moved: it appeared, disappeared or changed by a minute or more.
    /// Upstream compared times truncated to the minute, which also fires when two readings of the same
    /// reset straddle a minute boundary.
    /// </summary>
    public static bool ResetChanged(DateTimeOffset? previous, DateTimeOffset? current) =>
        (previous, current) switch
        {
            (null, null) => false,
            ({ } before, { } after) => (after - before).Duration() >= TimeSpan.FromMinutes(1),
            _ => true,
        };

    private static bool Due(DateTimeOffset? last, TimeSpan interval, DateTimeOffset now) =>
        last is not { } previous || now - previous >= interval;

    /// <summary>Drops the oldest snapshots of one type beyond the limit.</summary>
    private static void Prune(UsageHistoryData history, IReadOnlyList<UsageSnapshot> newestFirst, int limit)
    {
        if (newestFirst.Count <= limit)
        {
            return;
        }

        var remove = newestFirst.Skip(limit).Select(s => s.Id).ToHashSet();
        history.Snapshots.RemoveAll(s => remove.Contains(s.Id));
    }
}

/// <summary>
/// The history file and export formats. Written by hand over <c>JsonNode</c> so Core stays usable from
/// the NativeAOT helper. Keys are upstream's property names in sorted order, values that are missing
/// are omitted and times are ISO 8601 UTC to the second, as upstream's export.
/// </summary>
public static class UsageHistoryJson
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The export: <c>{"snapshots": [...]}</c>, optionally of one reset type.</summary>
    public static string Export(UsageHistoryData history, ResetType? only = null) =>
        new JsonObject { ["snapshots"] = SnapshotArray(history, only) }.ToJsonString(Indented);

    /// <summary>The stored file: the export plus a format version and the last periodic sample times.</summary>
    public static string Serialise(UsageHistoryData history)
    {
        var root = new JsonObject();
        if (history.LastSessionRecordAt is { } session)
        {
            root["lastSessionRecordAt"] = Time(session);
        }

        if (history.LastWeeklyRecordAt is { } weekly)
        {
            root["lastWeeklyRecordAt"] = Time(weekly);
        }

        root["snapshots"] = SnapshotArray(history, null);
        root["version"] = CurrentVersion;
        return root.ToJsonString();
    }

    /// <summary>Parses a stored file.</summary>
    /// <exception cref="FormatException">The content is damaged or from a newer format version.</exception>
    public static UsageHistoryData Parse(string json)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new FormatException($"The history file is not valid JSON ({e.Message}).", e);
        }

        if (root is null || root["snapshots"] is not JsonArray snapshots)
        {
            throw new FormatException("The history file has no snapshot list.");
        }

        if (JsonRead.Integer(root, "version") is > CurrentVersion and var version)
        {
            throw new FormatException(
                $"The history file was written by a newer version (format {version}); this version reads format {CurrentVersion}.");
        }

        var history = new UsageHistoryData
        {
            LastSessionRecordAt = ClaudeUsageResponse.Timestamp(root["lastSessionRecordAt"]),
            LastWeeklyRecordAt = ClaudeUsageResponse.Timestamp(root["lastWeeklyRecordAt"]),
        };
        foreach (var node in snapshots)
        {
            history.Snapshots.Add(ParseSnapshot(node as JsonObject
                ?? throw new FormatException("The history file has a snapshot that is not an object.")));
        }

        return history;
    }

    private static JsonArray SnapshotArray(UsageHistoryData history, ResetType? only) =>
        new([.. history.Snapshots.Where(s => only is null || s.ResetType == only).Select(s => (JsonNode)SnapshotObject(s))]);

    private static JsonObject SnapshotObject(UsageSnapshot s)
    {
        // Keys in sorted order, as upstream's sortedKeys output.
        var o = new JsonObject();
        Put(o, "apiCurrency", s.ApiCurrency);
        Put(o, "apiPrepaidCreditsCents", s.ApiPrepaidCreditsCents);
        Put(o, "apiSpendCents", s.ApiSpendCents);
        Put(o, "designWeeklyPercentage", s.DesignWeeklyPercentage);
        Put(o, "designWeeklyTokensUsed", s.DesignWeeklyTokensUsed);
        Put(o, "fableWeeklyPercentage", s.FableWeeklyPercentage);
        Put(o, "fableWeeklyTokensUsed", s.FableWeeklyTokensUsed);
        o["id"] = s.Id.ToString("D").ToUpperInvariant();
        Put(o, "opusWeeklyPercentage", s.OpusWeeklyPercentage);
        Put(o, "opusWeeklyTokensUsed", s.OpusWeeklyTokensUsed);
        Put(o, "provider", s.Provider);
        o["resetType"] = RawValue(s.ResetType);
        Put(o, "sessionPercentage", s.SessionPercentage);
        Put(o, "sessionTokensUsed", s.SessionTokensUsed);
        Put(o, "sonnetWeeklyPercentage", s.SonnetWeeklyPercentage);
        Put(o, "sonnetWeeklyTokensUsed", s.SonnetWeeklyTokensUsed);
        o["timestamp"] = Time(s.Timestamp);
        o["triggeringResetTime"] = Time(s.TriggeringResetTime);
        Put(o, "weeklyPercentage", s.WeeklyPercentage);
        Put(o, "weeklyTokensUsed", s.WeeklyTokensUsed);
        return o;
    }

    private static UsageSnapshot ParseSnapshot(JsonObject o)
    {
        if (!Guid.TryParse(JsonRead.String(o, "id"), out var id)
            || ClaudeUsageResponse.Timestamp(o["timestamp"]) is not { } timestamp
            || ClaudeUsageResponse.Timestamp(o["triggeringResetTime"]) is not { } triggering
            || ParseResetType(JsonRead.String(o, "resetType")) is not { } type)
        {
            throw new FormatException("The history file has a snapshot without a valid ID, time or reset type.");
        }

        return new UsageSnapshot
        {
            Id = id,
            Timestamp = timestamp,
            ResetType = type,
            Provider = JsonRead.String(o, "provider"),
            SessionTokensUsed = JsonRead.Integer(o, "sessionTokensUsed"),
            SessionPercentage = JsonRead.Double(o, "sessionPercentage"),
            WeeklyTokensUsed = JsonRead.Integer(o, "weeklyTokensUsed"),
            WeeklyPercentage = JsonRead.Double(o, "weeklyPercentage"),
            OpusWeeklyTokensUsed = JsonRead.Integer(o, "opusWeeklyTokensUsed"),
            OpusWeeklyPercentage = JsonRead.Double(o, "opusWeeklyPercentage"),
            SonnetWeeklyTokensUsed = JsonRead.Integer(o, "sonnetWeeklyTokensUsed"),
            SonnetWeeklyPercentage = JsonRead.Double(o, "sonnetWeeklyPercentage"),
            DesignWeeklyTokensUsed = JsonRead.Integer(o, "designWeeklyTokensUsed"),
            DesignWeeklyPercentage = JsonRead.Double(o, "designWeeklyPercentage"),
            FableWeeklyTokensUsed = JsonRead.Integer(o, "fableWeeklyTokensUsed"),
            FableWeeklyPercentage = JsonRead.Double(o, "fableWeeklyPercentage"),
            ApiSpendCents = JsonRead.Integer(o, "apiSpendCents"),
            ApiPrepaidCreditsCents = JsonRead.Integer(o, "apiPrepaidCreditsCents"),
            ApiCurrency = JsonRead.String(o, "apiCurrency"),
            TriggeringResetTime = triggering,
        };
    }

    /// <summary>Upstream's raw values, which name the export file and fill the CSV's type column.</summary>
    public static string RawValue(ResetType type) => type switch
    {
        ResetType.SessionReset => "sessionReset",
        ResetType.WeeklyReset => "weeklyReset",
        ResetType.BillingCycle => "billingCycle",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No raw value for reset type."),
    };

    private static ResetType? ParseResetType(string? raw) => raw switch
    {
        "sessionReset" => ResetType.SessionReset,
        "weeklyReset" => ResetType.WeeklyReset,
        "billingCycle" => ResetType.BillingCycle,
        _ => null,
    };

    private static string Time(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void Put(JsonObject o, string key, string? value)
    {
        if (value is not null)
        {
            o[key] = value;
        }
    }

    private static void Put(JsonObject o, string key, long? value)
    {
        if (value is not null)
        {
            o[key] = value.Value;
        }
    }

    private static void Put(JsonObject o, string key, double? value)
    {
        if (value is not null)
        {
            o[key] = value.Value;
        }
    }
}

/// <summary>CSV export. Port of upstream <c>UsageHistoryData.exportToCSV</c>.</summary>
public static class UsageHistoryCsv
{
    public const string Header =
        "Timestamp,Reset Type,Session %,Session Tokens,Weekly %,Weekly Tokens,Opus %,Sonnet %,Design %,Fable %,API Spend,Currency";

    /// <summary>
    /// One row per snapshot, newest first, with times in <paramref name="zone"/> and blanks for missing
    /// values.
    /// </summary>
    public static string Export(UsageHistoryData history, TimeZoneInfo zone, ResetType? only = null)
    {
        var csv = new StringBuilder(Header).Append('\n');
        foreach (var s in history.Snapshots.Where(s => only is null || s.ResetType == only).OrderByDescending(s => s.Timestamp))
        {
            csv.Append(TimeZoneInfo.ConvertTime(s.Timestamp, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                .Append(UsageHistoryJson.RawValue(s.ResetType)).Append(',')
                .Append(Percent(s.SessionPercentage)).Append(',')
                .Append(s.SessionTokensUsed?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Percent(s.WeeklyPercentage)).Append(',')
                .Append(s.WeeklyTokensUsed?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Percent(s.OpusWeeklyPercentage)).Append(',')
                .Append(Percent(s.SonnetWeeklyPercentage)).Append(',')
                .Append(Percent(s.DesignWeeklyPercentage)).Append(',')
                .Append(Percent(s.FableWeeklyPercentage)).Append(',')
                // Upstream printed a Swift Double of dollars: at least one decimal, no trailing zeros beyond it.
                .Append(s.ApiSpendCents is { } cents ? (cents / 100m).ToString("0.0#", CultureInfo.InvariantCulture) : "").Append(',')
                .Append(s.ApiCurrency)
                .Append('\n');
        }

        return csv.ToString();
    }

    private static string Percent(double? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "";
}
