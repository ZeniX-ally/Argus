using System.Text;

namespace FctAggregator;

/// <summary>
/// 统一 CSV 注入防护与 BOM（CWE-1236，规范 §2.4）。
/// 所有导出（FAIL 明细、维修记录、数据拉取）必须走此工具，保证口径一致。
/// 规则：首字符 = + - @ \t \r 时加单引号前缀；含逗号/引号/换行则双引号包裹、内部双引号翻倍。
/// 输出一律 UTF-8 BOM（Excel 直开不乱码）。
/// </summary>
public static class CsvUtil
{
    /// <summary>单字段转义（注入防护 + 引号包裹）。</summary>
    public static string Esc(string? v)
    {
        v ??= "";
        if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' ||
                             v[0] == '\t' || v[0] == '\r'))
            v = "'" + v;
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    /// <summary>写出 CSV 文件（带 BOM）。headers 与 rows 均为已按列排好的字符串数组。</summary>
    public static void Write(string path, string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Esc)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", r.Select(Esc)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>内存生成 CSV 字节（含 BOM），供 HTTP 响应直接发送。</summary>
    public static byte[] BuildBytes(string[] headers, IEnumerable<string[]> rows, string? title = null, string? exportTime = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(title)) sb.AppendLine(title);
        sb.AppendLine($"导出时间,{exportTime ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}");
        sb.AppendLine();
        sb.AppendLine(string.Join(",", headers.Select(Esc)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", r.Select(Esc)));
        var enc = new UTF8Encoding(true);
        return enc.GetBytes(sb.ToString());
    }

    /// <summary>直接生成 HTTP 用字节（无标题块，仅表头+数据，带 BOM）。</summary>
    public static byte[] BuildSimpleBytes(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Esc)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", r.Select(Esc)));
        var enc = new UTF8Encoding(true);
        var preamble = enc.GetPreamble();
        var content = enc.GetBytes(sb.ToString());
        var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes, 0);
        content.CopyTo(bytes, preamble.Length);
        return bytes;
    }
}
