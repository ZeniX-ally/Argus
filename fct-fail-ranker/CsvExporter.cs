using System.Text;

namespace FctFailRanker;

/// <summary>
/// 将扫描到的记录汇总并导出为 CSV。
/// 含三部分: 概览统计 / 不良项排名(按出现次数降序) / 明细清单。
/// CSV 使用 UTF-8 BOM, Excel 中文不乱码。
/// </summary>
public static class CsvExporter
{
    public class Summary
    {
        public int Total;              // 有效记录总数(不含 debug)
        public int Pass, Fail, Interrupted;
        public int DistinctSn;
        public double Yield;           // PASS / (PASS + FAIL)
        public int TotalFailOccurrences;   // 所有失败项累计出现次数
    }

    public class FailRank
    {
        public string Item = "";
        public int Count;              // 该不良项出现次数
        public int AffectedUnits;      // 受影响的不同 SN 数
        public double Percent;         // 占全部失败项次数的比例
    }

    public static (Summary summary, List<FailRank> ranks) Aggregate(List<XmlRecord> records)
    {
        var s = new Summary { Total = records.Count };
        var sns = new HashSet<string>();
        // item -> (次数, 影响的SN集合)
        var itemCount = new Dictionary<string, int>();
        var itemUnits = new Dictionary<string, HashSet<string>>();

        foreach (var r in records)
        {
            if (!string.IsNullOrEmpty(r.Sn)) sns.Add(r.Sn);
            switch (r.Result)
            {
                case "PASS": s.Pass++; break;
                case "FAIL": s.Fail++; break;
                case "INTERRUPTED": s.Interrupted++; break;
            }
            foreach (var item in r.FailItems)
            {
                itemCount[item] = itemCount.GetValueOrDefault(item) + 1;
                if (!itemUnits.TryGetValue(item, out var set))
                    itemUnits[item] = set = new HashSet<string>();
                if (!string.IsNullOrEmpty(r.Sn)) set.Add(r.Sn);
                s.TotalFailOccurrences++;
            }
        }

        s.DistinctSn = sns.Count;
        int denom = s.Pass + s.Fail;
        s.Yield = denom > 0 ? (double)s.Pass / denom * 100.0 : 0;

        var ranks = itemCount
            .Select(kv => new FailRank
            {
                Item = kv.Key,
                Count = kv.Value,
                AffectedUnits = itemUnits[kv.Key].Count,
                Percent = s.TotalFailOccurrences > 0
                    ? (double)kv.Value / s.TotalFailOccurrences * 100.0 : 0,
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Item, StringComparer.Ordinal)
            .ToList();

        return (s, ranks);
    }

    /// <summary>一组(型号/机台)的分组排名结果。</summary>
    public class GroupResult
    {
        public string Key = "";        // 分组键 (型号名或机台名)
        public Summary Summary = new();
        public List<FailRank> Ranks = new();
    }

    /// <summary>按型号分组, 每组内部各自排名。组间按 FAIL 台数降序。</summary>
    public static List<GroupResult> AggregateByModel(List<XmlRecord> records)
        => AggregateByKey(records, r => string.IsNullOrEmpty(r.Model) ? "(未知型号)" : r.Model);

    /// <summary>按机台(TESTER)分组, 每组内部各自排名。组间按 FAIL 台数降序。</summary>
    public static List<GroupResult> AggregateByStation(List<XmlRecord> records)
        => AggregateByKey(records, r => string.IsNullOrEmpty(r.Station) ? "(未知机台)" : r.Station);

    private static List<GroupResult> AggregateByKey(List<XmlRecord> records, Func<XmlRecord, string> keySelector)
    {
        return records
            .GroupBy(keySelector)
            .Select(g =>
            {
                var (sum, ranks) = Aggregate(g.ToList());
                return new GroupResult { Key = g.Key, Summary = sum, Ranks = ranks };
            })
            .OrderByDescending(gr => gr.Summary.Fail)
            .ThenBy(gr => gr.Key, StringComparer.Ordinal)
            .ToList();
    }

    public static void Export(
        string path, DateTime start, DateTime end,
        List<XmlRecord> records, Summary s, List<FailRank> ranks)
    {
        var sb = new StringBuilder();
        const int COLS = 5;   // 统一 5 列, 保证各段在 Excel 里列对齐

        void Line(params string[] cells)
        {
            var arr = new string[COLS];
            for (int i = 0; i < COLS; i++) arr[i] = i < cells.Length ? Esc(cells[i]) : "";
            sb.AppendLine(string.Join(",", arr));
        }
        void Blank() => sb.AppendLine(string.Join(",", new string[COLS]));
        void Section(string title) => Line("■ " + title);

        // ===== 标题 =====
        Line("FCT 不良项排名报表");
        Line("统计时间段", $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");
        Line("生成时间", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Blank();

        // ===== 概览 (对称的 指标|数值 两列) =====
        Section("概览");
        Line("指标", "数值");
        Line("有效记录总数", s.Total.ToString());
        Line("产品数(SN去重)", s.DistinctSn.ToString());
        Line("PASS", s.Pass.ToString());
        Line("FAIL", s.Fail.ToString());
        Line("INTERRUPTED(中断)", s.Interrupted.ToString());
        Line("良率(%)", s.Yield.ToString("F2"));
        Line("不良项累计次数", s.TotalFailOccurrences.ToString());
        Blank();

        // ===== 不良项排名(总) =====
        Section("不良项排名（按出现次数降序）");
        WriteRankRows(Line, ranks);
        Blank();

        // ===== 按型号分组排名 =====
        var byModel = AggregateByModel(records);
        if (byModel.Count > 1)
        {
            Section("按型号分组排名");
            foreach (var g in byModel)
            {
                Line($"【型号 {g.Key}】", $"FAIL {g.Summary.Fail} 台", $"中断 {g.Summary.Interrupted}", $"良率 {g.Summary.Yield:F2}%");
                WriteRankRows(Line, g.Ranks);
                Blank();
            }
        }

        // ===== 按机台分组排名 =====
        var byStation = AggregateByStation(records);
        if (byStation.Count > 1)
        {
            Section("按机台分组排名");
            foreach (var g in byStation)
            {
                Line($"【机台 {g.Key}】", $"FAIL {g.Summary.Fail} 台", $"中断 {g.Summary.Interrupted}", $"良率 {g.Summary.Yield:F2}%");
                WriteRankRows(Line, g.Ranks);
                Blank();
            }
        }

        // ===== 明细清单 (9 列, 单独宽度) =====
        Section("明细清单");
        sb.AppendLine("测试日期,类别,型号,SN,机台,结果,失败项数,失败项列表,文件名");
        foreach (var rec in records
            .OrderBy(r => r.TestDate, StringComparer.Ordinal)
            .ThenBy(r => r.Filename, StringComparer.Ordinal))
        {
            var failList = string.Join(" | ", rec.FailItems);
            sb.AppendLine(string.Join(",",
                Esc(rec.TestDate), Esc(rec.Category), Esc(rec.Model), Esc(rec.Sn),
                Esc(rec.Station), Esc(rec.Result), rec.FailItems.Count.ToString(),
                Esc(failList), Esc(rec.Filename)));
        }

        // UTF-8 BOM, Excel 打开中文不乱码
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>写排名表的表头 + 数据行 (统一 5 列)。</summary>
    private static void WriteRankRows(Action<string[]> line, List<FailRank> ranks)
    {
        line(new[] { "排名", "不良项名称", "出现次数", "受影响产品数", "占比(%)" });
        int rank = 1;
        foreach (var r in ranks)
        {
            line(new[] { rank.ToString(), r.Item, r.Count.ToString(), r.AffectedUnits.ToString(), r.Percent.ToString("F2") });
            rank++;
        }
    }

    /// <summary>
    /// 仅导出"不良项排名"单表 (干净表格, 便于直接贴报告/做图)。
    /// 顶部两行时间段说明, 之后就是一张按数量降序的排行表。
    /// </summary>
    public static void ExportRankOnly(
        string path, DateTime start, DateTime end,
        Summary s, List<FailRank> ranks)
    {
        var sb = new StringBuilder();
        void Line(params string[] cells)
        {
            var arr = new string[5];
            for (int i = 0; i < 5; i++) arr[i] = i < cells.Length ? Esc(cells[i]) : "";
            sb.AppendLine(string.Join(",", arr));
        }
        Line("不良项排名", $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");
        Line($"总 FAIL {s.Fail} 台", $"不良项累计 {s.TotalFailOccurrences} 次", $"良率 {s.Yield:F2}%");
        Line();
        WriteRankRows(Line, ranks);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>CSV 字段转义: 含逗号/引号/换行时加双引号并转义内部引号。</summary>
    private static string Esc(string? v)
    {
        v ??= "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
