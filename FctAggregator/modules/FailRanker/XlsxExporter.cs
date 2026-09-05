using System.IO.Compression;
using System.Text;

using Cell = FctShared.Xlsx.Cell;
using Sheet = FctShared.Xlsx.Sheet;

namespace FctFailRanker;

public static class XlsxExporter
{
    private const int S_NORMAL   = 0;
    private const int S_TITLE    = 1;
    private const int S_SUBTITLE = 2;
    private const int S_HEADER   = 3;
    private const int S_SECTION  = 4;
    private const int S_TEXT_C   = 5;
    private const int S_NUM_C    = 6;
    private const int S_NUM_C2   = 7;
    private const int S_KEY      = 8;
    private const int S_VAL      = 9;
    private const int S_TOP_TEXT = 10;
    private const int S_TOP_NUM  = 11;
    private const int S_TOP_NUM2 = 12;
    private const int S_ZEBRA_T  = 13;
    private const int S_ZEBRA_N  = 14;
    private const int S_ZEBRA_N2 = 15;
    private const int S_NUM_L    = 16;


    private static readonly string[] RankHeaders = { "排名", "不良项名称", "出现次数", "受影响产品数", "占比(%)", "测量值", "规格", "单位" };
    private static readonly double[] RankWidths  = { 8, 46, 12, 16, 12, 28, 24, 10 };

    public static void Export(
        string path, DateTime start, DateTime end,
        List<XmlRecord> records,
        CsvExporter.Summary summary, List<CsvExporter.FailRank> ranks)
    {
        var sheets = new List<Sheet>();

        var s1 = new Sheet { Name = "总排名", ColWidths = RankWidths.ToList() };
        AddTitle(s1, "FCT 不良项排名报表", 5);
        AddSubtitle(s1, $"统计时间段：{start:yyyy-MM-dd}  ~  {end:yyyy-MM-dd}      生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}", 5);
        s1.Rows.Add(new List<Cell>());

        AddSection(s1, "概览", 5);
        AddKV(s1, "有效记录总数", summary.Total);
        AddKV(s1, "产品数(SN去重)", summary.DistinctSn);
        AddKV(s1, "PASS", summary.Pass);
        AddKV(s1, "FAIL", summary.Fail);
        AddKV(s1, "INTERRUPTED(中断)", summary.Interrupted);
        AddKVd(s1, "良率(%)", summary.Yield);
        AddKV(s1, "不良项累计次数", summary.TotalFailOccurrences);
        s1.Rows.Add(new List<Cell>());

        AddSection(s1, "不良项排名（按出现次数降序）", 5);
        AddRankTable(s1, ranks);
        sheets.Add(s1);

        var byModel = CsvExporter.AggregateByModel(records);
        var s2 = new Sheet { Name = "各型号排名", ColWidths = RankWidths.ToList() };
        AddTitle(s2, "各型号不良项排名", 5);
        s2.Rows.Add(new List<Cell>());
        foreach (var g in byModel)
        {
            AddGroupSection(s2, $"型号 {g.Key}", g.Summary, 5);
            AddRankTable(s2, g.Ranks);
            s2.Rows.Add(new List<Cell>());
        }
        sheets.Add(s2);

        var byStation = CsvExporter.AggregateByStation(records);
        var s3 = new Sheet { Name = "各机台排名", ColWidths = RankWidths.ToList() };
        AddTitle(s3, "各机台不良项排名", 5);
        s3.Rows.Add(new List<Cell>());
        foreach (var g in byStation)
        {
            AddGroupSection(s3, $"机台 {g.Key}", g.Summary, 5);
            AddRankTable(s3, g.Ranks);
            s3.Rows.Add(new List<Cell>());
        }
        sheets.Add(s3);

        var s4 = new Sheet
        {
            Name = "明细清单",
            ColWidths = new List<double> { 14, 10, 12, 34, 16, 8, 10, 50, 46 },
        };
        AddTitle(s4, "测试明细清单", 9);
        s4.Rows.Add(new List<Cell>());
        string[] detHeaders = { "测试日期", "类别", "型号", "SN", "机台", "结果", "失败项数", "失败项列表(值/规格)", "文件名" };
        var hrow = new List<Cell>();
        foreach (var h in detHeaders) hrow.Add(Cell.T(h, S_HEADER));
        s4.Rows.Add(hrow);
        int zi = 0;
        foreach (var rec in records
            .OrderBy(r => r.TestDate, StringComparer.Ordinal)
            .ThenBy(r => r.Filename, StringComparer.Ordinal))
        {
            bool zebra = (zi++ % 2 == 1);
            int st = zebra ? S_ZEBRA_T : S_TEXT_C;
            int sn = zebra ? S_ZEBRA_N : S_NUM_C;
            var row = new List<Cell>();
            row.Add(Cell.T(rec.TestDate, st));
            row.Add(Cell.T(rec.Category, st));
            row.Add(Cell.T(rec.Model, st));
            row.Add(Cell.T(rec.Sn, zebra ? S_ZEBRA_T : S_NORMAL));
            row.Add(Cell.T(rec.Station, st));
            row.Add(Cell.T(rec.Result, st));
            row.Add(Cell.N(rec.FailItems.Count, sn));
            row.Add(Cell.T(string.Join(" | ", rec.FailItems.Select(f => f.ToDetail())), zebra ? S_ZEBRA_T : S_NORMAL));
            row.Add(Cell.T(rec.Filename, zebra ? S_ZEBRA_T : S_NORMAL));
            s4.Rows.Add(row);
        }
        sheets.Add(s4);

        FctShared.Xlsx.Write(path, sheets, Styles());
    }

