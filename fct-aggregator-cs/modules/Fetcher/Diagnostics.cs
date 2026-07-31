using System.Xml.Linq;

namespace FctFetcher;

/// <summary>
/// XML 结构诊断。
///
/// 用途：当某台机器上出现"XML 里明明有失败项，工具却没识别出来"时，
/// 在那台机器上跑 --diag，把输出发回来即可定位结构差异，
/// 不必靠猜生产环境的 XML 长什么样。
/// </summary>
public static class Diagnostics
{
    public static void DumpFile(string path, TextWriter w)
    {
        w.WriteLine(new string('=', 74));
        w.WriteLine($"文件: {path}");
        var fi = new FileInfo(path);
        w.WriteLine($"大小: {fi.Length} B");
        w.WriteLine(new string('=', 74));

        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (Exception e) { w.WriteLine($"[解析失败] {e.GetType().Name}: {e.Message}"); return; }

        var root = doc.Root;
        if (root == null) { w.WriteLine("[空文档]"); return; }

        w.WriteLine($"根元素: <{root.Name.LocalName}>");
        foreach (var a in root.Attributes())
            w.WriteLine($"    @{a.Name}={a.Value}");

        // 各元素 × STATUS 分布
        w.WriteLine();
        w.WriteLine("--- 元素 × STATUS 分布 ---");
        var stat = new Dictionary<string, int>();
        foreach (var e in root.DescendantsAndSelf())
        {
            var s = e.Attribute("STATUS")?.Value;
            if (s == null) continue;
            var k = $"<{e.Name.LocalName}> STATUS={s}";
            stat[k] = stat.GetValueOrDefault(k) + 1;
        }
        foreach (var kv in stat.OrderBy(x => x.Key))
            w.WriteLine($"  {kv.Key}: {kv.Value}");

        // 所有失败 GROUP 的层级与内部结构
        w.WriteLine();
        w.WriteLine("--- 失败 GROUP 明细（容器 / 叶子）---");
        foreach (var g in root.Descendants("GROUP"))
        {
            if ((g.Attribute("STATUS")?.Value ?? "") != "Failed") continue;
            int depth = g.Ancestors().Count();
            var subFailed = g.Elements("GROUP")
                             .Count(x => (x.Attribute("STATUS")?.Value ?? "") == "Failed");
            var tests = g.Elements("TEST").ToList();
            var failedTests = tests.Count(t => (t.Attribute("STATUS")?.Value ?? "") == "Failed");
            var kind = subFailed > 0 ? "容器" : "叶子(=测试项)";

            w.WriteLine($"  [{kind}] depth={depth} TYPE={g.Attribute("TYPE")?.Value ?? "-"} " +
                        $"NAME={g.Attribute("NAME")?.Value ?? "-"}");
            w.WriteLine($"      子GROUP(失败)={subFailed}  TEST总数={tests.Count}  TEST(失败)={failedTests}");
            var kids = g.Elements().GroupBy(x => x.Name.LocalName)
                        .Select(x => $"{x.Key}×{x.Count()}");
            w.WriteLine($"      直接子元素: {string.Join(", ", kids)}");
            foreach (var t in tests)
            {
                var attrs = string.Join(" ", t.Attributes().Select(a => $"{a.Name}={a.Value}"));
                w.WriteLine($"      <TEST> {Trunc(attrs, 150)}");
            }
        }

        // 所有失败 TEST（不论父节点）
        w.WriteLine();
        w.WriteLine("--- 所有失败 TEST（不论父节点位置）---");
        foreach (var t in root.Descendants("TEST"))
        {
            if ((t.Attribute("STATUS")?.Value ?? "") != "Failed") continue;
            var parent = t.Parent;
            w.WriteLine($"  父=<{parent?.Name.LocalName}> " +
                        $"父NAME={parent?.Attribute("NAME")?.Value ?? "-"}");
            w.WriteLine($"    NAME={t.Attribute("NAME")?.Value ?? "-"}  " +
                        $"VALUE={t.Attribute("VALUE")?.Value ?? "-"}  " +
                        $"LOLIM={t.Attribute("LOLIM")?.Value ?? "-"}  " +
                        $"HILIM={t.Attribute("HILIM")?.Value ?? "-"}");
        }

        // 本工具实际会识别出什么
        w.WriteLine();
        w.WriteLine("--- 本工具的识别结果 ---");
        var rec = Scanner.ParsePath(path, new[] { "Online", "Offline" });
        if (rec == null)
        {
            w.WriteLine("  [路径不合规] 需为 {Category}\\{型号E+7位}\\{yyyyMMdd}\\{文件名}");
            w.WriteLine("  -> 该文件不会被扫描到（这本身可能就是漏项原因）");
        }
        else
        {
            Scanner.ParseXmlPublic(rec, true);
            w.WriteLine($"  SN={rec.Sn}  站点={rec.Station}  USER={rec.User}  结果={rec.Result}");
            w.WriteLine($"  识别到失败项 {rec.FailItems.Count} 个:");
            foreach (var it in rec.FailItems)
                w.WriteLine($"    [{it.StepType}] {it}");
            if (rec.FailItems.Count == 0)
                w.WriteLine("    (无) —— 若上面 STATUS 分布里有 Failed，说明结构未被覆盖，请把本输出发回");
        }
        w.WriteLine();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
