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
    /// <summary>群挂卡最多列出的章节数 / 每章节信号数（防卡片过长被飞书折叠）。</summary>
    private const int GroupAlertMaxSections = 5;
    private const int GroupAlertMaxSignals = 10;

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

    // ───────────────────────── 采集异常告警（漏采是数据正确性问题，必须主动告知） ─────────────────────────

    public static Task<FeishuSendOutcome> SendCollectAlert(string webhookUrl, CollectAlertPayload p)
        => PostCard(webhookUrl, BuildCollectAlertCard(p), $"{p.StationId} 采集异常");

    public static Task<FeishuSendOutcome> SendCollectRecover(string webhookUrl, string stationId, string? recoveredFrom, int abnormalCount)
        => PostCard(webhookUrl, BuildCollectRecoverCard(stationId, recoveredFrom, abnormalCount),
            $"{stationId} 采集恢复");

    public static Task<FeishuSendOutcome> SendDailySummary(string webhookUrl, DailySummaryPayload p)
        => PostCard(webhookUrl, BuildDailySummaryCard(p), $"{p.StationId} 运行摘要 {p.Date}");

    /// <summary>采集异常告警卡：橙色（需要人看一眼，但不是停机事故）。</summary>
    public static object BuildCollectAlertCard(CollectAlertPayload p)
    {
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", p.StationId), ("统计窗口", $"{p.WindowMin} 分钟")),
            FeishuCardV2.FieldRow(("解析失败", $"{p.AbnormalCount} 条"), ("重试队列", $"{p.RetryQueueDepth} 条")),
            FeishuCardV2.Hr(),
            FeishuCardV2.Md("**触发原因**", heading: true),
            FeishuCardV2.Md(FeishuCardV2.Escape(
                CollectHealthMonitor.ReasonOf(p.AbnormalCount, p.ParseThreshold, p.RetryQueueDepth, p.RetryThreshold))),
        };

        if (p.ByCode.Count > 0)
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md($"**按错误码分布（{p.ByCode.Count} 类）**", heading: true));
            var sb = new StringBuilder();
            foreach (var row in p.ByCode)
            {
                sb.Append($"▸ {FeishuCardV2.Escape(ErrorCodeZh(row.ErrorCode))} ×{row.Count}");
                if (!string.IsNullOrWhiteSpace(row.SkipReason))
                    sb.Append($"（{FeishuCardV2.Escape(Clip(row.SkipReason, 30))}）");
                sb.AppendLine();
            }
            elements.Add(FeishuCardV2.Md(sb.ToString().TrimEnd()));
        }

        if (p.Recent.Count > 0)
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md($"**最近失败文件（前 {p.Recent.Count} 条）**", heading: true));
            var sb = new StringBuilder();
            foreach (var f in p.Recent)
                sb.AppendLine($"▸ {FeishuCardV2.Escape(f.CreatedAt)} · {FeishuCardV2.Escape(ErrorCodeZh(f.ErrorCode))} · {FeishuCardV2.Escape(FileName(f.XmlPath))}");
            elements.Add(FeishuCardV2.Md(sb.ToString().TrimEnd()));
        }

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note("漏采会让良率偏高且现场无感——请确认结果目录可写、文件未被其他程序占用。"));

        return FeishuCardV2.Root($"{p.StationId} · 采集异常告警", "orange", elements,
            bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    /// <summary>采集恢复卡：绿色。没有这张卡，运维不知道告警是否还需要继续处理。</summary>
    public static object BuildCollectRecoverCard(string stationId, string? recoveredFrom, int abnormalCount)
    {
        var station = string.IsNullOrWhiteSpace(stationId) ? "未知机台" : stationId;
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", station), ("当前解析失败", $"{abnormalCount} 条")),
        };
        if (!string.IsNullOrWhiteSpace(recoveredFrom))
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md("**此前告警**", heading: true));
            elements.Add(FeishuCardV2.Md(FeishuCardV2.Escape(recoveredFrom)));
        }
        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note($"恢复时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        return FeishuCardV2.Root($"{station} · 采集异常已恢复", "green", elements,
            bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    /// <summary>每日运行摘要卡：把当天已经跑完的维护结果汇总成一张例行卡（不产生噪音）。</summary>
    public static object BuildDailySummaryCard(DailySummaryPayload p)
    {
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("采集总数", p.Total.ToString()), ("综合良率", $"{p.YieldPct:F2}%")),
            FeishuCardV2.FieldRow(("PASS", p.Pass.ToString()), ("FAIL", p.Fail.ToString()), ("中断", p.Interrupted.ToString())),
        };

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Md("**Top 失败项**", heading: true));
        if (p.TopFails.Count == 0)
        {
            elements.Add(FeishuCardV2.Md("（当日无 FAIL 记录）"));
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var t in p.TopFails)
                sb.AppendLine($"▸ {FeishuCardV2.Escape(t.FailItem)} ×{t.Count}（{t.Ratio:F1}%）");
            elements.Add(FeishuCardV2.Md(sb.ToString().TrimEnd()));
        }

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Md("**运维状态**", heading: true));
        var ops = new StringBuilder();
        ops.AppendLine($"库健康分: {p.StorageScore}/100");
        ops.AppendLine($"库体积: {FmtBytes(p.DbBytes)}");
        ops.AppendLine(p.BackupOk ? $"每日备份: 成功（{FeishuCardV2.Escape(p.BackupName)}）" : "每日备份: **未完成**");
        ops.AppendLine($"重试队列: {p.RetryQueueDepth} 条");
        ops.AppendLine($"解析失败: {p.AbnormalParseCount} 条（近 {p.WindowMin} 分钟）");
        elements.Add(FeishuCardV2.Md(ops.ToString().TrimEnd()));

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note($"{p.Date} · 每日维护完成后自动汇总"));

        return FeishuCardV2.Root($"{p.StationId} · {p.Date} 运行摘要", "blue", elements,
            bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    /// <summary>错误码 → 现场可读中文（未知码原样返回，便于新增解析器时不用改这里）。</summary>
    public static string ErrorCodeZh(string? code) => (code ?? "").Trim().ToLowerInvariant() switch
    {
        "oversize" => "文件超 512MB 上限",
        "read_error" => "读取失败（占用/权限）",
        "registry_exception" => "解析器内部异常",
        "xml_malformed" => "XML 格式错误",
        "invalid_result" => "无效结果前缀",
        Database.SkipErrorCode => "正常跳过（debug/路径不匹配）",
        "" => "未标注",
        _ => code ?? "",
    };

    private static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "—";
        try { return Path.GetFileName(path); } catch { return path!; }
    }

    private static string FmtBytes(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }

    // ───────────────────────── 自学习告警（章节群挂 / 正常态偏离） ─────────────────────────

    public static Task<FeishuSendOutcome> SendGroupAlert(string webhookUrl, string stationId, string day,
        IReadOnlyList<SectionGroupAlertResult> alerts, int threshold)
        => PostCard(webhookUrl, BuildGroupAlertCard(stationId, day, alerts, threshold),
            $"{stationId} 章节群挂 ×{alerts.Count}");

    public static Task<FeishuSendOutcome> SendDeviationAlert(string webhookUrl, DeviationAlertPayload p)
        => PostCard(webhookUrl, BuildDeviationAlertCard(p), $"{p.StationId} 偏离 {p.TopSignal}");

    /// <summary>章节群挂卡：橙色。同一章节 N 个不同信号同时挂 → 系统性故障，信息量远高于单条 FAIL。</summary>
    public static object BuildGroupAlertCard(string stationId, string day,
        IReadOnlyList<SectionGroupAlertResult> alerts, int threshold)
    {
        var station = string.IsNullOrWhiteSpace(stationId) ? "未知机台" : stationId;
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", station), ("日期", day ?? "—")),
            FeishuCardV2.FieldRow(("判定阈值", $"≥{threshold} 个不同信号"), ("命中章节", $"{alerts.Count} 个")),
        };

        foreach (var a in alerts.Take(GroupAlertMaxSections))
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md($"**§{FeishuCardV2.Escape(a.Section)} · {a.DistinctSignalCount} 个不同信号**", heading: true));
            if (a.SignalNames.Count > 0)
            {
                var shown = a.SignalNames.Take(GroupAlertMaxSignals).Select(s => FeishuCardV2.Escape(s));
                var line = "▸ " + string.Join("\n▸ ", shown);
                if (a.SignalNames.Count > GroupAlertMaxSignals)
                    line += $"\n… 另有 {a.SignalNames.Count - GroupAlertMaxSignals} 个信号未列出";
                elements.Add(FeishuCardV2.Md(line));
            }
            if (!string.IsNullOrWhiteSpace(a.RootCauseHint))
                elements.Add(FeishuCardV2.Md($"根因指向：{FeishuCardV2.Escape(a.RootCauseHint)}"));
        }

        if (alerts.Count > GroupAlertMaxSections)
            elements.Add(FeishuCardV2.Md($"… 另有 {alerts.Count - GroupAlertMaxSections} 个章节未列出"));

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note("同一章节多个不同信号同时挂，通常指向治具/供电/程序版本等系统性原因，建议优先排查。"));
        return FeishuCardV2.Root($"{station} · 章节群挂告警 ×{alerts.Count}", "orange", elements,
            bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    /// <summary>正常态偏离卡：黄色。PASS 但在偏离 = 比 FAIL 更早的预警。</summary>
    public static object BuildDeviationAlertCard(DeviationAlertPayload p)
    {
        var model = string.IsNullOrWhiteSpace(p.Model) ? "—" : p.Model;
        var elements = new List<object>
        {
            FeishuCardV2.FieldRow(("机台", p.StationId), ("型号", model)),
            FeishuCardV2.FieldRow(("来源", p.SourceZh), ("最高偏离分", $"{p.TopScore:F0}")),
            FeishuCardV2.Hr(),
            FeishuCardV2.Md($"**最高分信号**\n{FeishuCardV2.Escape(p.TopSignal)}（共 {p.SignalCount} 个信号越线）"),
        };

        if (p.Top.Count > 0)
        {
            elements.Add(FeishuCardV2.Hr());
            elements.Add(FeishuCardV2.Md($"**偏离明细（前 {p.Top.Count} 项）**", heading: true));
            var sb = new StringBuilder();
            foreach (var h in p.Top)
                sb.AppendLine($"▸ {FeishuCardV2.Escape(h.Key)} 分 {h.Score:F0}（实测 {FmtNum(h.Value)}，正常 {FmtNum(h.Mean)}±{FmtNum(h.Sigma)}）");
            elements.Add(FeishuCardV2.Md(sb.ToString().TrimEnd()));
        }

        elements.Add(FeishuCardV2.Hr());
        elements.Add(FeishuCardV2.Note($"采集时间 {FmtTime(p.Ts)} · 属 PASS 但在偏离正常态，为提前预警"));
        return FeishuCardV2.Root($"{p.StationId} · {model} · 正常态偏离告警", "yellow", elements,
            bannerImgKey: AppConfig.Instance.FeishuBannerImgKey);
    }

    /// <summary>数值紧凑格式化（偏离明细用；避免 3.5200000001 这类噪声）。</summary>
    private static string FmtNum(double v)
    {
        if (!double.IsFinite(v)) return "—";
        var a = Math.Abs(v);
        if (a >= 10000 || (a > 0 && a < 0.01)) return v.ToString("0.###E+0");
        return Math.Round(v, 3).ToString("0.###");
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
