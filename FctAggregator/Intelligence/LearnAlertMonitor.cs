using System.Text.Json;

namespace FctAggregator;

/// <summary>章节群挂卡已推送记录（按天 + 章节去重，避免同一群挂每轮巡检都推）。</summary>
public sealed class GroupAlertPushState
{
    public string Date { get; set; } = "";
    public List<string> Sections { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);

    public static GroupAlertPushState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<GroupAlertPushState>(json); }
        catch { return null; }
    }
}

/// <summary>正常态偏离卡已推送计数（按天封顶）。</summary>
public sealed class DeviationPushState
{
    public string Date { get; set; } = "";
    public int Count { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static DeviationPushState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DeviationPushState>(json); }
        catch { return null; }
    }
}

public sealed class DeviationHitRow
{
    public string Key { get; set; } = "";
    public double Score { get; set; }
    public double Value { get; set; }
    public double Mean { get; set; }
    public double Sigma { get; set; }
}

/// <summary>正常态偏离告警卡的载荷。</summary>
public sealed class DeviationAlertPayload
{
    public string StationId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Source { get; set; } = "";
    public string Ts { get; set; } = "";
    public string TopSignal { get; set; } = "";
    public double TopScore { get; set; }
    public int SignalCount { get; set; }
    public List<DeviationHitRow> Top { get; set; } = new();

    /// <summary>来源代号 → 现场可读中文。</summary>
    public string SourceZh => Source switch
    {
        NormalModelStore.SourceMeasurement => "测量值",
        NormalModelStore.SourceWaveform => "TDMS 波形特征",
        NormalModelStore.SourceDevice => "机台资源",
        _ => string.IsNullOrWhiteSpace(Source) ? "未知来源" : Source,
    };

    /// <summary>从偏离事件组装载荷（纯函数；DetailJson 坏掉时只保留最高分信号，不抛异常）。</summary>
    public static DeviationAlertPayload FromEvent(DeviationEvent ev, string? stationId, int topN = 5)
    {
        var p = new DeviationAlertPayload
        {
            StationId = string.IsNullOrWhiteSpace(stationId) ? "未知机台" : stationId!,
            Model = ev.Model ?? "",
            Source = ev.Source ?? "",
            Ts = ev.Ts ?? "",
            TopSignal = ev.TopSignal ?? "",
            TopScore = ev.TopScore,
            SignalCount = ev.SignalCount,
        };
        if (string.IsNullOrWhiteSpace(ev.DetailJson)) return p;
        try
        {
            using var doc = JsonDocument.Parse(ev.DetailJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return p;
            var rows = new List<DeviationHitRow>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                rows.Add(new DeviationHitRow
                {
                    Key = el.TryGetProperty("signal_key", out var k) ? k.GetString() ?? "" : "",
                    Score = el.TryGetProperty("score", out var s) && s.TryGetDouble(out var sv) ? sv : 0,
                    Value = el.TryGetProperty("value", out var v) && v.TryGetDouble(out var vv) ? vv : 0,
                    Mean = el.TryGetProperty("mean", out var m) && m.TryGetDouble(out var mv) ? mv : 0,
                    Sigma = el.TryGetProperty("sigma", out var g) && g.TryGetDouble(out var gv) ? gv : 0,
                });
            }
            p.Top = rows.OrderByDescending(r => r.Score).ThenBy(r => r.Key, StringComparer.Ordinal).Take(Math.Max(1, topN)).ToList();
        }
        catch { /* DetailJson 损坏只损失明细，最高分信号已在事件本体里 */ }
        return p;
    }
}

/// <summary>
/// 自学习类告警（章节群挂 / 正常态偏离）。
///
/// 两者此前都只落在 `app_meta` 与界面里，现场无人盯屏时等于没有：
/// - **章节群挂**：同一章节 N 个**不同**信号同时挂 → 典型系统性故障（治具/供电/程序版本），
///   信息量远高于单条 FAIL。阈值复用 `learn_group_alert_min`（默认 3），不新开口径。
/// - **正常态偏离**：PASS 但测量值偏离已学正常态 → 比 FAIL 更早的预警。
///   `DeviationScorer` 已自带四道防误报闸门（模型 ready / 投票数 / 单日风暴上限 / 总开关），
///   这里只再加「分数下限 + 单日推送封顶」两道，避免把预警变成噪音。
/// </summary>
public static class LearnAlertMonitor
{
    public const string MetaGroupPush = "feishu_group_alert_pushed";
    public const string MetaDeviationPush = "feishu_deviation_push_state";

    // ───────────────────────── 章节群挂 ─────────────────────────

