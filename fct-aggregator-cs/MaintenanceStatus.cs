namespace FctAggregator;

/// <summary>
/// 维修记录的状态 / 严重度 <b>单一来源</b>。
/// 此前 StatusZh / SeverityZh 在 MaintenancePanel 与 MaintenanceExporter 各有一份，
/// 新增状态时极易漏改一处（导出会吐出原始英文 key），故统一到此处。
/// 看板列顺序即 <see cref="Statuses"/> 的数组顺序。
/// </summary>
public static class MaintenanceMeta
{
    /// <summary>一个状态的定义: key(入库值) / 中文名 / 强调色(看板列头与卡片)。</summary>
    public sealed record StatusDef(string Key, string Zh, Color Accent);

    /// <summary>
    /// 全部可选状态，顺序 = 看板列顺序 = 处理流程顺序:
    /// 未知问题(刚发现原因不明) → 待办(含未确认的不良 + 已确认待修) → 持续跟踪(问题未彻底解决，继续盯) → 已完成。
    /// </summary>
    public static readonly StatusDef[] Statuses =
    {
        new("unknown",       "未知问题", Color.FromArgb(124, 58, 237)),   // 紫
        new("open",          "待办",     Color.FromArgb(26, 115, 232)),   // 蓝
        new("in_progress",   "持续跟踪", Color.FromArgb(13, 148, 136)),   // 青
        new("resolved",      "已完成",   Color.FromArgb(22, 163, 74)),    // 绿
    };

    /// <summary>
    /// v3.2.x 及以前的旧状态 'investigating'(正在排查)。
    /// v3.3.0 起该列已去掉，历史记录归并到「待办」。此常量与兜底映射仅用于老库兼容显示。
    /// </summary>
    public const string LegacyInvestigating = "investigating";

    /// <summary>新建记录的默认状态(待办)。</summary>
    public const string DefaultStatus = "open";

    /// <summary>「已完成」状态 key。拖放到该列时会弹出维修人/措施补填框。</summary>
    public const string DoneStatus = "resolved";

    /// <summary>
    /// v2.1.0 及以前的历史状态 'closed'(已关闭)。
    /// v2.2.0 起状态体系合并为 5 个，Database.Init() 会把库里的 closed 一次性迁移为 resolved。
    /// 此常量与下面的兜底映射仅用于「外来 db / 迁移未执行」时仍能正确显示，不作为可选状态。
    /// </summary>
    public const string LegacyClosed = "closed";

    private static readonly Dictionary<string, StatusDef> ByKey =
        Statuses.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>状态 key → 中文名。未知 key 原样返回；legacy 'closed' 归为「已完成」，'investigating' 归为「待办」。</summary>
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

    /// <summary>中文名 → 状态 key。未匹配返回空串(表示“全部/不筛选”)。</summary>
    public static string KeyOf(string? zh)
    {
        if (string.IsNullOrEmpty(zh)) return "";
        foreach (var s in Statuses)
            if (s.Zh == zh) return s.Key;
        return "";
    }

    /// <summary>状态强调色。未知 key 用中性灰。</summary>
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
        return Color.FromArgb(113, 128, 150);
    }

    /// <summary>把 legacy / 未知状态规范化为有效状态之一，供看板分列用。'investigating' 归并到「待办」。</summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrEmpty(key)) return DefaultStatus;
        if (ByKey.TryGetValue(key, out var d)) return d.Key;
        if (string.Equals(key, LegacyClosed, StringComparison.OrdinalIgnoreCase)) return DoneStatus;
        if (string.Equals(key, LegacyInvestigating, StringComparison.OrdinalIgnoreCase)) return DefaultStatus;
        return DefaultStatus;
    }

    /// <summary>筛选下拉用: ["全部", 未知问题, 待办, 持续跟踪, 已完成]。</summary>
    public static object[] FilterItems() =>
        new object[] { "全部" }.Concat(Statuses.Select(s => (object)s.Zh)).ToArray();

    /// <summary>状态下拉用(不含“全部”)。</summary>
    public static object[] StatusItems() => Statuses.Select(s => (object)s.Zh).ToArray();

    // ==================== 严重度 ====================

    private static readonly Dictionary<string, string> SeverityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = "严重", ["major"] = "一般", ["minor"] = "轻微",
    };

    /// <summary>严重度下拉顺序(默认项在前)。</summary>
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

    /// <summary>严重度色条颜色: 严重=红 / 一般=橙 / 轻微=灰。</summary>
    public static Color SeverityColorOf(string? key) => key?.ToLowerInvariant() switch
    {
        "critical" => Theme.Danger,
        "minor" => Color.FromArgb(140, 152, 170),
        _ => Color.FromArgb(221, 107, 32),
    };
}
