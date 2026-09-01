using System.Text;
using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>
/// 待办「大项合并」的归一化规则（智能分析同类测试项）。
///
/// 目标：`6.1.1.1 5V_Rail` / `6.1.1.2 5V_Rail` / `CAN_Bus_Test_CH1` / `CAN_Bus_Test_CH2`
/// 这类**同一类型、只差编号/通道/位号**的测试项，应该合并成一张待办卡，
/// 否则一个批次的不良能刷出上百张卡片，看板既卡也没法排优先级。
///
/// 但**不能过度合并**：`Check_5V_Rail` 与 `Check_12V_Rail` 是两条不同的电源轨，
/// 合并了就丢了诊断信息。所以规则是：
///   · 丢掉「纯序号」token：`3`、`ch1`、`pin7`、`u12`、`step4`、`no.2` …
///   · 保留「数字+单位」token：`5v`、`3v3`、`12v`、`100ms`、`1khz`、`24vdc` …
///   · 去掉开头的步骤号：`6.1.1.1 `、`12) `、`Step 3: `
///   · 大小写、全角/半角、各种分隔符（`_ - . / : # ( )` …）统一
///
/// 归一化结果就是 `todo_items.group_key`，**同 key 即同一张待办卡**。
/// </summary>
public static class TodoGrouping
{
    /// <summary>合并键。空输入返回空串（调用方应跳过）。</summary>
    public static string KeyOf(string? failItem)
    {
        var s = Normalize(failItem);
        if (s.Length == 0) return "";
        s = StripLeadingStepNo(s);

        // 先试着去掉括号里的限定词（通常是测量手段/条件，如 (DMM) (XCP) (KL30_LS_UC)）——
        // 同一个被测参数的不同测量路径应归为同一个大项。
        // 但如果去掉后剩不到 2 个有效 token（例如 `Check(Voltage)` / `Check(Current)`），
        // 括号里就是此项的核心语义，**不能丢**，否则会把不同项错合并。
        var kept = KeptTokens(ParenRe.Replace(s, " "));
        if (kept.Count >= 2) return string.Join(" ", kept);

        kept = KeptTokens(s);
        // 整条都是序号(例如 "6.1.1.1")时退回用原串，避免所有这类项挤成同一个 key
        if (kept.Count == 0) return s;
        return string.Join(" ", kept);
    }

