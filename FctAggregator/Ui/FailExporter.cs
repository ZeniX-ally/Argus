using System.Globalization;
using System.Text;

namespace FctAggregator;

/// <summary>
/// FAIL 明细导出（CSV / UTF-8 BOM）。
/// 转义一律走 <see cref="CsvUtil.Esc"/>（CWE-1236 公式注入防护），不在本文件重实现。
/// 限值在 CSV 里拆成「下限 / 上限 / 单位」三列，便于在 Excel 里排序与筛选。
/// </summary>
public static class FailExporter
{
    public static readonly string[] Headers =
    {
        "型号", "SN", "测试Fail时间", "项目", "值", "下限", "上限", "单位",
    };

    public static string[] RowOf(FailItemDetail d) => new[]
    {
        d.Model,
        d.Sn,
        d.Ts,
        d.TestName,
        d.ValueDisplay,
        NumOrEmpty(d.LoLim),
        NumOrEmpty(d.HiLim),
        d.Unit,
    };

    private static string NumOrEmpty(double? v)
        => v.HasValue ? v.Value.ToString("0.####", CultureInfo.InvariantCulture) : "";

    /// <summary>xlsx 列宽（字符）：按各列内容量级预设，免去打开后手动"自动调整列宽"。</summary>
    private static readonly double[] XlsxWidths = { 14, 24, 20, 46, 12, 10, 10, 8 };

    /// <summary>
    /// 导出 xlsx（手写 OOXML）：**列宽已按内容调好**、首行表头加粗并冻结、所有单元格按文本落表
    /// （避免 Excel 把时间/测项名自作主张解析成日期或数字）。返回写入的数据行数（不含表头）。
    /// </summary>
    public static int ExportXlsx(string path, IEnumerable<FailItemDetail> rows)
    {
        var list = rows as ICollection<FailItemDetail> ?? rows.ToList();
        var sh = new FctShared.Xlsx.Sheet { Name = "FAIL明细", FreezeRows = 1 };
        sh.ColWidths.AddRange(XlsxWidths);

        var head = new List<FctShared.Xlsx.Cell>(Headers.Length);
        foreach (var h in Headers) head.Add(FctShared.Xlsx.T(h, 1));
        sh.Rows.Add(head);

        foreach (var d in list)
        {
            var vals = RowOf(d);
            var row = new List<FctShared.Xlsx.Cell>(vals.Length);
            foreach (var v in vals) row.Add(FctShared.Xlsx.T(v, 0));
            sh.Rows.Add(row);
        }

        FctShared.Xlsx.Write(path, new[] { sh }, FctShared.Xlsx.TableStyles());
        return list.Count;
    }

    /// <summary>导出当前周期的 FAIL 明细（CSV）；返回写入的行数。</summary>
    public static int ExportCsv(
        string path, TimeUtil.NaturalPeriod period, DateTime from, DateTime toInclusive,
        IEnumerable<FailItemDetail> rows)
    {
        var list = rows as ICollection<FailItemDetail> ?? rows.ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"FAIL 明细导出（{TimeUtil.NaturalPeriodName(period)}）");
        sb.AppendLine($"范围,{from:yyyy-MM-dd} ~ {toInclusive:yyyy-MM-dd}");
        sb.AppendLine($"导出时间,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"明细行数,{list.Count}");
        sb.AppendLine();
        sb.AppendLine(string.Join(",", Headers.Select(CsvUtil.Esc)));
        foreach (var d in list)
            sb.AppendLine(string.Join(",", RowOf(d).Select(CsvUtil.Esc)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return list.Count;
    }
}
