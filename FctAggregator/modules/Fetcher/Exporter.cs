using Cell = FctShared.Xlsx.Cell;
using Sheet = FctShared.Xlsx.Sheet;

namespace FctFetcher;

public static class Exporter
{
    public static string Export(List<Record> recs, Config cfg, DateTime start, DateTime end,
                                string outDir)
    {
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"fetch_{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx");

        var sheets = new List<Sheet>
        {
            BuildMain(recs),
            BuildDetail(recs),
            BuildRank(recs),
        };
        XlsxWriter.Write(path, sheets);
        return path;
    }

    private static Sheet BuildMain(List<Record> recs)
    {
        var sh = new Sheet { Name = "捞取清单" };
        string[] hd = { "SN", "结果", "站点", "型号", "日期", "类别", "前缀", "失败项数",
                        "失败项", "CSV", "TDMS数", "TDMS", "XML", "测试时间", "USER" };
        sh.ColWidths.AddRange(new double[] { 34, 8, 8, 11, 11, 9, 7, 9, 52, 46, 9, 52, 60, 24, 10 });
        sh.Rows.Add(hd.Select(XlsxWriter.H).ToList());

        foreach (var r in recs)
        {
            sh.Rows.Add(new List<Cell>
            {
                XlsxWriter.T(r.Sn, XlsxWriter.S_TEXT_C),
                XlsxWriter.T(r.Result, XlsxWriter.S_TEXT_C),
                XlsxWriter.T(r.Station, XlsxWriter.S_TEXT_C),
                XlsxWriter.T(r.Model, XlsxWriter.S_TEXT_C),
                XlsxWriter.T(r.Date, XlsxWriter.S_TEXT_C),
                XlsxWriter.T(r.Category, XlsxWriter.S_TEXT_C),
                XlsxWriter.T(r.Prefix, XlsxWriter.S_TEXT_C),
                XlsxWriter.N(r.FailItems.Count),
                XlsxWriter.T(string.Join(" | ", r.FailItems.Select(x => x.ToString()))),
                XlsxWriter.T(r.CsvPath),
                XlsxWriter.N(r.TdmsPaths.Count),
                XlsxWriter.T(string.Join(" | ", r.TdmsPaths)),
                XlsxWriter.T(r.XmlPath),
                XlsxWriter.T(r.Timestamp, XlsxWriter.S_TEXT_C),
                XlsxWriter.T(r.User, XlsxWriter.S_TEXT_C),
            });
        }
        return sh;
    }

    private static Sheet BuildDetail(List<Record> recs)
    {
        var sh = new Sheet { Name = "失败项明细" };
        string[] hd = { "SN", "站点", "日期", "失败项", "测量值", "下限", "上限", "单位" };
        sh.ColWidths.AddRange(new double[] { 34, 8, 11, 46, 16, 12, 12, 8 });
        sh.Rows.Add(hd.Select(XlsxWriter.H).ToList());

        foreach (var r in recs)
            foreach (var it in r.FailItems)
                sh.Rows.Add(new List<Cell>
                {
                    XlsxWriter.T(r.Sn, XlsxWriter.S_TEXT_C),
                    XlsxWriter.T(r.Station, XlsxWriter.S_TEXT_C),
                    XlsxWriter.T(r.Date, XlsxWriter.S_TEXT_C),
                    XlsxWriter.T(it.Name),
                    XlsxWriter.T(it.Value, XlsxWriter.S_TEXT_C),
                    XlsxWriter.T(it.Lolim, XlsxWriter.S_TEXT_C),
                    XlsxWriter.T(it.Hilim, XlsxWriter.S_TEXT_C),
                    XlsxWriter.T(it.Unit, XlsxWriter.S_TEXT_C),
                });
        return sh;
    }

    private static Sheet BuildRank(List<Record> recs)
    {
        var sh = new Sheet { Name = "失败项排名" };
        string[] hd = { "排名", "失败项", "次数", "影响SN数" };
        sh.ColWidths.AddRange(new double[] { 8, 52, 10, 12 });
        sh.Rows.Add(hd.Select(XlsxWriter.H).ToList());

        var ranks = recs
            .SelectMany(r => r.FailItems.Select(i => new { i.Name, r.Sn }))
            .GroupBy(x => x.Name)
            .Select(g => new { Name = g.Key, Count = g.Count(), Sns = g.Select(x => x.Sn).Distinct().Count() })
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Sns)
            .ToList();

        int rank = 1;
        foreach (var x in ranks)
            sh.Rows.Add(new List<Cell>
            {
                XlsxWriter.N(rank++),
                XlsxWriter.T(x.Name),
                XlsxWriter.N(x.Count),
                XlsxWriter.N(x.Sns),
            });
        return sh;
    }
}
