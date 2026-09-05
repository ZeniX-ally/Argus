using System.Text;
using System.Text.RegularExpressions;

namespace FctAggregator;

public static class TodoGrouping
{
    public static string MergeKeyOf(string? failItem)
    {
        if (AppConfig.Instance.TodoSpecMerge)
        {
            try
            {
                var spec = G49TodoRules.Resolve(failItem);
                if (!string.IsNullOrEmpty(spec)) return spec;
            }
            catch {  }
        }
        return KeyOf(failItem);
    }

    public static string KeyOf(string? failItem)
    {
        var s = Normalize(failItem);
        if (s.Length == 0) return "";
        s = StripLeadingStepNo(s);

        var kept = KeptTokens(ParenRe.Replace(s, " "));
        if (kept.Count >= 2) return string.Join(" ", kept);

        kept = KeptTokens(s);
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

    public static string StripLeadingStepNo(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return s;
        var r = StepNoRe.Replace(s, "", 1);
        return r.Trim().Length == 0 ? s : r.Trim();
    }

    private static readonly Regex StepNoRe = new(
        @"^(?:" +
        @"(?:(?:step|item|test|seq|no\.?)\s*)?\d+(?:[\.\-]\d+)*\s*[\)\]:：、,，]\s*" +
        @"|(?:step|item|test|seq|no\.?)\s*\d+(?:[\.\-]\d+)*[\s_\-\.]+" +
        @"|\d+(?:[\.\-]\d+)+[\s_\-]+" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            if (ch == '\u3000') { sb.Append(' '); continue; }
            if (ch >= '\uFF01' && ch <= '\uFF5E') { sb.Append((char)(ch - 0xFEE0)); continue; }
            sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static IEnumerable<string> Tokenize(string s) =>
        Regex.Split(s, @"[\s_\-\./\\:;,、|#@!?""'`~+*=<>\(\)\[\]\{\}]+")
             .Where(t => t.Length > 0);

    private static bool IsIndexToken(string t)
    {
        if (t.Length == 0) return true;
        if (Regex.IsMatch(t, @"^\d+(\.\d+)?(v|va|vdc|vac|a|ma|ua|w|mw|kw|hz|khz|mhz|s|ms|us|ns|ohm|k|m|db|c|f|pa|rpm|bit|byte|kb|mb)\d*$"))
            return false;
        if (Regex.IsMatch(t, @"^\d+v\d+$")) return false;
        if (Regex.IsMatch(t, @"^\d+(\.\d+)*$")) return true;
        if (Regex.IsMatch(t, @"^(ch|chan|channel|no|num|nr|idx|index|seq|step|item|test|pin|port|slot|dut|unit|site|u|j|p|q|r|cn|con|jp|sw|led|rly|relay)\d+$"))
            return true;
        return false;
    }

    public const string SourceItemsTag = "来源测试项：";

    public static string BuildSourceItemsNote(IEnumerable<string> items) =>
        SourceItemsTag + "\n" + string.Join("\n", items);

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

    public const int HighThreshold = 20;
    public const int MediumThreshold = 5;

    public static string PriorityZhOf(int failCount) =>
        failCount >= HighThreshold ? "高" : failCount >= MediumThreshold ? "中" : "低";

    public static Color PriorityColorOf(int failCount) =>
        failCount >= HighThreshold ? Theme.Danger
      : failCount >= MediumThreshold ? Color.FromArgb(89, 89, 89)
      : Color.FromArgb(179, 179, 179);
}
