using System.Text;
using System.Text.Json;

namespace FctTdmsViewer;

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
