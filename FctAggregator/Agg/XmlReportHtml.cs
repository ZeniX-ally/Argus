using System.Text;

namespace FctAggregator;

public static class XmlReportHtml
{
    private static readonly string[] IgnoredFailSteps = { "Get Unit Information", "UUT Status Err" };

    private static bool IsIgnored(string name)
    {
        foreach (var ig in IgnoredFailSteps)
            if (name.Contains(ig, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string Render(XmlParser.ReportData data, string fileName, string? rawUrl)
    {
        var pass = data.PanelStatus.Equals("Passed", StringComparison.OrdinalIgnoreCase);
        var badge = pass ? "PASS" : (string.IsNullOrEmpty(data.PanelStatus) ? "UNKNOWN" : data.PanelStatus.ToUpperInvariant());
        var badgeCls = pass ? "pass" : "fail";

        int total = data.Tests.Count;
        int failed = data.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !IsIgnored(t.Name));
        int ignored = data.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && IsIgnored(t.Name));
        int passed = data.Tests.Count(t => t.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase));

        var ts = data.BatchTimestamp ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"UTF-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("<title>测试报告 - " + Html(data.Sn.Length > 0 ? data.Sn : fileName) + "</title><style>");
        sb.AppendLine("body{background:#F7F7F7;color:#141414;font-family:'Microsoft YaHei UI',sans-serif;margin:0;padding:20px;font-size:13px}");
        sb.AppendLine(".wrap{max-width:1080px;margin:0 auto}");
        sb.AppendLine(".head{background:#fff;border:1px solid #E3E3E3;border-radius:12px;padding:16px 22px;position:relative;border-bottom:3px solid " + (pass ? "#141414" : "#C8102E") + "}");
        sb.AppendLine(".kicker{color:#8C8C8C;font-size:12px;margin-bottom:6px}");
        sb.AppendLine(".sn{font-family:Consolas,monospace;font-size:22px;font-weight:500;word-break:break-all;padding-right:120px}");
        sb.AppendLine(".badge{position:absolute;right:20px;top:18px;padding:8px 16px;border-radius:10px;font-size:15px;font-weight:500;border:1.5px solid}");
        sb.AppendLine(".badge.pass{color:#141414;border-color:#141414;background:rgba(20,20,20,.05)}");
        sb.AppendLine(".badge.fail{color:#C8102E;border-color:#C8102E;background:#FCEBEB}");
        sb.AppendLine(".kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:16px 0}");
        sb.AppendLine(".kpi{background:#fff;border:1px solid #E3E3E3;border-radius:10px;padding:12px 16px;position:relative;overflow:hidden}");
        sb.AppendLine(".kpi b{position:absolute;left:0;top:10px;bottom:10px;width:4px;border-radius:2px}");
        sb.AppendLine(".kpi .v{font-size:24px;font-weight:500;margin-bottom:2px}");
        sb.AppendLine(".kpi .k{color:#8C8C8C;font-size:12px}");
        sb.AppendLine(".c-red{color:#C8102E}.c-ink{color:#141414}.c-dim{color:#8C8C8C}");
        sb.AppendLine(".card{background:#fff;border:1px solid #E3E3E3;border-radius:12px;padding:16px 22px;margin-bottom:16px}");
        sb.AppendLine(".grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:8px 24px;font-size:12px}");
        sb.AppendLine(".grid .k{color:#8C8C8C}.grid .v{font-weight:500;word-break:break-all}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;font-size:12px;font-family:Consolas,'NSimSun',monospace}");
        sb.AppendLine("th{text-align:left;color:#C8102E;font-weight:500;padding:8px;border-bottom:1px solid #E3E3E3;white-space:nowrap}");
        sb.AppendLine("td{padding:7px 8px;border-bottom:1px solid #F0F0F0;word-break:break-all}");
        sb.AppendLine("tr.fail td{color:#C8102E}");
        sb.AppendLine("tr.ign td{color:#8C8C8C}");
        sb.AppendLine("tr:hover td{background:#FAFAFA}");
        sb.AppendLine(".status{white-space:nowrap;font-weight:500}");
        sb.AppendLine(".st-fail{color:#C8102E}.st-ign{color:#8C8C8C}.st-pass{color:#141414}");
        sb.AppendLine(".bar{background:#fff;border:1px solid #E3E3E3;border-radius:10px;padding:10px 22px;margin-bottom:16px;font-size:12px;color:#8C8C8C;display:flex;gap:18px;flex-wrap:wrap}");
        sb.AppendLine(".bar a{color:#C8102E;text-decoration:none}.bar a:hover{text-decoration:underline}");
        sb.AppendLine("@media(max-width:640px){.sn{padding-right:0}.badge{position:static;display:inline-block;margin-top:8px}}");
        sb.AppendLine("</style></head><body><div class=\"wrap\">");

        sb.AppendLine("<div class=\"head\"><div class=\"kicker\">FCT 测试报告</div>");
        sb.AppendLine("<div class=\"sn\">" + Html(data.Sn.Length > 0 ? data.Sn : fileName) + "</div>");
        sb.AppendLine("<div class=\"badge " + badgeCls + "\">● " + Html(badge) + "</div></div>");

        sb.AppendLine("<div class=\"kpis\">");
        sb.AppendLine(KpiCard("测试项总数", total.ToString(), "#C8102E"));
        sb.AppendLine(KpiCard("失败（计入不良）", failed.ToString(), failed > 0 ? "#C8102E" : "#141414"));
        sb.AppendLine(KpiCard("排除项", ignored.ToString(), "#8C8C8C"));
        sb.AppendLine(KpiCard("通过项", passed.ToString(), "#141414"));
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"card\"><div class=\"grid\">");
        sb.AppendLine(Info("机台 TESTER", data.Tester));
        sb.AppendLine(Info("操作模式", data.FactoryUser));
        sb.AppendLine(Info("测试时间", ts));
        sb.AppendLine(Info("整体状态", data.PanelStatus));
        sb.AppendLine(Info("文件名", fileName));
        sb.AppendLine(Info("SN", data.Sn));
        sb.AppendLine("</div></div>");