    private static readonly Regex ParenRe = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);

    private static List<string> KeptTokens(string s)
    {
        var kept = new List<string>();
        foreach (var t in Tokenize(s))
            if (!IsIndexToken(t)) kept.Add(t);
        return kept;
    }

    /// <summary>
    /// 合并后的展示名：取各原始项里**最短**的一条（去掉步骤号），相同长度取字典序最小。
    /// 选「最短」是为了稳定 —— 后续再合入新变体时标题不会来回跳。
    /// </summary>
    public static string TitleOf(IEnumerable<string> variants)
    {
        string? best = null;
        foreach (var v in variants)
        {
            var t = StripLeadingStepNo((v ?? "").Trim());
            if (t.Length == 0) continue;
            if (best == null || t.Length < best.Length ||
                (t.Length == best.Length && string.CompareOrdinal(t, best) < 0))
                best = t;
        }
        return best ?? "";
    }

    /// <summary>
    /// 去掉开头的步骤号 / 序号前缀，保留可读的测试项名。
    ///
    /// ⚠ 坑：不能简单地“剔掉开头数字”—— `5V_Rail` 的 `5` 是电压值，剔了就成了 `V_Rail`，
    /// 且 `5V_Rail` 与 `12V_Rail` 会被错合并。所以只剔三种**确定是序号**的形式：
    ///   ① `12) ` / `6.1: ` / `no.3、`  —— 数字 + 标点
    ///   ② `Step 3 ` / `Test 12_`   —— 关键词 + 数字
    ///   ③ `6.1.1.1 ` / `1.2_`      —— **多段**数字 + 空白/下划线
    /// 单个数字 + 空格（如 `5 Rail`）故意**不**剔，宁可保守。
    /// </summary>
    public static string StripLeadingStepNo(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return s;
        var r = StepNoRe.Replace(s, "", 1);
        // 全是序号的项（去完变空）就保留原文
        return r.Trim().Length == 0 ? s : r.Trim();
    }

    private static readonly Regex StepNoRe = new(
        @"^(?:" +
        @"(?:(?:step|item|test|seq|no\.?)\s*)?\d+(?:[\.\-]\d+)*\s*[\)\]:：、,，]\s*" +   // 12)  6.1:  no.3、
        @"|(?:step|item|test|seq|no\.?)\s*\d+(?:[\.\-]\d+)*[\s_\-\.]+" +               // Step 3  Test12_
        @"|\d+(?:[\.\-]\d+)+[\s_\-]+" +                                                // 6.1.1.1  1.2_
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ==================== 内部 ====================

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            // 全角 -> 半角（全角空格单独处理）
            if (ch == '\u3000') { sb.Append(' '); continue; }
            if (ch >= '\uFF01' && ch <= '\uFF5E') { sb.Append((char)(ch - 0xFEE0)); continue; }
            sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static IEnumerable<string> Tokenize(string s) =>
        Regex.Split(s, @"[\s_\-\./\\:;,、|#@!?""'`~+*=<>\(\)\[\]\{\}]+")
             .Where(t => t.Length > 0);

    /// <summary>位号/通道/序号一类的 token（应丢弃）。数字+单位的不算。</summary>
    private static bool IsIndexToken(string t)
    {
        if (t.Length == 0) return true;
        // 数字+单位（5v / 3v3 / 12vdc / 100ms / 1khz / 2a）—— 有物理含义，必须保留
        if (Regex.IsMatch(t, @"^\d+(\.\d+)?(v|va|vdc|vac|a|ma|ua|w|mw|kw|hz|khz|mhz|s|ms|us|ns|ohm|k|m|db|c|f|pa|rpm|bit|byte|kb|mb)\d*$"))
            return false;
        // 3v3 / 1v8 这类中缀单位写法
        if (Regex.IsMatch(t, @"^\d+v\d+$")) return false;
        // 纯数字
        if (Regex.IsMatch(t, @"^\d+(\.\d+)*$")) return true;
        // 前缀 + 数字：通道 / 位号 / 步骤 / 序号
        if (Regex.IsMatch(t, @"^(ch|chan|channel|no|num|nr|idx|index|seq|step|item|test|pin|port|slot|dut|unit|site|u|j|p|q|r|cn|con|jp|sw|led|rly|relay)\d+$"))
            return true;
        return false;
    }

    // ==================== 合并清单写入 / 回读 ====================

    /// <summary>确认待办时写进维修记录备注的清单标头（卡片/预览回读用）。</summary>
    public const string SourceItemsTag = "来源测试项：";

    /// <summary>把合并的原始测试项拼成备注文本。</summary>
    public static string BuildSourceItemsNote(IEnumerable<string> items) =>
        SourceItemsTag + "\n" + string.Join("\n", items);

    /// <summary>
    /// 从维修记录备注里把合并的原始测试项读回来。
    /// 这样看板卡片与预览**不用额外查库**就能显示合并清单（记录本身已带着它）。
    /// </summary>
    public static List<string> ParseSourceItems(string? notes)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return result;
        var i = notes.IndexOf(SourceItemsTag, StringComparison.Ordinal);
        if (i < 0) return result;
        foreach (var line in notes[(i + SourceItemsTag.Length)..]
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.Length > 0) result.Add(t);
        }
        return result;
    }

    // ==================== 优先级（按 fail 次数） ====================

    /// <summary>fail 次数 → 优先级。阈值集中在此，UI 与导出共用。</summary>
    public const int HighThreshold = 20;
    public const int MediumThreshold = 5;

    public static string PriorityZhOf(int failCount) =>
        failCount >= HighThreshold ? "高" : failCount >= MediumThreshold ? "中" : "低";

    public static Color PriorityColorOf(int failCount) =>
        failCount >= HighThreshold ? Theme.Danger          // 高 → 主红
      : failCount >= MediumThreshold ? Color.FromArgb(89, 89, 89)      // 中 → 深灰
      : Color.FromArgb(179, 179, 179);                                 // 低 → 浅灰
}
