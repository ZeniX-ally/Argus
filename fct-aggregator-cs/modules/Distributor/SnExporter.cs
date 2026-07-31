using System.Text;

using Cell = FctShared.Xlsx.Cell;    // v2.9.3: xlsx 外壳已抽到 FctShared.Xlsx
using Sheet = FctShared.Xlsx.Sheet;

namespace FctDistributor;

/// <summary>
/// 导出 xlsx 台账：机台号 / 日期 / 金板 SN / 银板 SN（各自带 Golden / Silvery 标注）。
///
/// 金银板**只有两块**，SN 固定：
///   金板 Golden  = ...K30500272
///   银板 Silvery = ...K30500274
/// 台账里只出现这两块板，其它 SN 的文件一律不进表。
///
/// 两个数据源：
///   ① 当前计划（扫描/生成后）—— 这一批投放了什么
///   ② 目标根目录现有文件 —— 机台上实际已有什么（不依赖计划）
/// </summary>
public static class SnExporter
{
    // ==================== 金 / 银板 ====================

    public const string SnGolden  = "E3002781AFV75236898002K30500272";
    public const string SnSilvery = "E3002781AFV75236898002K30500274";

    /// <summary>金银板的型号（SN 前 8 位）—— 也就是它们**唯一**可能待的目录名。</summary>
    public static string Model => SnGolden[..8];          // E3002781

    public enum Kind { None, Golden, Silvery }

    /// <summary>
    /// 判定是金板还是银板；都不是则 None（不进台账）。
    /// SN **全程不改写**，所以这里直接整串精确比。
    /// </summary>
    public static Kind KindOf(string? sn)
    {
        var s = (sn ?? "").Trim();
        if (s.Equals(SnGolden, StringComparison.OrdinalIgnoreCase)) return Kind.Golden;
        if (s.Equals(SnSilvery, StringComparison.OrdinalIgnoreCase)) return Kind.Silvery;
        return Kind.None;
    }

    public static string Label(Kind k) => k switch
    {
        Kind.Golden => "Golden（金板）",
        Kind.Silvery => "Silvery（银板）",
        _ => "",
    };

    /// <summary>一块板（= 一个 XML 文件）。</summary>
    public sealed record Board(
        string Station,      // FCT4
        string DateDir,      // 20260701
        string Model,        // E3002781
        string Sn,           // E3002781AFV...
        string Prefix,       // O / P
        string FileName,
        DateTime Stamp,
        string Status)       // 待生成 / 已生成 / 已存在 / 跳过
    {
        public Kind Kind => KindOf(Sn);
    }

    // ==================== 取数 ====================

    /// <summary>从当前计划取（跳过的条目不进台账 —— 它们不会落盘）。</summary>
    public static List<Board> FromPlan(IEnumerable<PlanItem> plan, bool executed)
    {
        var list = new List<Board>();
        foreach (var it in plan)
        {
            if (!it.Ok || it.Skip) continue;
            list.Add(new Board(it.Station, it.DateDir, it.Model, it.Sn, it.Prefix,
                               it.FileName, it.Stamp, executed ? "已生成" : "待生成"));
        }
        return Sort(list);
    }

    /// <summary>扫目标根目录下现有的 Offline\{型号}\{日期}\*.xml。</summary>
    public static List<Board> FromTarget(string root, Action<string>? log = null)
    {
        var list = new List<Board>();
        var dirs = TemplateGenerator.ScanExistingDateDirs(root);
        int bad = 0;
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d.Path, "*.xml"))
            {
                var name = Path.GetFileName(f);
                var p = NameParser.Parse(name);
                if (p.Error != null) { bad++; continue; }
                list.Add(new Board(p.Station, d.Date.ToString("yyyyMMdd"), p.Model, p.Sn,
                                   p.Prefix, name, p.Stamp, "已存在"));
            }
        log?.Invoke($"[导出] 扫到现有目录 {dirs.Count} 个、可解析文件 {list.Count} 个" +
                    (bad > 0 ? $"（文件名无法解析 {bad} 个已忽略）" : ""));
        return Sort(list);
    }

    private static List<Board> Sort(List<Board> list) => list
        .OrderBy(b => b.DateDir, StringComparer.Ordinal)
        .ThenBy(b => b.Station, StringComparer.Ordinal)
        .ThenBy(b => b.Stamp)
        .ToList();

    // ==================== 导出 ====================

    public sealed class Result
    {
        public int Rows;        // 台账行数
        public int Golden;      // 金板块数
        public int Silvery;     // 银板块数
        public int Excluded;    // 非金银板、被排除的文件数
        public string Path = "";
    }

    /// <summary>
    /// 写 xlsx。单个 Sheet：机台号 | 日期 | 金板 SN | 标注 | 银板 SN | 标注。
    /// 每个【机台 + 日期】一行；只有金/银板进表。
    /// </summary>
    public static Result Export(string path, List<Board> boards, Action<string>? log = null)
    {
        var kept = boards.Where(b => b.Kind != Kind.None).ToList();
        int excluded = boards.Count - kept.Count;
        if (excluded > 0)
            log?.Invoke($"[导出] 已排除 {excluded} 个非金/银板文件（台账只收 ...272 金板 与 ...274 银板）");

        var sheet = new Sheet { Name = "金银板SN台账" };
        sheet.AddHeader(("机台号", 12), ("日期", 14),
                        ("金板 SN", 36), ("标注", 18),
                        ("银板 SN", 36), ("标注", 18));

        int rows = 0, zi = 0;
        foreach (var g in kept
                    .GroupBy(b => (b.Station, b.DateDir))
                    .OrderBy(g => g.Key.DateDir, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Station, StringComparer.Ordinal))
        {
            var golds = g.Where(b => b.Kind == Kind.Golden).ToList();
            var silvers = g.Where(b => b.Kind == Kind.Silvery).ToList();
            int n = Math.Max(golds.Count, silvers.Count);      // 正常就是 1 行

            for (int i = 0; i < n; i++)
            {
                bool zebra = zi++ % 2 == 1;
                int st = zebra ? XlsxWriter.S_ZEBRA : XlsxWriter.S_TEXT;
                int sc = zebra ? XlsxWriter.S_ZEBRA_C : XlsxWriter.S_CENTER;

                var row = sheet.NewRow();
                row.Add(Cell.T(g.Key.Station, sc));
                row.Add(Cell.T(FormatDate(g.Key.DateDir), sc));
                row.Add(Cell.T(i < golds.Count ? golds[i].Sn : "", st));
                row.Add(Cell.T(i < golds.Count ? Label(Kind.Golden) : "", sc));
                row.Add(Cell.T(i < silvers.Count ? silvers[i].Sn : "", st));
                row.Add(Cell.T(i < silvers.Count ? Label(Kind.Silvery) : "", sc));
                rows++;
            }
        }

        XlsxWriter.Write(path, new[] { sheet });

        return new Result
        {
            Rows = rows,
            Golden = kept.Count(b => b.Kind == Kind.Golden),
            Silvery = kept.Count(b => b.Kind == Kind.Silvery),
            Excluded = excluded,
            Path = path,
        };
    }

    /// <summary>20260701 -> 2026-07-01（写成文本，避免 Excel 按地区把日期猜错）。</summary>
    public static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8
            ? $"{yyyymmdd[..4]}-{yyyymmdd[4..6]}-{yyyymmdd[6..8]}"
            : yyyymmdd;
}
