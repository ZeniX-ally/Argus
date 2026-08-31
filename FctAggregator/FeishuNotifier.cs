using System.Text;
using System.Text.Json;

namespace FctAggregator;

/// <summary>飞书群机器人 FAIL 告警推送。</summary>
public static class FeishuNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const int MaxRetries = 3;

    public static async Task SendFailAlert(string webhookUrl, TestRecord record)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        await PostCard(webhookUrl, BuildFailCard(record), record.Sn ?? "?");
    }

    /// <summary>
    /// 待办/维修状态变更推送。卡片格式与 FAIL 告警一致（header 机台·型号 + 对称两列 + note）。
    /// </summary>
    public static async Task SendStatusChangeAlert(string webhookUrl, MaintenanceRecord rec, string fromStatus, string toStatus)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        await PostCard(webhookUrl, BuildStatusChangeCard(rec, fromStatus, toStatus), $"#{rec.Id} {rec.FailItem}");
    }

    /// <summary>
    /// 聚合链路告警（v3.4.11 连通性加固）：机台端在聚合端断连/恢复/队列溢出时推送。
    /// kind: disconnected=断连（红）/ recovered=恢复（绿）/ overflow=队列溢出（琥珀）。
    /// 走机台自己的 webhook，聚合端挂掉时告警依然能发出（聚合端发的离线告警在此场景不可用）。
    /// </summary>
    public static async Task SendAggLinkAlert(string webhookUrl, string machine, string kind, string detail)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        var (title, template) = kind switch
        {
            "recovered" => ($"聚合链路已恢复 · {machine}", "green"),
            "overflow" => ($"聚合推送队列溢出 · {machine}", "amber"),
            _ => ($"聚合链路断连 · {machine}", "red"),
        };
        var card = new
        {
            header = new { title = new { tag = "plain_text", content = title }, template },
            elements = new object[]
            {
                new
                {
                    tag = "div",
                    fields = new object[] { Field("机台", machine) },
                },
                new
                {
                    tag = "markdown",
                    content = $"**{Escape(detail)}**\n请检查机台与聚合端之间的网络 / 聚合端服务状态。",
                },
                new
                {
                    tag = "note",
                    elements = new object[] { new { tag = "plain_text", content = $"Argus 链路告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}" } },
                },
            },
        };
        await PostCard(webhookUrl, card, $"link-{kind}-{machine}");
    }

    /// <summary>卡片 POST + 重试（两种推送共用）。tag 仅用于最终失败日志定位。</summary>
    private static async Task PostCard(string webhookUrl, object card, string tag)
    {
        // 飞书机器人只支持 https；若误配成 http:// 会明文泄露 webhook token（含密钥的完整 URL）
        if (!webhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Error($"[飞书推送] 拒绝发送: webhook 必须 https://（当前前缀 {webhookUrl[..Math.Min(24, webhookUrl.Length)]}…）| {tag}");
            return;
        }
        var payload = new { msg_type = "interactive", card };
        var json = JsonSerializer.Serialize(payload);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(webhookUrl, content);
                if (resp.IsSuccessStatusCode) return;
                Logger.Warning($"飞书推送失败(尝试{attempt + 1}/{MaxRetries}): HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"飞书推送异常(尝试{attempt + 1}/{MaxRetries}): {ex.Message}");
            }
            await Task.Delay(1000 * (attempt + 1));
        }
        Logger.Error($"飞书推送最终失败: {tag}");
    }

    /// <summary>
    /// 交互卡片：机台/型号前置到 header，正文用 <c>div.fields</c> 两列等宽对称布局。
    /// 失败项不再用 6 列表格（手机窄屏会被挤/截断），改为每项一行统一格式。
    /// </summary>
    private static object BuildFailCard(TestRecord r)
    {
        var station = string.IsNullOrWhiteSpace(r.StationId) ? "未知机台" : r.StationId;
        var model = string.IsNullOrWhiteSpace(r.Model) ? "—" : r.Model;

        var header = new
        {
            title = new { tag = "plain_text", content = $"{station} · {model} · FAIL 告警" },
            template = "red",
        };

        var elements = new List<object>
        {
            // 对称两列（is_short=true 每个占一半宽度）：机台 | 型号
            new
            {
                tag = "div",
                fields = new object[] { Field("机台", station), Field("型号", model) },
            },
            // 位置 | 时间
            new
            {
                tag = "div",
                fields = new object[] { Field("位置", r.Category), Field("时间", FmtTime(r.BatchTimestamp)) },
            },
            // SN 长，占满整行（is_short=false）
            new
            {
                tag = "div",
                fields = new object[] { Field("产品 SN", r.Sn ?? "—", fullWidth: true) },
            },
        };

        if (r.FailedTests.Count > 0)
            elements.Add(new { tag = "markdown", content = BuildFailItems(r) });
        else if (!string.IsNullOrWhiteSpace(r.FailReason))
            elements.Add(new { tag = "markdown", content = $"**失败项**\n{Escape(r.FailReason)}" });

        elements.Add(new
        {
            tag = "note",
            elements = new object[] { new { tag = "plain_text", content = $"文件 {Path.GetFileName(r.XmlPath)}" } },
        });

        return new { header, elements };
    }

    /// <summary>
    /// 待办状态变更卡片：结构照搬 FAIL 告警（header 机台·型号 + 对称两列 + 变更行 + note），
    /// 只把「失败项」换成「状态变化」+ 故障项 + 维修人/措施。
    /// </summary>
    private static object BuildStatusChangeCard(MaintenanceRecord r, string fromStatus, string toStatus)
    {
        var station = string.IsNullOrWhiteSpace(r.StationId) ? "未知机台" : r.StationId;
        var model = string.IsNullOrWhiteSpace(r.EquipmentModel) ? "—" : r.EquipmentModel;
        var fromZh = string.IsNullOrEmpty(fromStatus) ? "新登记" : MaintenanceMeta.ZhOf(fromStatus);
        var toZh = MaintenanceMeta.ZhOf(toStatus);

        var header = new
        {
            title = new { tag = "plain_text", content = $"{station} · {model} · 待办状态变更" },
            template = "blue",
        };

        var elements = new List<object>
        {
            // 对称两列：机台 | 型号
            new
            {
                tag = "div",
                fields = new object[] { Field("机台", station), Field("型号", model) },
            },
            // 状态变更（旧 → 新）| 严重度
            new
            {
                tag = "div",
                fields = new object[] { Field("状态变更", $"{fromZh} → {toZh}"), Field("严重度", MaintenanceMeta.SeverityZhOf(r.Severity)) },
            },
            // 故障项 / SN 整行
            new
            {
                tag = "div",
                fields = new object[] { Field("故障项", string.IsNullOrWhiteSpace(r.FailItem) ? "—" : r.FailItem, fullWidth: true) },
            },
        };

        if (!string.IsNullOrWhiteSpace(r.EquipmentSn))
            elements.Add(new { tag = "div", fields = new object[] { Field("产品 SN", r.EquipmentSn, fullWidth: true) } });

        if (!string.IsNullOrWhiteSpace(r.Resolver) || !string.IsNullOrWhiteSpace(r.Resolution))
        {
            var sb = new StringBuilder("**处理信息**\n");
            if (!string.IsNullOrWhiteSpace(r.Resolver)) sb.AppendLine($"维修人: {Escape(r.Resolver)}");
            if (!string.IsNullOrWhiteSpace(r.Resolution)) sb.AppendLine($"措施: {Escape(r.Resolution)}");
            elements.Add(new { tag = "markdown", content = sb.ToString().TrimEnd() });
        }

        elements.Add(new
        {
            tag = "note",
            elements = new object[] { new { tag = "plain_text", content = $"记录 #{r.Id} · 更新时间 {FmtTime(r.UpdatedAt)}" } },
        });

        return new { header, elements };
    }

    /// <summary>对称字段：粗体标签 + 值。fullWidth=true 时占满整行。</summary>
    private static object Field(string label, string value, bool fullWidth = false) => new
    {
        is_short = !fullWidth,
        text = new { tag = "lark_md", content = $"**{label}**\n{Escape(value)}" },
    };

    /// <summary>失败项每行统一格式：▸ 名称 = 实测值（规格 下限 ~ 上限）。</summary>
    private static string BuildFailItems(TestRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**失败项 ×{r.FailedTests.Count}**");
        sb.AppendLine();
        foreach (var t in r.FailedTests)
        {
            var val = string.IsNullOrWhiteSpace(t.Value) ? "" : $" = {t.Value}{t.Unit}";
            var spec = string.IsNullOrWhiteSpace(t.Lolim) && string.IsNullOrWhiteSpace(t.Hilim)
                ? "" : $"（规格 {t.Lolim} ~ {t.Hilim}）";
            sb.AppendLine($"▸ {Escape(t.Name)}{val} {spec}".Trim());
        }
        return sb.ToString();
    }

    /// <summary>时间统一格式（ISO 带时区/毫秒、14/17 位数字都能识别）。空 → "—"。</summary>
    private static string FmtTime(string? ts)
    {
        var n = TimeUtil.Normalize(ts);
        return n.Length == 0 ? "—" : n;
    }

    /// <summary>飞书 lark_md 转义：粗体符号、竖线、链接方括号与反引号代码块（避免恶意测试项名/失败原因渲染成可点击链接）。</summary>
    private static string Escape(string? s)
    {
        s ??= "";
        return s.Replace("*", "\\*").Replace("|", "\\|")
                .Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
    }
}