    private static void AddTitle(Sheet sh, string text, int span)
    {
        int r = sh.Rows.Count;
        var row = new List<Cell>();
        row.Add(Cell.T(text, S_TITLE));
        for (int i = 1; i < span; i++) row.Add(Cell.T("", S_TITLE));
        sh.Rows.Add(row);
        sh.Merges.Add((r, 0, r, span - 1));
    }

    private static void AddSubtitle(Sheet sh, string text, int span)
    {
        int r = sh.Rows.Count;
        var row = new List<Cell>();
        row.Add(Cell.T(text, S_SUBTITLE));
        for (int i = 1; i < span; i++) row.Add(Cell.T("", S_SUBTITLE));
        sh.Rows.Add(row);
        sh.Merges.Add((r, 0, r, span - 1));
    }

    private static void AddSection(Sheet sh, string text, int span)
    {
        int r = sh.Rows.Count;
        var row = new List<Cell>();
        row.Add(Cell.T(text, S_SECTION));
        for (int i = 1; i < span; i++) row.Add(Cell.T("", S_SECTION));
        sh.Rows.Add(row);
        sh.Merges.Add((r, 0, r, span - 1));
    }

    private static void AddGroupSection(Sheet sh, string title, CsvExporter.Summary sum, int span)
    {
        int r = sh.Rows.Count;
        var text = $"{title}    ｜    FAIL {sum.Fail} 台    中断 {sum.Interrupted}    良率 {sum.Yield:F2}%";
        var row = new List<Cell>();
        row.Add(Cell.T(text, S_SECTION));
        for (int i = 1; i < span; i++) row.Add(Cell.T("", S_SECTION));
        sh.Rows.Add(row);
        sh.Merges.Add((r, 0, r, span - 1));
    }

    private static void AddKV(Sheet sh, string k, int v)
    {
        var row = new List<Cell>();
        row.Add(Cell.T(k, S_KEY));
        row.Add(Cell.N(v, S_VAL));
        sh.Rows.Add(row);
    }
    private static void AddKVd(Sheet sh, string k, double v)
    {
        var row = new List<Cell>();
        row.Add(Cell.T(k, S_KEY));
        row.Add(Cell.N(Math.Round(v, 2), S_NUM_C2 == 0 ? S_VAL : S_NUM_C2));
        sh.Rows.Add(row);
    }

