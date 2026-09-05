using System.Text.Json;

namespace FctAggregator;

public sealed class TodoEvent
{
    public long TodoId;
    public string OriginMachine = "";
    public string? Owner;
    public string State = "";
    public long Version;
    public DateTime UpdatedAt = DateTime.Now;
    public string Operator = "";
}

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
            var ev = new TodoEvent
            {
                TodoId = rec.Id,
                OriginMachine = _machine,
                Owner = rec.Resolver,
                State = to,
                Version = _db.BumpTodoVersion(rec.Id) + 1,
                UpdatedAt = DateTime.Now,
                Operator = rec.Resolver ?? _machine,
            };
            Broadcast(ev);
        }
        catch (Exception ex) { Logger.Warning($"[待办同步] 本地变更广播失败: {ex.Message}"); }
    }

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
