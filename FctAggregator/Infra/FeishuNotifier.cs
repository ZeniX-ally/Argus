using System.Text;
using System.Text.Json;

namespace FctAggregator;

public static class FeishuNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const int MaxRetries = 3;

    public static async Task SendFailAlert(string webhookUrl, TestRecord record)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        await PostCard(webhookUrl, BuildFailCard(record), record.Sn ?? "?");
    }

    public static async Task SendStatusChangeAlert(string webhookUrl, MaintenanceRecord rec, string fromStatus, string toStatus)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        await PostCard(webhookUrl, BuildStatusChangeCard(rec, fromStatus, toStatus), $"#{rec.Id} {rec.FailItem}");
    }

    public static async Task SendAggLinkAlert(string webhookUrl, string machine, string kind, string detail)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        var (title, template) = kind switch
        {
            "recovered" => ($"聚合链路已恢复 · {machine}", "green"),
            "overflow" => ($"聚合推送队列溢出 · {machine}", "yellow"),
            _ => ($"聚合链路断连 · {machine}", "red"),
        };
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", machine)),
            FeishuCardV2.Md($"**{FeishuCardV2.Escape(detail)}**\n请检查机台与聚合端之间的网络 / 聚合端服务状态。"),
            FeishuCardV2.Hr(),
            FeishuCardV2.Note($"Argus 链路告警 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"),
        };
        var card = FeishuCardV2.Root(title, template, elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
        await PostCard(webhookUrl, card, $"link-{kind}-{machine}");
    }

    private static async Task PostCard(string webhookUrl, object card, string tag)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            webhookUrl = AppConfig.FallbackWebhookUrl;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Logger.Info($"[飞书推送] 未配置 webhook_url，跳过推送 | {tag}");
            return;
        }
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

    private static object BuildFailCard(TestRecord r)
    {
        var station = string.IsNullOrWhiteSpace(r.StationId) ? "未知机台" : r.StationId;
        var model = string.IsNullOrWhiteSpace(r.Model) ? "—" : r.Model;

        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", station), ("型号", model)),
            FeishuCardV2.FieldRow(("位置", r.Category), ("时间", FmtTime(r.BatchTimestamp))),
            FeishuCardV2.Md($"**产品 SN**\n{FeishuCardV2.Escape(r.Sn ?? "—")}"),
        };

        if (r.FailedTests.Count > 0)
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md($"**失败项 ×{r.FailedTests.Count}**", heading: true));
            elements.Add(FeishuCardV2.Md(BuildFailItems(r)));
        }
        else if (!string.IsNullOrWhiteSpace(r.FailReason))
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md($"**失败项**\n{FeishuCardV2.Escape(r.FailReason)}"));
        }

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note($"文件 {Path.GetFileName(r.XmlPath)}"));

        return FeishuCardV2.Root($"{station} · {model} · FAIL 告警", "red", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    private static object BuildStatusChangeCard(MaintenanceRecord r, string fromStatus, string toStatus)
    {
        var station = string.IsNullOrWhiteSpace(r.StationId) ? "未知机台" : r.StationId;
        var model = string.IsNullOrWhiteSpace(r.EquipmentModel) ? "—" : r.EquipmentModel;
        var fromZh = string.IsNullOrEmpty(fromStatus) ? "新登记" : MaintenanceMeta.ZhOf(fromStatus);
        var toZh = MaintenanceMeta.ZhOf(toStatus);

        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", station), ("型号", model)),
            FeishuCardV2.FieldRow(("状态变更", $"{fromZh} → {toZh}"), ("严重度", MaintenanceMeta.SeverityZhOf(r.Severity))),
            FeishuCardV2.Md($"**故障项**\n{FeishuCardV2.Escape(string.IsNullOrWhiteSpace(r.FailItem) ? "—" : r.FailItem)}"),
        };

        if (!string.IsNullOrWhiteSpace(r.EquipmentSn))
            elements.Add(FeishuCardV2.Md($"**产品 SN**\n{FeishuCardV2.Escape(r.EquipmentSn)}"));

        if (!string.IsNullOrWhiteSpace(r.Resolver) || !string.IsNullOrWhiteSpace(r.Resolution))
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md("**处理信息**", heading: true));
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(r.Resolver)) sb.AppendLine($"维修人: {FeishuCardV2.Escape(r.Resolver)}");
            if (!string.IsNullOrWhiteSpace(r.Resolution)) sb.AppendLine($"措施: {FeishuCardV2.Escape(r.Resolution)}");
            elements.Add(FeishuCardV2.Md(sb.ToString().TrimEnd()));
        }

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note($"记录 #{r.Id} · 更新时间 {FmtTime(r.UpdatedAt)}"));

        return FeishuCardV2.Root($"{station} · {model} · 待办状态变更", "blue", elements, bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    private static string BuildFailItems(TestRecord r)
    {
        var sb = new StringBuilder();
        foreach (var t in r.FailedTests)
        {
            var val = string.IsNullOrWhiteSpace(t.Value) ? "" : $" = {t.Value}{t.Unit}";
            var spec = string.IsNullOrWhiteSpace(t.Lolim) && string.IsNullOrWhiteSpace(t.Hilim)
                ? "" : $"（规格 {t.Lolim} ~ {t.Hilim}）";
            sb.AppendLine($"▸ {FeishuCardV2.Escape(t.Name)}{val} {spec}".Trim());
        }
        return sb.ToString().TrimEnd();
    }

    private static string FmtTime(string? ts)
    {
        var n = TimeUtil.Normalize(ts);
        return n.Length == 0 ? "—" : n;
    }
}
