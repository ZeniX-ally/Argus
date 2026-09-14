using System.Text;
using System.Text.Json;

namespace FctAggregator;

public static class FeishuNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const int MaxRetries = 3;

    /// <summary>合并卡最多列出的失败项数（超出只给「另有 N 项未列出」，防止卡片过长被飞书折叠）。</summary>
    private const int BatchMaxItemsListed = 10;
    /// <summary>合并卡最多列出的记录明细条数。</summary>
    private const int BatchMaxRecordsListed = 10;

    public static Task<FeishuSendOutcome> SendFailAlert(string webhookUrl, TestRecord record)
        => PostCard(webhookUrl, BuildFailCard(record), record.Sn ?? "?");

    /// <summary>FAIL 告警批量推送：**单条走原卡**（孤立失败的信息量不该被摊平），多条走合并卡。</summary>
    public static Task<FeishuSendOutcome> SendFailAlertBatch(string webhookUrl, IReadOnlyList<TestRecord> records)
    {
        if (records == null || records.Count == 0) return Task.FromResult(FeishuSendOutcome.Skipped);
        if (records.Count == 1) return SendFailAlert(webhookUrl, records[0]);
        var windowSec = Math.Clamp(AppConfig.Instance.FeishuFailMergeWindowSec, 10, 3600);
        return PostCard(webhookUrl, BuildFailBatchCard(records, windowSec), $"批量 ×{records.Count}");
    }

    public static Task<FeishuSendOutcome> SendStatusChangeAlert(string webhookUrl, MaintenanceRecord rec, string fromStatus, string toStatus)
        => PostCard(webhookUrl, BuildStatusChangeCard(rec, fromStatus, toStatus), $"#{rec.Id} {rec.FailItem}");

    private static async Task<FeishuSendOutcome> PostCard(string webhookUrl, object card, string tag)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            webhookUrl = AppConfig.FallbackWebhookUrl;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            Logger.Info($"[飞书推送] 未配置 webhook_url，跳过推送 | {tag}");
            return FeishuSendOutcome.Skipped;
        }
        if (!webhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // 前缀不合法属于配置错误，重试无意义 → 返回 Skipped，避免每次启动把今日 FAIL 全量重刷一遍日志
            Logger.Error($"[飞书推送] 拒绝发送: webhook 必须 https://（当前前缀 {webhookUrl[..Math.Min(24, webhookUrl.Length)]}…）| {tag}");
            return FeishuSendOutcome.Skipped;
        }
        var payload = new { msg_type = "interactive", card };
        var json = JsonSerializer.Serialize(payload);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(webhookUrl, content);
                if (resp.IsSuccessStatusCode) return FeishuSendOutcome.Sent;
                Logger.Warning($"飞书推送失败(尝试{attempt + 1}/{MaxRetries}): HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"飞书推送异常(尝试{attempt + 1}/{MaxRetries}): {ex.Message}");
            }
            await Task.Delay(1000 * (attempt + 1));
        }
        Logger.Error($"飞书推送最终失败: {tag}");
        return FeishuSendOutcome.Failed;
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

    /// <summary>FAIL 合并卡：一个窗口内多条 FAIL 合成一张，按失败项聚合计数 + 明细截断。
    /// 用于批量不良（治具故障连挂几十片）场景，替代原来「一条 FAIL 一张卡」的刷屏行为。</summary>
    public static object BuildFailBatchCard(IReadOnlyList<TestRecord> recs, int windowSec)
    {
        if (recs == null || recs.Count == 0) throw new ArgumentException("批量卡至少需要 1 条记录", nameof(recs));

        var stations = Distinct(recs.Select(r => r.StationId).Where(s => !string.IsNullOrWhiteSpace(s)));
        var models = Distinct(recs.Select(r => r.Model).Where(m => !string.IsNullOrWhiteSpace(m)));
        var station = Summarize(stations, "未知机台", "台");
        var model = Summarize(models, "—", "个");

        var times = recs.Select(r => FmtTime(r.BatchTimestamp)).Where(t => t != "—").OrderBy(t => t, StringComparer.Ordinal).ToList();
        var range = times.Count == 0 ? "—"
            : times[0] == times[^1] ? times[0]
            : $"{times[0]} ~ {times[^1]}";

        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", station), ("型号", model)),
            FeishuCardV2.FieldRow(("失败条数", $"×{recs.Count}"), ("时间范围", range)),
        };

        // 失败项聚合：同一测项在多片板上重复失败 → 合并计数，一眼看出「哪个项在批量挂」
        var itemCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var noDetail = 0;
        foreach (var r in recs)
        {
            if (r.FailedTests.Count > 0)
            {
                foreach (var t in r.FailedTests)
                    if (!string.IsNullOrWhiteSpace(t.Name))
                        itemCount[t.Name] = itemCount.GetValueOrDefault(t.Name) + 1;
            }
            else noDetail++;
        }

        if (itemCount.Count > 0)
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md($"**失败项统计（{itemCount.Count} 项）**", heading: true));
            var top = itemCount
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(BatchMaxItemsListed)
                .ToList();
            var sb = new StringBuilder();
            foreach (var kv in top) sb.AppendLine($"▸ {FeishuCardV2.Escape(kv.Key)} ×{kv.Value}");
            if (itemCount.Count > top.Count) sb.AppendLine($"… 另有 {itemCount.Count - top.Count} 项未列出");
            elements.Add(FeishuCardV2.Md(sb.ToString().TrimEnd()));
        }

        if (noDetail > 0)
            elements.Add(FeishuCardV2.Md($"（{noDetail} 条无测项明细，仅有汇总失败原因）"));

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Md("**明细**", heading: true));
        var detail = new StringBuilder();
        foreach (var r in recs.Take(BatchMaxRecordsListed))
        {
            var sn = FeishuCardV2.Escape(string.IsNullOrWhiteSpace(r.Sn) ? "—" : r.Sn);
            var what = r.FailedTests.Count > 0
                ? $"{r.FailedTests.Count} 项"
                : FeishuCardV2.Escape(Clip(r.FailReason, 20));
            detail.AppendLine($"▸ {sn} · {FmtTime(r.BatchTimestamp)} · {what}");
        }
        if (recs.Count > BatchMaxRecordsListed) detail.AppendLine($"… 另有 {recs.Count - BatchMaxRecordsListed} 条未列出");
        elements.Add(FeishuCardV2.Md(detail.ToString().TrimEnd()));

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note($"{windowSec}s 窗口内合并 · 共 {recs.Count} 条 FAIL"));

        return FeishuCardV2.Root($"{station} · {model} · FAIL 批量告警 ×{recs.Count}", "red", elements,
            bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    /// <summary>去重保序（卡面「机台/型号」列用）。</summary>
    internal static List<string> Distinct(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var v in values)
        {
            var t = (v ?? "").Trim();
            if (t.Length == 0) continue;
            if (seen.Add(t)) list.Add(t);
        }
        return list;
    }

    /// <summary>把去重后的集合压成一行：≤3 个直接列名，超过则截断并补「等 N 台/个」。</summary>
    internal static string Summarize(IReadOnlyList<string> values, string fallback, string unit)
    {
        if (values == null || values.Count == 0) return fallback;
        if (values.Count <= 3) return string.Join("/", values);
        return $"{string.Join("/", values.Take(3))} 等 {values.Count} {unit}";
    }

    /// <summary>超长文本裁剪（无测项明细的老记录只能退回 fail_reason，可能很长）。</summary>
    internal static string Clip(string? s, int max)
    {
        var t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (t.Length == 0) return "—";
        return t.Length <= max ? t : t[..max] + "…";
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
            // 审计 M14：XML 值可含 markdown 特殊字符——Value/Unit/Lolim/Hilim 全部过 Escape（Name 原已过）
            var val = string.IsNullOrWhiteSpace(t.Value) ? "" : $" = {FeishuCardV2.Escape(t.Value)}{FeishuCardV2.Escape(t.Unit)}";
            var spec = string.IsNullOrWhiteSpace(t.Lolim) && string.IsNullOrWhiteSpace(t.Hilim)
                ? "" : $"（规格 {FeishuCardV2.Escape(t.Lolim)} ~ {FeishuCardV2.Escape(t.Hilim)}）";
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
