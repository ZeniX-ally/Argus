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

        var md = BuildFailMarkdown(record);
        var payload = new
        {
            msg_type = "interactive",
            card = new
            {
                header = new
                {
                    title = new { tag = "plain_text", content = "FCT Fail 告警" },
                    template = "red",
                },
                elements = new object[]
                {
                    new { tag = "markdown", content = md }
                }
            }
        };
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
        Logger.Error($"飞书推送最终失败: {record.Sn}");
    }

    private static string BuildFailMarkdown(TestRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## FCT Fail 告警");
        sb.AppendLine();
        sb.AppendLine("| 字段 | 值 |");
        sb.AppendLine("|------|-----|");
        sb.AppendLine($"| **机台** | {r.StationId} |");
        sb.AppendLine($"| **型号** | {r.Model} |");
        sb.AppendLine($"| **位置** | {r.Category} |");
        sb.AppendLine($"| **时间** | {r.BatchTimestamp} |");
        sb.AppendLine($"| **SN** | {r.Sn} |");
        sb.AppendLine($"| **文件** | {Path.GetFileName(r.XmlPath)} |");
        sb.AppendLine();
        if (r.FailedTests.Count > 0)
        {
            sb.AppendLine("| 测试项 | 实测值 | 下限 | 上限 | 单位 | 规则 |");
            sb.AppendLine("|--------|--------|------|------|------|------|");
            foreach (var t in r.FailedTests)
                sb.AppendLine($"| {t.Name} | {t.Value} | {t.Lolim} | {t.Hilim} | {t.Unit} | {t.Rule} |");
        }
        return sb.ToString();
    }
}
