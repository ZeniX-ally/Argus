using System.Globalization;
using System.Text;

namespace FctAggregator;

/// <summary>
/// FAIL 明细导出（xlsx / CSV）。转义一律走 <see cref="CsvUtil.Esc"/>（CWE-1236），不在本文件重实现。
/// 行序列由 <see cref="BuildRows"/> 统一生成，两种格式内容一致：
/// 按项目名分组 → 每组明细 → 小计行（失败次数 + 值均值）→ 空行 → 下一组。
/// 限值在文件里拆成「下限 / 上限 / 单位」三列，便于在 Excel 里排序筛选。
/// </summary>
public static class FailExporter
{
    public static readonly string[] Headers =
    {
        "型号", "SN", "测试Fail时间", "项目", "值", "下限", "上限", "单位",
    };

    /// <summary>导出行种类：小计行在 xlsx 里加粗，空行用于分组之间分隔。</summary>
    public enum FailRowKind { Detail, Subtotal, Blank }

    /// <summary>与 <see cref="Headers"/> 列数对齐的一行。</summary>
    public readonly record struct FailRow(FailRowKind Kind, string[] Cells);

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

    private static string NumOrEmpty(double? v) => v.HasValue ? FailItemDetail.Num(v.Value) : "";

    /// <summary>
    /// 生成导出用行序列。输入须已按项目名分组排序（<see cref="Database.QueryFailItemDetails"/> 的默认顺序）。
    /// 每组：明细若干行 → 小计行（项目列「小计：名」、值列 = 该组**数值**均值（整组无数值写「—」）、限值列「失败 N 次」）
    /// → 空行（最后一组之后不补）。
    /// </summary>
    public static List<FailRow> BuildRows(IEnumerable<FailItemDetail> rows)
    {
        var src = (rows as ICollection<FailItemDetail> ?? rows.ToList()).ToList();
        var outRows = new List<FailRow>(src.Count + 8);
        int i = 0;
        while (i < src.Count)
        {
            var name = src[i].TestName;
            int j = i, n = 0, numeric = 0;
            double sum = 0;
            while (j < src.Count && string.Equals(src[j].TestName, name, StringComparison.OrdinalIgnoreCase))
            {
                outRows.Add(new FailRow(FailRowKind.Detail, RowOf(src[j])));
                if (src[j].Value.HasValue) { sum += src[j].Value!.Value; numeric++; }
                n++;
                j++;
            }

            var cells = new string[Headers.Length];
            for (int c = 0; c < cells.Length; c++) cells[c] = "";
            // D4：小计行进「项目/值/限值」列（3/4/5），与文件头文档注释一致。
            // 原实现写进 0/1/2（型号/SN/时间列）——Excel 按列筛选会把均值当 SN、把失败次数当测试时间。
            cells[3] = $"小计：{name}";
            cells[4] = numeric > 0 ? FailItemDetail.Num(sum / numeric) : "—";
            cells[5] = $"失败 {n} 次";
            outRows.Add(new FailRow(FailRowKind.Subtotal, cells));

            if (j < src.Count) outRows.Add(new FailRow(FailRowKind.Blank, new string[Headers.Length]));
            i = j;
        }
        return outRows;
    }

    /// <summary>xlsx 列宽（字符）：按各列内容量级预设，免去打开后手动"自动调整列宽"。</summary>
    private static readonly double[] XlsxWidths = { 14, 24, 20, 46, 12, 10, 10, 8 };

    /// <summary>
    /// 导出 xlsx（手写 OOXML）：列宽已预设、首行表头加粗并冻结、**小计行加粗**、所有单元格按文本落表
    /// （时间与测项名不会被 Excel 乱解析）。返回**明细**行数（不含小计行与空行）。
    /// </summary>
    public static int ExportXlsx(string path, IEnumerable<FailItemDetail> rows)
    {
        var exportRows = BuildRows(rows);
        int dataCount = exportRows.Count(r => r.Kind == FailRowKind.Detail);

        var sh = new FctShared.Xlsx.Sheet { Name = "FAIL明细", FreezeRows = 1 };
        sh.ColWidths.AddRange(XlsxWidths);

        var head = new List<FctShared.Xlsx.Cell>(Headers.Length);
        foreach (var h in Headers) head.Add(FctShared.Xlsx.T(h, 1));
        sh.Rows.Add(head);

        foreach (var r in exportRows)
        {
            int style = r.Kind == FailRowKind.Subtotal ? 2 : 0;
            var row = new List<FctShared.Xlsx.Cell>(r.Cells.Length);
            foreach (var v in r.Cells) row.Add(FctShared.Xlsx.T(v, style));
            sh.Rows.Add(row);
        }

        FctShared.Xlsx.Write(path, new[] { sh }, FctShared.Xlsx.TableStyles());
        return dataCount;
    }

    /// <summary>按自然月一张表：<c>FAIL记录_yyyyMM.csv</c>，与库文件同目录。</summary>
    public static string MonthLogPath(string dbPath, int year, int month)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        return Path.Combine(dir, $"FAIL记录_{year:0000}{month:00}.csv");
    }

    /// <summary>
    /// 重写某月 FAIL 明细。列与手动导出一致，不写小计行。
    /// 排序：该月同一项目的失败次数从高到低，次数相同再按项目名、时间倒序。
    /// </summary>
    public static void RewriteMonth(string path, IReadOnlyList<FailItemDetail> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var name = r.TestName ?? "";
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        var ordered = rows
            .OrderByDescending(r => counts.GetValueOrDefault(r.TestName ?? ""))
            .ThenBy(r => r.TestName ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.Ts ?? "", StringComparer.Ordinal)
            .ToList();

        var lines = new List<string> { string.Join(",", Headers.Select(CsvUtil.Esc)) };
        foreach (var r in ordered)
            lines.Add(string.Join(",", RowOf(r).Select(FlatCell)));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(true));
        File.Move(tmp, path, overwrite: true);
    }

    private static string FlatCell(string? v)
    {
        v ??= "";
        if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            v = v.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return CsvUtil.Esc(v);
    }

    /// <summary>导出 CSV（UTF-8 BOM）；返回**明细**行数（不含小计行与空行）。</summary>
    public static int ExportCsv(
        string path, TimeUtil.NaturalPeriod period, DateTime from, DateTime toInclusive,
        IEnumerable<FailItemDetail> rows)
    {
        var exportRows = BuildRows(rows);
        int dataCount = exportRows.Count(r => r.Kind == FailRowKind.Detail);

        var sb = new StringBuilder();
        sb.AppendLine($"FAIL 明细导出（{TimeUtil.NaturalPeriodName(period)}）");
        sb.AppendLine($"范围,{from:yyyy-MM-dd} ~ {toInclusive:yyyy-MM-dd}");
        sb.AppendLine($"导出时间,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"明细行数,{dataCount}");
        sb.AppendLine();
        sb.AppendLine(string.Join(",", Headers.Select(CsvUtil.Esc)));
        foreach (var r in exportRows)
            sb.AppendLine(string.Join(",", r.Cells.Select(CsvUtil.Esc)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return dataCount;
    }
}
