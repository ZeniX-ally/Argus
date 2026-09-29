namespace FctAggregator;

/// <summary>飞书推送结果三态。Sent=已送达；Skipped=未配置 webhook / 前缀不合法，主动跳过（不该重试）；
/// Failed=重试耗尽仍失败（不标记已推送，留给下次启动的「今日补推」兜底）。</summary>
public enum FeishuSendOutcome { Sent, Skipped, Failed }

/// <summary>FAIL 告警待发队列（纯容器，无定时器/无 IO，便于自检直接断言去重与取走语义）。</summary>
public sealed class FailAlertQueue
{
    private readonly Dictionary<string, TestRecord> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _pending.Count; } }

    /// <summary>入队。同一 xml_path 重复入队返回 false（去重，与 test_records.fail_alerted 同口径）。
    /// 空路径不入队——没有路径就无法落库标记，重复推送无从判断。</summary>
    public bool Enqueue(TestRecord rec)
    {
        if (rec == null || string.IsNullOrWhiteSpace(rec.XmlPath)) return false;
        lock (_lock)
        {
            if (_pending.ContainsKey(rec.XmlPath)) return false;
            _pending[rec.XmlPath] = rec;
            return true;
        }
    }

    public bool Contains(string xmlPath)
    {
        if (string.IsNullOrWhiteSpace(xmlPath)) return false;
        lock (_lock) return _pending.ContainsKey(xmlPath);
    }

    /// <summary>取走全部并清空（按 batch_timestamp 升序，卡面明细顺序稳定可复现）。</summary>
    public List<TestRecord> TakeAll()
    {
        lock (_lock)
        {
            var list = _pending.Values
                .OrderBy(r => r.BatchTimestamp ?? "", StringComparer.Ordinal)
                .ThenBy(r => r.XmlPath ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            _pending.Clear();
            return list;
        }
    }

    /// <summary>只读快照（不清空），自检用。</summary>
    public List<TestRecord> Snapshot()
    {
        lock (_lock)
            return _pending.Values
                .OrderBy(r => r.BatchTimestamp ?? "", StringComparer.Ordinal)
                .ThenBy(r => r.XmlPath ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}

/// <summary>
/// FAIL 告警批处理：窗口内多条 FAIL 合并成一张卡，治「批量不良刷屏」（治具坏掉连挂几十片时，
/// 原来一条 FAIL 一张卡，群会被刷满反而没人看）。
///
/// 口径：
/// - **单条仍走原卡**（`FeishuNotifier.BuildFailCard`）——孤立失败的信息量不该被摊平；
/// - 窗口 = `feishu_fail_merge_window_sec`（从第一条入队起算的**固定窗口**，不因后续入队顺延，
///   否则慢速持续失败会永远等不到冲刷）；
/// - 攒满 `feishu_fail_merge_max` 条立即冲刷（不等窗口）；
/// - `feishu_fail_merge_enabled=false` 时每条立即单独推送，退回 v3.40.5 行为。
///
/// 可靠性：**推送成功才 MarkFailAlerted**，失败/异常一律不标记 → 下次启动
/// `Engine.FlushTodayFailAlerts` 会重新补推今日未标记的 FAIL（复用既有补推链路，不新增落盘状态）。
/// </summary>
public sealed class FeishuFailBatcher : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Database _db;
    private readonly FailAlertQueue _queue = new();
    private readonly Func<string, IReadOnlyList<TestRecord>, Task<FeishuSendOutcome>> _send;
    private readonly object _timerLock = new();

    private System.Threading.Timer? _timer;
    private bool _armed;
    private int _flushing; // 防重入：同一时刻只允许一轮冲刷

    public FeishuFailBatcher(AppConfig cfg, Database db,
        Func<string, IReadOnlyList<TestRecord>, Task<FeishuSendOutcome>>? send = null)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _send = send ?? FeishuNotifier.SendFailAlertBatch;
    }

    public int PendingCount => _queue.Count;

    /// <summary>纯判定：是否应立即冲刷（不等窗口）。合并关闭→有任何一条即冲刷；合并开启→攒满上限即冲刷。</summary>
    public static bool ShouldFlushImmediately(int pendingCount, int mergeMax, bool mergeEnabled)
        => pendingCount > 0 && (!mergeEnabled || pendingCount >= Math.Max(1, mergeMax));

    /// <summary>纯判定：该结果是否要标记已推送。只有 Failed 不标记（留给补推），Skipped 也标记
    /// ——否则未配 webhook 的机台每次启动都会把今日 FAIL 全量重刷一遍日志。</summary>
    public static bool ShouldMarkAlerted(FeishuSendOutcome outcome)
        => outcome != FeishuSendOutcome.Failed;

    /// <summary>入队一条 FAIL。返回 false = 重复（同 xml_path 已在队列里）。</summary>
    public bool Enqueue(TestRecord rec)
    {
        if (!_queue.Enqueue(rec)) return false;

        var immediate = ShouldFlushImmediately(_queue.Count, _cfg.FeishuFailMergeMax, _cfg.FeishuFailMergeEnabled);
        // 抢到冲刷权就立即发；抢不到说明上一轮还在发，交给保底窗口即可（不丢，只是晚一点）
        if (immediate && TryBeginFlush())
            _ = RunFlushAsync();
        else
            ArmTimer();
        return true;
    }

    /// <summary>冲刷一次（Stop 与定时器调用）。已有冲刷在途时直接返回，不排队。</summary>
    public Task FlushAsync()
    {
        if (!TryBeginFlush()) return Task.CompletedTask;
        return RunFlushAsync();
    }

    private bool TryBeginFlush() => Interlocked.CompareExchange(ref _flushing, 1, 0) == 0;

    private void ArmTimer()
    {
        var sec = Math.Clamp(_cfg.FeishuFailMergeWindowSec, 10, 3600);
        lock (_timerLock)
        {
            if (_armed) return; // 固定窗口：从第一条起算，不因后续入队顺延
            _timer ??= new System.Threading.Timer(_ => OnTimerTick(), null, Timeout.Infinite, Timeout.Infinite);
            _armed = true;
            _timer.Change(TimeSpan.FromSeconds(sec), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>窗口到点：先复位 _armed 再尝试冲刷。
    /// 审计修复：原实现把 _armed=false 放在 RunFlushAsync 里，而抢不到冲刷权时 FlushAsync 直接返回——
    /// 一次性定时器已被消耗、_armed 却卡在 true → ArmTimer 从此永久空转，积压 FAIL 再也等不到窗口
    /// 冲刷（只在攒满 feishu_fail_merge_max 或进程退出时才发）。抢不到锁说明上一轮仍在发送，这里重排窗口。</summary>
    private void OnTimerTick()
    {
        lock (_timerLock)
        {
            _armed = false;
            try { _timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }
        if (TryBeginFlush()) _ = RunFlushAsync();
        else ArmTimer();
    }

    private async Task RunFlushAsync()
    {
        try
        {
            lock (_timerLock)
            {
                _armed = false;
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }

            var batch = _queue.TakeAll();
            if (batch.Count == 0) return;

            FeishuSendOutcome outcome;
            try
            {
                outcome = await _send(_cfg.WebhookUrl, batch);
            }
            catch (Exception ex)
            {
                RequeueBatch(batch);
                Logger.Error($"[飞书推送] FAIL 批量推送异常（{batch.Count} 条已回队，待下批/下次启动补推）: {ex.Message}");
                return;
            }

            if (!ShouldMarkAlerted(outcome))
            {
                RequeueBatch(batch);
                Logger.Warning($"[飞书推送] FAIL 批量推送失败（{batch.Count} 条已回队，待下批/下次启动补推）");
                return;
            }

            var marked = 0;
            try { marked = _db.MarkFailAlertedMany(batch.Select(r => r.XmlPath)); }
            catch (Exception ex) { Logger.Warning($"[飞书推送] 批量标记已推送失败: {ex.Message}"); }
            Logger.Info($"[飞书推送] FAIL {(batch.Count > 1 ? $"批量 ×{batch.Count}" : batch[0].Sn)} / {batch[0].Model} / 已标记 {marked} 条");
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    /// <summary>推送失败时把整批放回队列。审计：原来只打日志（TakeAll 已清空队列），
    /// 进程连续运行数天/数周时这些 FAIL 在本次生命周期内再也不会发出。
    /// 这里只回队不再 Arm：避免 webhook 持续故障时形成 60s 重试风暴；
    /// 下一条 FAIL 入队或退出冲刷会自动带上它们，兜底仍是下次启动的「今日补推」。</summary>
    private void RequeueBatch(List<TestRecord> batch)
    {
        int back = 0;
        foreach (var r in batch)
            if (_queue.Enqueue(r)) back++;
        if (back > 0) Logger.Info($"[飞书推送] {back} 条 FAIL 已回队列等待重试");
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            _armed = false;
        }
    }
}
