using System.Text;

namespace FctFailRanker;

public static class CsvExporter
{
    public class Summary
    {
        public int Total;
        public int Pass, Fail, Interrupted;
        public int DistinctSn;
        public double Yield;
        public int TotalFailOccurrences;
    }

    public class FailRank
    {
        public string Item = "";
        public int Count;
        public int AffectedUnits;
        public double Percent;
        public string Values = "";
        public string Limits = "";
        public string Units = "";
    }

    public static (Summary summary, List<FailRank> ranks) Aggregate(List<XmlRecord> records)
    {
        var s = new Summary { Total = records.Count };
        var sns = new HashSet<string>();
        var itemCount = new Dictionary<string, int>();
        var itemUnits = new Dictionary<string, HashSet<string>>();
        var itemValues = new Dictionary<string, HashSet<string>>();
        var itemLimits = new Dictionary<string, HashSet<string>>();
        var itemUnitSet = new Dictionary<string, HashSet<string>>();

        foreach (var r in records)
        {
            if (!string.IsNullOrEmpty(r.Sn)) sns.Add(r.Sn);
            switch (r.Result)
            {
                case "PASS": s.Pass++; break;
                case "FAIL": s.Fail++; break;
                case "INTERRUPTED": s.Interrupted++; break;
            }
            foreach (var fi in r.FailItems)
            {
                var item = fi.Name;
                itemCount[item] = itemCount.GetValueOrDefault(item) + 1;
                if (!itemUnits.TryGetValue(item, out var set))
                    itemUnits[item] = set = new HashSet<string>();
                if (!string.IsNullOrEmpty(r.Sn)) set.Add(r.Sn);
                s.TotalFailOccurrences++;

                if (!itemValues.ContainsKey(item))
                    itemValues[item] = new HashSet<string>();
                if (!string.IsNullOrEmpty(fi.Value))
                    itemValues[item].Add(fi.Value);

                if (!itemLimits.ContainsKey(item))
                    itemLimits[item] = new HashSet<string>();
                var limitStr = LimitString(fi.Lolim, fi.Hilim);
                if (!string.IsNullOrEmpty(limitStr))
                    itemLimits[item].Add(limitStr);

                if (!itemUnitSet.ContainsKey(item))
                    itemUnitSet[item] = new HashSet<string>();
                if (!string.IsNullOrEmpty(fi.Unit))
                    itemUnitSet[item].Add(fi.Unit);
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
                Values = itemValues.TryGetValue(kv.Key, out var vals) ? string.Join(", ", vals) : "",
                Limits = itemLimits.TryGetValue(kv.Key, out var lims) ? string.Join(", ", lims) : "",
                Units = itemUnitSet.TryGetValue(kv.Key, out var us) ? string.Join(", ", us) : "",
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Item, StringComparer.Ordinal)
            .ToList();

        return (s, ranks);
    }

    private static string LimitString(string lolim, string hilim)
    {
        if (string.IsNullOrEmpty(lolim) && string.IsNullOrEmpty(hilim)) return "";
        if (string.IsNullOrEmpty(lolim)) return $"<={hilim}";
        if (string.IsNullOrEmpty(hilim)) return $">={lolim}";
        return $"{lolim}~{hilim}";
    }

    public class GroupResult
    {
        public string Key = "";
        public Summary Summary = new();
        public List<FailRank> Ranks = new();
    }

    public static List<GroupResult> AggregateByModel(List<XmlRecord> records)
        => AggregateByKey(records, r => string.IsNullOrEmpty(r.Model) ? "(未知型号)" : r.Model);

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
        const int COLS = 8;

        void Line(params string[] cells)
        {
            var arr = new string[COLS];
            for (int i = 0; i < COLS; i++) arr[i] = i < cells.Length ? Esc(cells[i]) : "";
            sb.AppendLine(string.Join(",", arr));
        }
        void Blank() => sb.AppendLine(string.Join(",", new string[COLS]));
        void Section(string title) => Line("■ " + title);

        Line("FCT 不良项排名报表");
        Line("统计时间段", $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");
        Line("生成时间", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Blank();

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

        Section("不良项排名（按出现次数降序）");
        WriteRankRows(Line, ranks);
        Blank();

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

        Section("明细清单");
        sb.AppendLine("测试日期,类别,型号,SN,机台,结果,失败项数,失败项列表(值/规格),文件名");
        foreach (var rec in records
            .OrderBy(r => r.TestDate, StringComparer.Ordinal)
            .ThenBy(r => r.Filename, StringComparer.Ordinal))
        {
            var failList = string.Join(" | ", rec.FailItems.Select(f => f.ToDetail()));
            sb.AppendLine(string.Join(",",
                Esc(rec.TestDate), Esc(rec.Category), Esc(rec.Model), Esc(rec.Sn),
                Esc(rec.Station), Esc(rec.Result), rec.FailItems.Count.ToString(),
                Esc(failList), Esc(rec.Filename)));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static void WriteRankRows(Action<string[]> line, List<FailRank> ranks)
    {
        line(new[] { "排名", "不良项名称", "出现次数", "受影响产品数", "占比(%)", "测量值", "规格", "单位" });
        int rank = 1;
        foreach (var r in ranks)
        {
            line(new[] {
                rank.ToString(), r.Item, r.Count.ToString(), r.AffectedUnits.ToString(),
                r.Percent.ToString("F2"), r.Values, r.Limits, r.Units
            });
            rank++;
        }
    }

    public static void ExportRankOnly(
        string path, DateTime start, DateTime end,
        Summary s, List<FailRank> ranks)
    {
        var sb = new StringBuilder();
        void Line(params string[] cells)
        {
            var arr = new string[8];
            for (int i = 0; i < 8; i++) arr[i] = i < cells.Length ? Esc(cells[i]) : "";
            sb.AppendLine(string.Join(",", arr));
        }
        Line("不良项排名", $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}");
        Line($"总 FAIL {s.Fail} 台", $"不良项累计 {s.TotalFailOccurrences} 次", $"良率 {s.Yield:F2}%");
        Line();
        WriteRankRows(Line, ranks);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Esc(string? v)
    {
        v ??= "";
        if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' ||
                             v[0] == '\t' || v[0] == '\r'))
            v = "'" + v;
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
