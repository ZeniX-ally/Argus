using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace FctFetcher;

public static class Diagnostics
{
	public static void DumpFile(string path, TextWriter w)
	{
		w.WriteLine(new string('=', 74));
		w.WriteLine("文件: " + path);
		FileInfo fileInfo = new FileInfo(path);
		w.WriteLine($"大小: {fileInfo.Length} B");
		w.WriteLine(new string('=', 74));
		XDocument xDocument;
		try
		{
			xDocument = XDocument.Load(path);
		}
		catch (Exception ex)
		{
			w.WriteLine("[解析失败] " + ex.GetType().Name + ": " + ex.Message);
			return;
		}
		XElement? root = xDocument.Root;
		if (root == null)
		{
			w.WriteLine("[空文档]");
			return;
		}
		w.WriteLine("根元素: <" + root.Name.LocalName + ">");
		foreach (XAttribute item in root.Attributes())
		{
			w.WriteLine($"    @{item.Name}={item.Value}");
		}
		w.WriteLine();
		w.WriteLine("--- 元素 × STATUS 分布 ---");
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		foreach (XElement item2 in root.DescendantsAndSelf())
		{
			string? text = item2.Attribute("STATUS")?.Value;
			if (text != null)
			{
				string key = "<" + item2.Name.LocalName + "> STATUS=" + text;
				dictionary[key] = dictionary.GetValueOrDefault(key) + 1;
			}
		}
		foreach (KeyValuePair<string, int> item3 in dictionary.OrderBy((KeyValuePair<string, int> x) => x.Key))
		{
			w.WriteLine($"  {item3.Key}: {item3.Value}");
		}
		w.WriteLine();
		w.WriteLine("--- 失败 GROUP 明细（容器 / 叶子）---");
		foreach (XElement item4 in root.Descendants("GROUP"))
		{
			if ((item4.Attribute("STATUS")?.Value ?? "") != "Failed")
			{
				continue;
			}
			int value = item4.Ancestors().Count();
			int num = item4.Elements("GROUP").Count((XElement x) => (x.Attribute("STATUS")?.Value ?? "") == "Failed");
			List<XElement> list = item4.Elements("TEST").ToList();
			int value2 = list.Count((XElement t) => (t.Attribute("STATUS")?.Value ?? "") == "Failed");
			string value3 = ((num > 0) ? "容器" : "叶子(=测试项)");
			w.WriteLine($"  [{value3}] depth={value} TYPE={item4.Attribute("TYPE")?.Value ?? "-"} NAME={item4.Attribute("NAME")?.Value ?? "-"}");
			w.WriteLine($"      子GROUP(失败)={num}  TEST总数={list.Count}  TEST(失败)={value2}");
			IEnumerable<string> values = from x in item4.Elements()
				group x by x.Name.LocalName into x
				select $"{x.Key}×{x.Count()}";
			w.WriteLine("      直接子元素: " + string.Join(", ", values));
			foreach (XElement item5 in list)
			{
				string s = string.Join(" ", from a in item5.Attributes()
					select $"{a.Name}={a.Value}");
				w.WriteLine("      <TEST> " + Trunc(s, 150));
			}
		}
		w.WriteLine();
		w.WriteLine("--- 所有失败 TEST（不论父节点位置）---");
		foreach (XElement item6 in root.Descendants("TEST"))
		{
			if (!((item6.Attribute("STATUS")?.Value ?? "") != "Failed"))
			{
				XElement? parent = item6.Parent;
				w.WriteLine($"  父=<{parent?.Name.LocalName}> 父NAME={parent?.Attribute("NAME")?.Value ?? "-"}");
				w.WriteLine($"    NAME={item6.Attribute("NAME")?.Value ?? "-"}  VALUE={item6.Attribute("VALUE")?.Value ?? "-"}  LOLIM={item6.Attribute("LOLIM")?.Value ?? "-"}  HILIM={item6.Attribute("HILIM")?.Value ?? "-"}");
			}
		}
		w.WriteLine();
		w.WriteLine("--- 本工具的识别结果 ---");
		Record? record = Scanner.ParsePath(path, new string[2] { "Online", "Offline" });
		if (record == null)
		{
			w.WriteLine("  [路径不合规] 需为 {Category}\\{型号E+7位}\\{yyyyMMdd}\\{文件名}");
			w.WriteLine("  -> 该文件不会被扫描到（这本身可能就是漏项原因）");
		}
		else
		{
			Scanner.ParseXmlPublic(record, excludeIgnored: true);
			w.WriteLine($"  SN={record.Sn}  站点={record.Station}  USER={record.User}  结果={record.Result}");
			w.WriteLine($"  识别到失败项 {record.FailItems.Count} 个:");
			foreach (FailItem failItem in record.FailItems)
			{
				w.WriteLine($"    [{failItem.StepType}] {failItem}");
			}
			if (record.FailItems.Count == 0)
			{
				w.WriteLine("    (无) —— 若上面 STATUS 分布里有 Failed，说明结构未被覆盖，请把本输出发回");
			}
		}
		w.WriteLine();
	}

	private static string Trunc(string s, int n)
	{
		if (s.Length > n)
		{
			return s.Substring(0, n - 1) + "…";
		}
		return s;
	}
}
