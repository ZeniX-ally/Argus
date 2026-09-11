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

    /// <summary>导出当前周期的 FAIL 明细；返回写入的行数。</summary>
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
