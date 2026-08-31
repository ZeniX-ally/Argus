using System.Text;
using System.Text.Json;

namespace FctTdmsViewer;

/// <summary>
/// 导出结构快照(JSON)，用于与其他实现(如 Python npTDMS)交叉核对完整性。
/// 不只比组数，而是逐组逐通道比对名称、点数、类型 —— 数量对得上不代表没错漏。
/// </summary>
public static class JsonDumper
{
    public static string Dump(TdmsDoc doc, string path)
    {
        var groups = new List<object>();
        int gi = 0;
        foreach (var g in doc.Groups)
        {
            var chans = g.Channels.Select(c => new
            {
                name = c.Name,
                n = c.Count,
                dtype = MapDtype(c.DataType),
            }).ToList();
            groups.Add(new
            {
                idx = gi++,
                group = g.Name,
                nch = chans.Count,
                channels = chans,
            });
        }
        var json = JsonSerializer.Serialize(groups, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    /// <summary>把 .NET 类型名映射成 numpy 风格，便于与 Python 输出直接比对。</summary>
    private static string MapDtype(Type? t)
    {
        if (t == null) return "None";
        if (t == typeof(double)) return "float64";
        if (t == typeof(float)) return "float32";
        if (t == typeof(int)) return "int32";
        if (t == typeof(uint)) return "uint32";
        if (t == typeof(short)) return "int16";
        if (t == typeof(ushort)) return "uint16";
        if (t == typeof(long)) return "int64";
        if (t == typeof(ulong)) return "uint64";
        if (t == typeof(sbyte)) return "int8";
        if (t == typeof(byte)) return "uint8";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "str";
        if (t == typeof(DateTime)) return "datetime64";
        return t.Name;
    }
}