        sb.AppendLine("<div class=\"card\"><h2 style=\"font-size:14px;font-weight:500;margin:0 0 10px\">测试项明细</h2>");
        sb.AppendLine("<table><thead><tr><th>#</th><th>测试项</th><th>测量值</th><th>下限</th><th>上限</th><th>单位</th><th>状态</th></tr></thead><tbody>");
        int idx = 1;
        foreach (var t in data.Tests)
        {
            bool isFail = t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
            bool ign = isFail && IsIgnored(t.Name);
            string status = isFail ? (ign ? "排除·不计入不良" : "FAILED")
                                   : (string.IsNullOrEmpty(t.Status) ? "-" : t.Status.ToUpperInvariant());
            string cls = isFail ? (ign ? "ign" : "fail") : "";
            string stCls = isFail ? (ign ? "st-ign" : "st-fail") : "st-pass";
            sb.AppendLine("<tr class=\"" + cls + "\"><td>" + idx + "</td><td>" + Html(t.Name) + "</td>"
                + "<td>" + Html(Val(t.Value)) + "</td><td>" + Html(Val(t.Lolim)) + "</td><td>" + Html(Val(t.Hilim)) + "</td>"
                + "<td>" + Html(Val(t.Unit)) + "</td><td class=\"status " + stCls + "\">" + Html(status) + "</td></tr>");
            idx++;
        }
        sb.AppendLine("</tbody></table></div>");

        sb.AppendLine("<div class=\"bar\"><span>在线查看 · 口径与本地查看器一致</span>");
        if (!string.IsNullOrEmpty(rawUrl))
            sb.AppendLine("<a href=\"" + Html(rawUrl) + "\">查看原始 XML</a>");
        sb.AppendLine("<a href=\"javascript:history.back()\">返回列表</a></div>");

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static string KpiCard(string k, string v, string color)
    {
        return "<div class=\"kpi\"><b style=\"background:" + color + "\"></b>"
            + "<div class=\"v\" style=\"color:" + color + "\">" + Html(v) + "</div>"
            + "<div class=\"k\">" + Html(k) + "</div></div>";
    }

    private static string Info(string k, string v)
    {
        return "<div><div class=\"k\">" + Html(k) + "</div><div class=\"v\">" + Html(string.IsNullOrEmpty(v) ? "—" : v) + "</div></div>";
    }

    private static string Val(string s) => string.IsNullOrEmpty(s) ? "-" : s;

    private static string Html(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
    }
}
