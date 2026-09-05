namespace FctAggregator;

public static class LayoutAdvisor
{
    public static string[] DefaultOrder(string role)
    {
        role = (role ?? "").ToLowerInvariant();
        if(role=="viewer") return new[] { "overview","fails","yield","devices","maintenance" };
        if(role=="engineer") return new[] { "overview","fails","maintenance","devices","yield" };
        return new[] { "overview","fails","yield","maintenance","devices","report","proc","alerts","compare","settings","fetch","xml" };
    }

    public static List<string> SuggestOrder(Dictionary<string,int> freq, string role)
    {
        var def = DefaultOrder(role);
        var ranked = freq.Where(kv=> kv.Value>0).OrderByDescending(kv=> kv.Value).Select(kv=> kv.Key).ToList();
        var set = new HashSet<string>(ranked, StringComparer.OrdinalIgnoreCase);
        foreach(var d in def) if(!set.Contains(d)) ranked.Add(d);
        var known = new HashSet<string>(def, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(new[] {"overview","fails","yield","xml","maintenance","devices","fetch","report","proc","alerts","compare","settings"});
        var result = new List<string>();
        foreach(var r in ranked) if(known.Contains(r) && !result.Contains(r, StringComparer.OrdinalIgnoreCase)) result.Add(r);
        foreach(var k in known) if(!result.Contains(k, StringComparer.OrdinalIgnoreCase)) result.Add(k);
        return result;
    }

    public static Dictionary<string,int> ParseFreq(string json)
    {
        try{
            if(string.IsNullOrWhiteSpace(json)) return new Dictionary<string,int>();
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,int>>(json);
            return dict ?? new Dictionary<string,int>();
        } catch{ return new Dictionary<string,int>();}
    }
}