    /// <summary>纯函数：挑出当天尚未推送过的章节，并把它们记入状态（跨天自动重置）。
    /// 抽成纯函数是为了让「同一群挂每轮巡检不重复推」这条口径可被自检直接断言。</summary>
    public static List<SectionGroupAlertResult> SelectFreshAlerts(GroupAlertPushState state,
        string dayKey, IReadOnlyList<SectionGroupAlertResult> all)
    {
        if (state == null || all == null) return new List<SectionGroupAlertResult>();
        if (!string.Equals(state.Date, dayKey, StringComparison.Ordinal))
        {
            state.Date = dayKey;
            state.Sections.Clear();
        }
        var fresh = all
            .Where(r => !state.Sections.Contains(r.Section, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var r in fresh) state.Sections.Add(r.Section);
        return fresh;
    }

    /// <summary>评估当天章节群挂并推送未推过的章节。由采集健康巡检周期调用。</summary>
    public static void RunGroupAlerts(Database db, AppConfig cfg, DateTime? now = null)
    {
        if (db == null || cfg == null || !cfg.FeishuGroupAlertEnabled) return;
        try
        {
            var day = (now ?? DateTime.Now).Date;
            var dayKey = day.ToString("yyyy-MM-dd");
            var reasons = db.FetchDayFailReasons(dayKey);
            if (reasons == null || reasons.Count == 0) return;

            var items = reasons
                .SelectMany(s => (s ?? "").Split(new[] { '\r', '\n', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (items.Count == 0) return;

            var results = FailReasonMerger.CheckSectionGroupAlert(items, cfg.LearnGroupAlertMin);
            if (results.Count == 0) return;

            var state = GroupAlertPushState.FromJson(db.GetMeta(MetaGroupPush)) ?? new GroupAlertPushState();
            var fresh = SelectFreshAlerts(state, dayKey, results);
            if (fresh.Count == 0) return;
            db.SetMeta(MetaGroupPush, state.ToJson());

            var station = string.IsNullOrEmpty(cfg.StationId) ? "未知机台" : cfg.StationId;
            var url = cfg.WebhookUrl;
            Logger.Warning($"[自学习] 章节群挂告警: {string.Join("；", fresh.Select(a => $"§{a.Section} {a.DistinctSignalCount} 信号"))}");
            Task.Run(async () =>
            {
                try { await FeishuNotifier.SendGroupAlert(url, station, dayKey, fresh, cfg.LearnGroupAlertMin); }
                catch (Exception ex) { Logger.Error($"[自学习] 群挂告警推送失败: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Logger.Warning($"[自学习] 群挂告警评估异常（已吞并）: {ex.Message}"); }
    }

    // ───────────────────────── 正常态偏离 ─────────────────────────

    /// <summary>纯函数：占用一次当日推送额度。跨天自动重置；已达上限返回 false（不占额度）。</summary>
    public static bool TryReserveDeviationPush(DeviationPushState state, string dayKey, int cap)
    {
        if (state == null) return false;
        if (!string.Equals(state.Date, dayKey, StringComparison.Ordinal))
        {
            state.Date = dayKey;
            state.Count = 0;
        }
        var limit = Math.Max(1, cap);
        if (state.Count >= limit) return false;
        state.Count++;
        return true;
    }

    /// <summary>黄卡机台：这份记录上的号（排除空和 UNKNOWN）→ 进程已识别的号 → config.station_id → 未知机台。</summary>
    public static string StationLabel(string? recordStation, AppConfig? cfg)
    {
        foreach (var raw in new[] { recordStation, AppState.StationId, cfg?.StationId })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Trim();
            if (t.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)) continue;
            return t;
        }
        return "未知机台";
    }

    /// <summary>偏离事件落库后调用：达分数下限且当日未超推送封顶时推卡。</summary>
    public static void MaybeNotifyDeviation(Database db, AppConfig cfg, DeviationEvent ev)
    {
        if (db == null || cfg == null || ev == null) return;
        if (!cfg.FeishuDeviationAlertEnabled) return;
        if (ev.TopScore < Math.Max(1, cfg.FeishuDeviationMinScore)) return; // 先判分数，避免热路径白读 meta

        try
        {
            var dayKey = (ev.Ts != null && ev.Ts.Length >= 10 ? ev.Ts[..10] : DateTime.Now.ToString("yyyy-MM-dd"));
            var state = DeviationPushState.FromJson(db.GetMeta(MetaDeviationPush)) ?? new DeviationPushState();
            if (!TryReserveDeviationPush(state, dayKey, cfg.FeishuDeviationMaxPerDay))
            {
                Logger.Info($"[正常态] 偏离告警已达当日上限 {cfg.FeishuDeviationMaxPerDay} 条，本条不再推送（分数 {ev.TopScore:F0}）");
                return;
            }
            db.SetMeta(MetaDeviationPush, state.ToJson());

            var station = string.IsNullOrWhiteSpace(ev.StationId) ? StationLabel(null, cfg) : ev.StationId;
            var payload = DeviationAlertPayload.FromEvent(ev, station);
            var url = cfg.WebhookUrl;
            Logger.Warning($"[正常态] 偏离告警: {ev.Model} / {ev.TopSignal} 分数 {ev.TopScore:F0}（{ev.SignalCount} 个信号）");
            Task.Run(async () =>
            {
                try { await FeishuNotifier.SendDeviationAlert(url, payload); }
                catch (Exception ex) { Logger.Error($"[正常态] 偏离告警推送失败: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Logger.Warning($"[正常态] 偏离告警评估异常（已吞并）: {ex.Message}"); }
    }
}
