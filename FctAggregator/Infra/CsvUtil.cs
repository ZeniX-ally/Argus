using System.Text;

namespace FctAggregator;

public static class CsvUtil
{
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

    public static void Write(string path, string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Esc)));
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", r.Select(Esc)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

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
