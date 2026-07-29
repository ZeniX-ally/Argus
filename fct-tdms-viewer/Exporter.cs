using System.Globalization;
using System.Text;

namespace FctTdmsViewer;

/// <summary>导出为 CSV（UTF-8 BOM，Excel 可直接打开）。</summary>
public static class Exporter
{
    /// <summary>导出选中通道：第一列为时间/索引，其后每通道一列。长度不齐时以最长为准，短的留空。</summary>
    public static string ExportChannels(TdmsDoc doc, List<ChannelInfo> chans, string path)
    {
        var cols = new List<(string header, double[] data)>();
        double inc = 0;
        foreach (var c in chans)
        {
            var d = doc.GetData(c);
            if (d.Length == 0 && !c.Numeric) continue;
            cols.Add(($"{c.GroupName}/{c.Name}", d));
            if (inc == 0) inc = TdmsDoc.GetIncrement(c);
        }
        if (cols.Count == 0) throw new InvalidOperationException("选中的通道没有可导出的数值数据。");

        int rows = cols.Max(c => c.data.Length);
        var sb = new StringBuilder();
        sb.Append(inc > 0 ? "Time(s)" : "Index");
        foreach (var c in cols) sb.Append(',').Append(Esc(c.header));
        sb.AppendLine();

        for (int i = 0; i < rows; i++)
        {
            sb.Append(inc > 0
                ? (i * inc).ToString("0.####", CultureInfo.InvariantCulture)
                : i.ToString(CultureInfo.InvariantCulture));
            foreach (var c in cols)
            {
                sb.Append(',');
                if (i < c.data.Length)
                    sb.Append(c.data[i].ToString("R", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    /// <summary>导出整个文件的结构清单（组/通道/类型/点数/统计）。</summary>
    public static string ExportSummary(TdmsDoc doc, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Group,Channel,DataType,Count,Min,Max,Mean,Std,First,Last");
        foreach (var g in doc.Groups)
        {
            foreach (var c in g.Channels)
            {
                sb.Append(Esc(g.Name)).Append(',').Append(Esc(c.Name)).Append(',')
                  .Append(c.TypeName).Append(',').Append(c.Count);
                var st = c.Numeric ? TdmsDoc.Describe(doc.GetData(c)) : null;
                if (st == null) sb.Append(",,,,,,");
                else
                {
                    sb.Append(',').Append(F(st.Min)).Append(',').Append(F(st.Max))
                      .Append(',').Append(F(st.Mean)).Append(',').Append(F(st.Std))
                      .Append(',').Append(F(st.First)).Append(',').Append(F(st.Last));
                }
                sb.AppendLine();
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Esc(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
