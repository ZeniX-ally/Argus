namespace FctAggregator;

/// <summary>域8 布局智能 — 访问频次 + 角色差异化（v3.19.0 A）。前端 localStorage 为主，后端 users.layout 双写兜底。</summary>
public static class LayoutAdvisor
{
    // 角色默认顺序
    public static string[] DefaultOrder(string role)
    {
        role = (role ?? "").ToLowerInvariant();
        if(role=="viewer") return new[] { "overview","fails","yield","devices","maintenance" };
        if(role=="engineer") return new[] { "overview","fails","maintenance","devices","yield" };
        return new[] { "overview","fails","yield","maintenance","devices","report","proc","alerts","compare","settings","fetch","xml" };
    }

    // 根据访问频次排序：freq {page:count} -> 按 count 降序，其余按默认顺序追加
    public static List<string> SuggestOrder(Dictionary<string,int> freq, string role)
    {
        var def = DefaultOrder(role);
        var ranked = freq.Where(kv=> kv.Value>0).OrderByDescending(kv=> kv.Value).Select(kv=> kv.Key).ToList();
        var set = new HashSet<string>(ranked, StringComparer.OrdinalIgnoreCase);
        foreach(var d in def) if(!set.Contains(d)) ranked.Add(d);
        // 保留已知路由，其余追加
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
