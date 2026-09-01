using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 全局待办事件（跨机台广播同步单元）。
/// 任一机台对待办做认领/状态变更时，本地生成此事件并经 MeshPusher 广播；
/// 邻居收到后按 <see cref="Version"/> 合并（认领抢占 + last-write-wins）。
/// </summary>
public sealed class TodoEvent
{
    public long TodoId;                       // 待办在源机 todo_items 的 id（跨机用 (origin_machine, TodoId) 定位）
    public string OriginMachine = "";         // 待办归属机台（首次登记它的机台）
    public string? Owner;                     // 认领人（null = 未认领）
    public string State = "";                 // 待办状态（open/in_progress/resolved...）
    public long Version;                      // 单调递增版本号（每次变更 +1）
    public DateTime UpdatedAt = DateTime.Now;
    public string Operator = "";              // 操作人（谁改的，用于审计/展示）
}

/// <summary>
/// 全局待办池（去中心化协作核心）。
///
/// 设计（满足"全局待办池 + 跨机实时同步 + 冲突处理"）：
/// - 每台机在本地 maintenance_records / todo_items 上维护一份全局视图；
/// - 任一机台的认领/状态变更 → 生成 <see cref="TodoEvent"/>（携带 version）→ 经 MeshPusher 广播；
/// - 邻居收到事件 → 按 (origin_machine, TodoId) 定位本地副本，用版本号裁决：
///     * version 更大者胜（last-write-wins）；
///     * 认领态优先：若本地未认领而远端已认领，直接采纳远端 owner（认领抢占，避免两人同时认领同一待办）；
/// - 冲突概率极低（认领即抢占），无需复杂共识；Gossiper 周期对账可补漏（todo 事件非强一致，
///   依赖状态变更时的广播 + 人工刷新视图）。
///
/// 与本地既有待办逻辑解耦：本类只负责"跨机同步"，不接管待办的业务登记（SyncTodoItems 仍本地跑）。
/// </summary>
public sealed class TodoSync : IDisposable
{
    private readonly Database _db;
    private readonly MeshPusher _pusher;
    private readonly string _machine;

    public TodoSync(Database db, MeshPusher pusher, string machine)
    {
        _db = db;
        _pusher = pusher;
        _machine = string.IsNullOrEmpty(machine) ? "UNKNOWN" : machine;
    }

    /// <summary>启动：订阅本地待办状态变更事件，自动广播。需在 MeshPusher.Init 之后调用。</summary>
    public void Start()
    {
        _db.MaintenanceStatusChanged += OnLocalStatusChanged;
        Logger.Info($"[待办同步] 已启用: machine={_machine}");
    }

    public void Stop()
    {
        _db.MaintenanceStatusChanged -= OnLocalStatusChanged;
    }

    private void OnLocalStatusChanged(MaintenanceRecord rec, string from, string to)
    {
        try
        {
            // 本地状态变更 → 生成事件广播（version 取本地最新 +1，ApplyEvent 会写回）
            var ev = new TodoEvent
            {
                TodoId = rec.Id,
                OriginMachine = _machine,
                Owner = rec.Resolver,           // MaintenanceRecord.Resolver 即维修/认领人
                State = to,
                Version = _db.BumpTodoVersion(rec.Id) + 1,
                UpdatedAt = DateTime.Now,
                Operator = rec.Resolver ?? _machine,
            };
            Broadcast(ev);
        }
        catch (Exception ex) { Logger.Warning($"[待办同步] 本地变更广播失败: {ex.Message}"); }
    }

    /// <summary>本地主动认领/改状态时调用（供 UI 在改库后触发同步，避免依赖事件参数）。</summary>
    public void PublishClaim(long todoId, string? owner, string state)
    {
        try
        {
            var ev = new TodoEvent
            {
                TodoId = todoId,
                OriginMachine = _machine,
                Owner = owner,
                State = state,
                Version = _db.BumpTodoVersion(todoId) + 1,
                UpdatedAt = DateTime.Now,
                Operator = owner ?? _machine,
            };
            Broadcast(ev);
        }
        catch (Exception ex) { Logger.Warning($"[待办同步] 认领广播失败: {ex.Message}"); }
    }

    private void Broadcast(TodoEvent ev)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "todo",
            ["todo_id"] = ev.TodoId,
            ["origin_machine"] = ev.OriginMachine,
            ["owner"] = ev.Owner,
            ["state"] = ev.State,
            ["version"] = ev.Version,
            ["updated_at"] = ev.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            ["operator"] = ev.Operator,
        });
        _pusher.BroadcastEvent("todo", json);
    }

    /// <summary>邻居广播来的待办事件 → 按版本合并到本地副本库。</summary>
    public void HandleEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ev = new TodoEvent
            {
                TodoId = root.TryGetProperty("todo_id", out var t) && t.TryGetInt64(out var tid) ? tid : 0,
                OriginMachine = root.TryGetProperty("origin_machine", out var om) ? (om.GetString() ?? "") : "",
                Owner = root.TryGetProperty("owner", out var ow) ? ow.GetString() : null,
                State = root.TryGetProperty("state", out var st) ? (st.GetString() ?? "") : "",
                Version = root.TryGetProperty("version", out var vv) && vv.TryGetInt64(out var ver) ? ver : 0,
            };
            if (ev.TodoId <= 0 || string.IsNullOrEmpty(ev.OriginMachine)) return;
            _db.ApplyRemoteTodoEvent(ev);
        }
        catch (Exception ex) { Logger.Warning($"[待办同步] 处理远端事件失败: {ex.Message}"); }
    }

    public void Dispose() => Stop();
}
