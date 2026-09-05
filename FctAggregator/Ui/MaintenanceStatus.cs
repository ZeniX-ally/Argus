namespace FctAggregator;

public static class MaintenanceMeta
{
    public sealed record StatusDef(string Key, string Zh, Color Accent);

    public static readonly StatusDef[] Statuses =
    {
        new("unknown",       "未知问题", Color.FromArgb(140, 140, 140)),
        new("open",          "待办",     Color.FromArgb(200, 16, 46)),
        new("in_progress",   "持续跟踪", Color.FromArgb(20, 20, 20)),
        new("resolved",      "已完成",   Color.FromArgb(191, 191, 191)),
    };

    public const string LegacyInvestigating = "investigating";

    public const string DefaultStatus = "open";

    public const string DoneStatus = "resolved";

    public const string LegacyClosed = "closed";

    private static readonly Dictionary<string, StatusDef> ByKey =
        Statuses.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    public static string ZhOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (ByKey.TryGetValue(key, out var d)) return d.Zh;
        if (string.Equals(key, LegacyClosed, StringComparison.OrdinalIgnoreCase))
            return ByKey[DoneStatus].Zh;
        if (string.Equals(key, LegacyInvestigating, StringComparison.OrdinalIgnoreCase))
            return ByKey[DefaultStatus].Zh;
        return key;
    }

    public static string KeyOf(string? zh)
    {
        if (string.IsNullOrEmpty(zh)) return "";
        foreach (var s in Statuses)
            if (s.Zh == zh) return s.Key;
        return "";
    }

    public static Color AccentOf(string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            if (ByKey.TryGetValue(key, out var d)) return d.Accent;
            if (string.Equals(key, LegacyClosed, StringComparison.OrdinalIgnoreCase))
                return ByKey[DoneStatus].Accent;
            if (string.Equals(key, LegacyInvestigating, StringComparison.OrdinalIgnoreCase))
                return ByKey[DefaultStatus].Accent;
        }
        return Color.FromArgb(140, 140, 140);
    }

    public static string Normalize(string? key)
    {
        if (string.IsNullOrEmpty(key)) return DefaultStatus;
        if (ByKey.TryGetValue(key, out var d)) return d.Key;
        if (string.Equals(key, LegacyClosed, StringComparison.OrdinalIgnoreCase)) return DoneStatus;
        if (string.Equals(key, LegacyInvestigating, StringComparison.OrdinalIgnoreCase)) return DefaultStatus;
        return DefaultStatus;
    }

    public static object[] FilterItems() =>
        new object[] { "全部" }.Concat(Statuses.Select(s => (object)s.Zh)).ToArray();

    public static object[] StatusItems() => Statuses.Select(s => (object)s.Zh).ToArray();

    private static readonly Dictionary<string, string> SeverityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = "严重", ["major"] = "一般", ["minor"] = "轻微",
    };

    public static readonly string[] SeverityOrderZh = { "一般", "严重", "轻微" };

    public const string DefaultSeverity = "major";

    public static string SeverityZhOf(string? key) =>
        string.IsNullOrEmpty(key) ? "" : SeverityMap.GetValueOrDefault(key, key);

    public static string SeverityKeyOf(string? zh) => zh switch
    {
        "严重" => "critical",
        "轻微" => "minor",
        "一般" => "major",
        _ => DefaultSeverity,
    };

    public static Color SeverityColorOf(string? key) => key?.ToLowerInvariant() switch
    {
        "critical" => Theme.Danger,
        "minor" => Color.FromArgb(178, 178, 178),
        _ => Color.FromArgb(89, 89, 89),
    };
}