    private static void AddRankTable(Sheet sh, List<CsvExporter.FailRank> ranks)
    {
        var hrow = new List<Cell>();
        foreach (var h in RankHeaders) hrow.Add(Cell.T(h, S_HEADER));
        sh.Rows.Add(hrow);

        int rank = 1;
        foreach (var r in ranks)
        {
            bool top = rank <= 3;
            bool zebra = !top && (rank % 2 == 0);
            int tStyle = top ? S_TOP_TEXT : (zebra ? S_ZEBRA_T : S_TEXT_C);
            int nStyle = top ? S_TOP_NUM  : (zebra ? S_ZEBRA_N : S_NUM_C);
            int n2Style = top ? S_TOP_NUM2 : (zebra ? S_ZEBRA_N2 : S_NUM_C2);

            var row = new List<Cell>();
            row.Add(Cell.N(rank, nStyle));
            row.Add(Cell.T(r.Item, top ? S_TOP_TEXT : (zebra ? S_ZEBRA_T : S_NORMAL)));
            row.Add(Cell.N(r.Count, nStyle));
            row.Add(Cell.N(r.AffectedUnits, nStyle));
            row.Add(Cell.N(Math.Round(r.Percent, 2), n2Style));
            row.Add(Cell.T(r.Values, zebra ? S_ZEBRA_T : S_NORMAL));
            row.Add(Cell.T(r.Limits, zebra ? S_ZEBRA_T : S_NORMAL));
            row.Add(Cell.T(r.Units, zebra ? S_ZEBRA_T : S_TEXT_C));
            sh.Rows.Add(row);
            rank++;
        }
    }

    internal static string Styles2() => Styles();

    private static string Styles()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        sb.Append("<fonts count=\"6\">");
        sb.Append("<font><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><b/><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><b/><sz val=\"16\"/><color rgb=\"FF1F4E79\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><sz val=\"10\"/><color rgb=\"FF808080\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FF1F4E79\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("</fonts>");

        sb.Append("<fills count=\"7\">");
        sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/></patternFill></fill>");
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/></patternFill></fill>");
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/></patternFill></fill>");
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFCE4E4\"/></patternFill></fill>");
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF7F9FC\"/></patternFill></fill>");
        sb.Append("</fills>");

        sb.Append("<borders count=\"2\">");
        sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
        sb.Append("<border>");
        sb.Append("<left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left>");
        sb.Append("<right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>");
        sb.Append("<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top>");
        sb.Append("<bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom>");
        sb.Append("<diagonal/></border>");
        sb.Append("</borders>");

        sb.Append("<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>");

        sb.Append("<cellXfs count=\"17\">");
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "left");
        Xf(sb, font: 3, fill: 0, border: 0, hAlign: "center");
        Xf(sb, font: 4, fill: 0, border: 0, hAlign: "center");
        Xf(sb, font: 2, fill: 2, border: 1, hAlign: "center");
        Xf(sb, font: 5, fill: 3, border: 1, hAlign: "left");
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "center");
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "center");
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "center", numFmt: 2);
        Xf(sb, font: 1, fill: 4, border: 1, hAlign: "right");
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "center");
        Xf(sb, font: 0, fill: 5, border: 1, hAlign: "center");
        Xf(sb, font: 0, fill: 5, border: 1, hAlign: "center");
        Xf(sb, font: 0, fill: 5, border: 1, hAlign: "center", numFmt: 2);
        Xf(sb, font: 0, fill: 6, border: 1, hAlign: "center");
        Xf(sb, font: 0, fill: 6, border: 1, hAlign: "center");
        Xf(sb, font: 0, fill: 6, border: 1, hAlign: "center", numFmt: 2);
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "left");
        sb.Append("</cellXfs>");

        sb.Append("</styleSheet>");
        return sb.ToString();
    }

    private static void Xf(StringBuilder sb, int font, int fill, int border, string hAlign, int numFmt = 0)
    {
        sb.Append("<xf ");
        if (numFmt > 0) sb.Append($"numFmtId=\"{numFmt}\" applyNumberFormat=\"1\" ");
        sb.Append($"fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" ");
        sb.Append("applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
        sb.Append($"<alignment horizontal=\"{hAlign}\" vertical=\"center\" wrapText=\"0\"/>");
        sb.Append("</xf>");
    }

}
