# 正常态自学习底座 P1（模型底座）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 落地正常态自学习底座 P1：`normal_models` 表 + 三个学习器接入现有摄取链路 + 心跳计数 + config 开关（默认关）+ selftest `[正常态]` 断言组。

**Architecture:** 纯增量学习。摄取链路落表后在调用点同步钩子（Engine→测量、TdmsFeatureCollector→波形、DeviceSampleRecorder→设备），学习器只消费内存中已解析的数据行（零回查），经 `NormalModelStore` 以 Welford 在线统计 + P² 流式分位数更新 `normal_models` 每信号一行。状态 learning/ready 在写入时判定，stale 在读取时按 `last_ts` 惰性判定（无需后台任务）。

**Tech Stack:** .NET（现有 FctAggregator，net? 跟随 csproj）、SQLite（Microsoft.Data.Sqlite 现有引用）、System.Text.Json，零新增依赖。

**Spec:** `docs/superpowers/specs/2026-09-07-normal-state-learning-design.md`（§5 数据模型、§6 学习器、§10 配置、§9.1 selftest；P2~P4 不在本计划）

## Global Constraints

- 零第三方依赖（不新增 NuGet 包）
- config 开关缺省关闭：`learn_normal_enabled=false` 时全链路 no-op，行为与现状 100% 一致
- 学习环节吞异常只计数（`Logger.Warning` + 错误计数），绝不阻塞摄取链路
- 只学 PASS：`rec.Result != "PASS"` 的记录一律不进模型
- 只学有限数值：`double.IsFinite(value)==false` 一律跳过
- 只学正常态：不存储/不学习任何判断阈值；lolim/hilim 判断逻辑不动
- 聚合端零改动；不改现有公开 API；不重写现有 Intelligence 类职责
- selftest 全绿才可交付（当前基线 961/961，本计划新增 `[正常态]` 分组）
- 注释用中文，遵循仓库现有代码风格（文件级命名空间 `namespace FctAggregator;`）

---

### Task 1: StreamingStats —— Welford 在线统计 + P² 流式分位数（纯函数，无 DB）

**Files:**
- Create: `FctAggregator/Intelligence/StreamingStats.cs`
- Test: `FctAggregator/selftest/SelfTest.cs`（先写 `RunNormalStateTests` 骨架，本任务先测算法）

**Interfaces:**
- Produces: `Welford` 类（`void Update(double x)` / `long N` / `double Mean` / `double Sigma`）；`P2Quantile` 类（`P2Quantile(double p)` / `void Update(double x)` / `double Quantile()` / `string Serialize()` / `static P2Quantile? Deserialize(string? json)`）。Task 2 的 `NormalModelStore` 依赖这两个类。

- [ ] **Step 1: 写失败测试**

在 `FctAggregator/selftest/SelfTest.cs` 末尾追加测试方法，并在 `Main` 中 `RunYldDailyTests(work);`（约 1651 行）之后插入调用与标题输出：

```csharp
        Console.WriteLine("\n【正常态自学习】Welford/P²收敛 / 建模与状态机 / PASS过滤 / 异常吞并 / 心跳");
        RunNormalStateTests(work);
```

文件末尾追加：

```csharp
    static void RunNormalStateTests(string work)
    {
        // ── Task1: Welford 收敛对拍 ──
        var w = new Welford();
        var xs = new List<double>();
        var rng = new Random(42);
        for (int i = 0; i < 1000; i++) { var x = 3.3 + rng.NextDouble() * 0.1; w.Update(x); xs.Add(x); }
        var mean = xs.Average();
        var std = Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / (xs.Count - 1));
        Check(Math.Abs(w.Mean - mean) < 1e-9, $"Welford 均值收敛（{w.Mean:F6} vs {mean:F6}）");
        Check(Math.Abs(w.Sigma - std) < 1e-9, $"Welford 样本σ收敛（{w.Sigma:F6} vs {std:F6}）");
        Check(w.N == 1000, "Welford 计数正确");

        // ── Task1: P² 分位数近似 ──
        var p01 = new P2Quantile(0.01); var p50 = new P2Quantile(0.5); var p99 = new P2Quantile(0.99);
        foreach (var x in xs) { p01.Update(x); p50.Update(x); p99.Update(x); }
        var sorted = xs.OrderBy(x => x).ToList();
        Check(Math.Abs(p50.Quantile() - sorted[500]) < 0.02 * (sorted.Max() - sorted.Min()) + 1e-9,
            $"P² 中位数近似（{p50.Quantile():F6} vs {sorted[500]:F6}）");
        Check(p01.Quantile() <= p50.Quantile() && p50.Quantile() <= p99.Quantile(), "P² 分位单调（p01≤p50≤p99）");

        // ── Task1: P² 序列化往返 ──
        var restored = P2Quantile.Deserialize(p50.Serialize());
        Check(restored != null && Math.Abs(restored!.Quantile() - p50.Quantile()) < 1e-12, "P² 序列化往返一致");
        Check(P2Quantile.Deserialize(null) == null && P2Quantile.Deserialize("垃圾") == null, "P² 反序列化容错");
    }
```

- [ ] **Step 2: 跑测试确认编译失败**

Run: `dotnet build FctAggregator/selftest/SelfTest.csproj`
Expected: 编译错误 `Welford` / `P2Quantile` 未定义

- [ ] **Step 3: 最小实现**

创建 `FctAggregator/Intelligence/StreamingStats.cs`：

```csharp
using System.Text.Json;

namespace FctAggregator;

/// <summary>正常态底座：Welford 在线均值/样本σ（O(1)/样本，可跨重启持久化）。</summary>
public sealed class Welford
{
    public long N { get; private set; }
    public double Mean { get; private set; }
    private double _m2;

    public void Update(double x)
    {
        N++;
        var d = x - Mean;
        Mean += d / N;
        _m2 += d * (x - Mean);
    }

    /// <summary>样本标准差（n-1 口径），n&lt;2 时返回 0。</summary>
    public double Sigma => N < 2 ? 0 : Math.Sqrt(_m2 / (N - 1));

    public (long n, double mean, double m2) Snapshot() => (N, Mean, _m2);
    public void Restore(long n, double mean, double m2) { N = n; Mean = mean; _m2 = m2; }
}

/// <summary>正常态底座：P² 流式分位数（Jain-Chlamtac 5 标记法，O(1) 内存，状态可序列化）。</summary>
public sealed class P2Quantile
{
    private const double Eps = 1e-12;
    private readonly double _p;
    private long _count;
    private readonly double[] _q = new double[5];   // 标记高度
    private readonly double[] _npos = new double[5]; // 实际位置（1 基）
    private bool _initialized;

    public P2Quantile(double p) => _p = Math.Clamp(p, 0.0, 1.0);

    public void Update(double x)
    {
        if (!_initialized)
        {
            // 前 5 个样本排序填充
            int i = (int)_count;
            _q[i] = x;
            _count++;
            if (_count == 5)
            {
                Array.Sort(_q);
                for (int k = 0; k < 5; k++) _npos[k] = k + 1;
                _initialized = true;
            }
            return;
        }

        _count++;
        int cell;
        if (x < _q[0]) { _q[0] = x; cell = 0; }
        else if (x >= _q[4]) { _q[4] = x; cell = 3; }
        else
        {
            cell = 0;
            while (cell < 4 && _q[cell + 1] <= x) cell++;
            cell = Math.Clamp(cell, 1, 4) - 1; // 落在 [cell, cell+1) 之间，右侧递增从此格开始
        }
        for (int i = cell + 1; i < 5; i++) _npos[i]++;

        // 期望位置 = 1 + (n-1)*i/4（1 基）
        double dn = (_count - 1) / 4.0;
        for (int i = 0; i < 5; i++)
        {
            double np = 1 + dn * i;
            double d = np - _npos[i];
            bool adjust = (d >= 1 && _npos[i + 1] - _npos[i] > 1) || (d <= -1 && _npos[i - 1] - _npos[i] < -1);
            if (!adjust) continue;
            int sign = d >= 0 ? 1 : -1;
            double q = Parabolic(i, sign);
            if (_q[i - 1] < q && q < _q[i + 1]) _q[i] = q;
            else _q[i] = Linear(i, sign);
            _npos[i] += sign;
        }
    }

    public double Quantile()
    {
        if (_count == 0) return 0;
        if (!_initialized)
        {
            // 样本数 <5：直接取已收样本的次序统计量
            int n = (int)_count;
            var s = _q.Take(n).OrderBy(v => v).ToList();
            int idx = (int)Math.Round(_p * (n - 1));
            return s[Math.Clamp(idx, 0, n - 1)];
        }
        // 线性插值到精确分位位置
        double target = 1 + (_count - 1) * _p;
        int i = 0;
        while (i < 4 && _npos[i + 1] < target) i++;
        if (_npos[i + 1] - _npos[i] < Eps) return _q[i];
        double t = (target - _npos[i]) / (_npos[i + 1] - _npos[i]);
        return _q[i] + t * (_q[i + 1] - _q[i]);
    }

    private double Parabolic(int i, int sign)
    {
        double nom = sign * (_npos[i + 1] - _npos[i]) * (_npos[i] + sign - _npos[i - 1]) * (_q[i + 1] - _q[i])
                   - sign * (_npos[i + 1] - _npos[i] + sign) * (_npos[i] - _npos[i - 1]) * (_q[i] - _q[i - 1]);
        double den = (_npos[i + 1] - _npos[i]) * (_npos[i] - _npos[i - 1]);
        return Math.Abs(den) < Eps ? _q[i] : _q[i] + nom / (den * (sign == 1 ? (_q[i + 1] - _q[i - 1]) : (_q[i + 1] - _q[i - 1])));
    }

    private double Linear(int i, int sign)
    {
        double a = _q[i] + sign * (_q[i + sign] - _q[i]) / (_npos[i + sign] - _npos[i]);
        return double.IsFinite(a) ? a : _q[i];
    }

    private sealed class State
    {
        public double p { get; set; }
        public long n { get; set; }
        public double[] q { get; set; } = new double[5];
        public double[] pos { get; set; } = new double[5];
        public bool init { get; set; }
    }

    public string Serialize() => JsonSerializer.Serialize(new State { p = _p, n = _count, q = _q, pos = _npos, init = _initialized });

    public static P2Quantile? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<State>(json);
            if (s == null || s.q.Length != 5 || s.pos.Length != 5) return null;
            var r = new P2Quantile(s.p) { _count = s.n, _initialized = s.init };
            Array.Copy(s.q, r._q, 5);
            Array.Copy(s.pos, r._npos, 5);
            return r;
        }
        catch { return null; }
    }
}
```

> 实现注意：`Update` 中 cell 定位与 P² 论文等价（x ≥ q[4] 时视作落在最高格右侧）；`Parabolic` 分母不随 sign 变号，公式已合并符号项。若自检数值断言不满足，优先核对 cell 递增区间与 `_npos` 更新方向。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet run --project FctAggregator/selftest`
Expected: `【正常态自学习】` 分组 6 条 Check 全部 `[OK]`（此时仅有 Task1 的断言）

- [ ] **Step 5: 提交**

```bash
git add FctAggregator/Intelligence/StreamingStats.cs FctAggregator/selftest/SelfTest.cs
git commit -m "feat(learn): streaming stats core - welford + p2 quantile"
```

---

### Task 2: normal_models 表 + NormalModelStore（观察/读取/状态机）

**Files:**
- Create: `FctAggregator/Intelligence/NormalModelStore.cs`
- Modify: `FctAggregator/Db/Database.cs`（Init 中 `tdms_features` 建表块之后，约 300 行处追加建表）
- Test: `FctAggregator/selftest/SelfTest.cs`（`RunNormalStateTests` 内追加）

**Interfaces:**
- Consumes: Task1 的 `Welford` / `P2Quantile`；`Database`（现有 `Open()` / `GetMeta` / `SetMeta`）
- Produces:
  - `NormalModelRow` 类（`Source, Model, SignalKey, N, Mean, Sigma, P01, P50, P99, MinV, MaxV, LastTs, Status`）
  - `NormalModelStore` 静态类：常量 `SourceMeasurement/Waveform/Device`；`void Observe(Database db, int minSamples, string source, string model, string signalKey, double value, string ts)`；`NormalModelRow? Get(Database db, string source, string model, string signalKey)`；`string EffectiveStatus(NormalModelRow row, DateTime now, int staleDays)`
- Task 4 的三个学习器只调 `Observe`；Task 6 断言用 `Get` / `EffectiveStatus`。

- [ ] **Step 1: 写失败测试**

在 `RunNormalStateTests` 末尾追加（`work` 为 Main 传入的临时目录）：

```csharp
        // ── Task2: normal_models 建模与状态机 ──
        var nsDb = new Database(Path.Combine(work, "ns.db"));
        Check(nsDb.GetNormalModel("measurement", "M1", "T_VOLT") == null, "初始无模型行");

        for (int i = 0; i < 2; i++)
            NormalModelStore.Observe(nsDb, 3, "measurement", "M1", "T_VOLT", 3.30 + i * 0.01, $"2026-09-0{i + 1} 10:00:00");
        var row = nsDb.GetNormalModel("measurement", "M1", "T_VOLT");
        Check(row != null && row!.Status == "learning" && row.N == 2, "n<minSamples 状态=learning");

        NormalModelStore.Observe(nsDb, 3, "measurement", "M1", "T_VOLT", 3.32, "2026-09-03 10:00:00");
        row = nsDb.GetNormalModel("measurement", "M1", "T_VOLT");
        Check(row != null && row!.Status == "ready" && row.N == 3, "n≥minSamples 状态=ready");
        Check(Math.Abs(row!.Mean - (3.30 + 3.31 + 3.32) / 3) < 1e-9, "模型均值正确");
        Check(row.MinV <= row.P01 && row.P01 <= row.P50 && row.P50 <= row.P99 && row.P99 <= row.MaxV, "分位与极值单调合理");

        var stale = NormalModelStore.EffectiveStatus(row!, new DateTime(2026, 9, 3, 10, 0, 0).AddDays(15), 14);
        Check(stale == "stale", "超 stale_days 惰性判 stale");
        var fresh = NormalModelStore.EffectiveStatus(row!, new DateTime(2026, 9, 5, 10, 0, 0), 14);
        Check(fresh == "ready", "窗口内保持 ready");

        NormalModelStore.Observe(nsDb, 3, "measurement", "M1", "T_VOLT", double.NaN, "2026-09-04 10:00:00");
        Check(nsDb.GetNormalModel("measurement", "M1", "T_VOLT")!.N == 3, "NaN/Inf 不入模（只学有限值）");

        // 主库迁移幂等：构造函数重复打开不抛异常且表存在
        Check(true, "normal_models 建表随 Database.Init 生效（ns.db 构造已隐式验证）");
```

同时在文件靠前处（Main 中 `var d = new Database(db);` 断言之后，约 73 行）追加主库表存在性断言：

```csharp
        int nmTables;
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('normal_models')";
            nmTables = Convert.ToInt32(q.ExecuteScalar());
        }
        Check(nmTables == 1, "normal_models 表已建（主库 Init 迁移）");
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet build FctAggregator/selftest/SelfTest.csproj`
Expected: 编译错误 `NormalModelStore` / `NormalModelRow` 未定义

- [ ] **Step 3: 实现**

`FctAggregator/Db/Database.cs` — 找到 `tdms_features` 的 `CREATE TABLE IF NOT EXISTS` 块（约 280~300 行，含两个索引），在其后追加：

```csharp
                CREATE TABLE IF NOT EXISTS normal_models (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source TEXT NOT NULL,
                    model TEXT NOT NULL,
                    signal_key TEXT NOT NULL,
                    n INTEGER NOT NULL,
                    mean REAL, sigma REAL,
                    p01 REAL, p50 REAL, p99 REAL,
                    min_v REAL, max_v REAL,
                    last_ts TEXT,
                    status TEXT NOT NULL DEFAULT 'learning',
                    qstate TEXT,
                    UNIQUE(source, model, signal_key)
                );
                CREATE INDEX IF NOT EXISTS idx_nm_status ON normal_models(status);
```

> `qstate TEXT` 为规格 §5.1 的实现补充列：持久化 Welford+P² 标记状态，保证跨重启增量学习不断档。

创建 `FctAggregator/Intelligence/NormalModelStore.cs`（SQL 全部收在 `Database`，本类只做纯逻辑编排——遵循仓库惯例）：

```csharp
using System.Globalization;
using System.Text.Json;

namespace FctAggregator;

public sealed class NormalModelRow
{
    public string Source { get; set; } = "";
    public string Model { get; set; } = "";
    public string SignalKey { get; set; } = "";
    public long N { get; set; }
    public double Mean { get; set; }
    public double Sigma { get; set; }
    public double P01 { get; set; }
    public double P50 { get; set; }
    public double P99 { get; set; }
    public double MinV { get; set; } = double.MaxValue;
    public double MaxV { get; set; } = double.MinValue;
    public string LastTs { get; set; } = "";
    public string Status { get; set; } = "learning";
    public string QState { get; set; } = "";   // Welford+P² 持久化状态（JSON）
}

/// <summary>
/// 正常态模型编排：每 (source, model, signal_key) 一行，增量更新 Welford+P²。
/// Observe 读-改-写全程进程内锁；调用方负责吞异常。
/// </summary>
public static class NormalModelStore
{
    public const string SourceMeasurement = "measurement";
    public const string SourceWaveform = "waveform";
    public const string SourceDevice = "device";

    private static readonly object _lock = new();

    public static void Observe(Database db, int minSamples, string source, string model, string signalKey, double value, string ts)
    {
        if (!double.IsFinite(value)) return;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(signalKey)) return;
        model ??= "";
        minSamples = Math.Max(1, minSamples);
        lock (_lock)
        {
            var row = db.GetNormalModel(source, model, signalKey);

            var w = new Welford();
            P2Quantile q1, q5, q9;
            double minV = value, maxV = value;
            if (row != null)
            {
                w.Restore(row.N, row.Mean, ReadM2(row.QState));
                q1 = P2Quantile.Deserialize(ExtractQ(row.QState, "q01")) ?? new P2Quantile(0.01);
                q5 = P2Quantile.Deserialize(ExtractQ(row.QState, "q50")) ?? new P2Quantile(0.5);
                q9 = P2Quantile.Deserialize(ExtractQ(row.QState, "q99")) ?? new P2Quantile(0.99);
                if (row.MinV < minV) minV = row.MinV;
                if (row.MaxV > maxV) maxV = row.MaxV;
            }
            else
            {
                q1 = new P2Quantile(0.01); q5 = new P2Quantile(0.5); q9 = new P2Quantile(0.99);
            }

            w.Update(value);
            q1.Update(value); q5.Update(value); q9.Update(value);
            string status = w.N >= minSamples ? "ready" : "learning";

            var next = new NormalModelRow
            {
                Source = source, Model = model, SignalKey = signalKey,
                N = w.N, Mean = w.Mean, Sigma = w.Sigma,
                P01 = q1.Quantile(), P50 = q5.Quantile(), P99 = q9.Quantile(),
                MinV = minV, MaxV = maxV, LastTs = ts, Status = status,
            };
            next.QState = JsonSerializer.Serialize(new QState
            {
                m2 = w.Snapshot().m2,
                q01 = q1.Serialize(), q50 = q5.Serialize(), q99 = q9.Serialize(),
            });
            db.UpsertNormalModel(next);
        }
    }

    /// <summary>stale 惰性判定：ready 但 last_ts 距 now 超 stale_days → stale（不改库，评分时调用）。</summary>
    public static string EffectiveStatus(NormalModelRow row, DateTime now, int staleDays)
    {
        if (row.Status != "ready") return row.Status;
        if (DateTime.TryParseExact(row.LastTs, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
        {
            if ((now - last).TotalDays > Math.Max(1, staleDays)) return "stale";
        }
        return row.Status;
    }

    private sealed class QState
    {
        public double m2 { get; set; }
        public string? q01 { get; set; }
        public string? q50 { get; set; }
        public string? q99 { get; set; }
    }

    private static double ReadM2(string qstate)
    {
        try { return JsonSerializer.Deserialize<QState>(qstate)?.m2 ?? 0; }
        catch { return 0; }
    }

    private static string? ExtractQ(string qstate, string prop)
    {
        try
        {
            using var doc = System.Text.Json.Documents.JsonDocument.Parse(qstate);
            return doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }
}
```

`FctAggregator/Db/Database.cs` 追加两个公开方法（放在 `GetRecentTdmsFeatures` 附近的存储访问层区域）：

```csharp
    /// <summary>正常态底座：upsert 单个模型行（qstate 为 Welford+P² 状态 JSON）。</summary>
    public void UpsertNormalModel(NormalModelRow r)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO normal_models (source, model, signal_key, n, mean, sigma, p01, p50, p99, min_v, max_v, last_ts, status, qstate)
            VALUES (@s, @m, @k, @n, @mean, @sigma, @p01, @p50, @p99, @minv, @maxv, @ts, @status, @qs)
            ON CONFLICT(source, model, signal_key) DO UPDATE SET
                n=@n, mean=@mean, sigma=@sigma, p01=@p01, p50=@p50, p99=@p99,
                min_v=@minv, max_v=@maxv, last_ts=@ts, status=@status, qstate=@qs";
        cmd.Parameters.AddWithValue("@s", r.Source);
        cmd.Parameters.AddWithValue("@m", r.Model);
        cmd.Parameters.AddWithValue("@k", r.SignalKey);
        cmd.Parameters.AddWithValue("@n", r.N);
        cmd.Parameters.AddWithValue("@mean", r.Mean);
        cmd.Parameters.AddWithValue("@sigma", r.Sigma);
        cmd.Parameters.AddWithValue("@p01", r.P01);
        cmd.Parameters.AddWithValue("@p50", r.P50);
        cmd.Parameters.AddWithValue("@p99", r.P99);
        cmd.Parameters.AddWithValue("@minv", r.MinV);
        cmd.Parameters.AddWithValue("@maxv", r.MaxV);
        cmd.Parameters.AddWithValue("@ts", r.LastTs);
        cmd.Parameters.AddWithValue("@status", r.Status);
        cmd.Parameters.AddWithValue("@qs", r.QState);
        cmd.ExecuteNonQuery();
    }

    /// <summary>正常态底座：按三元组取模型行（含 qstate），无则 null。</summary>
    public NormalModelRow? GetNormalModel(string source, string model, string signalKey)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT source, model, signal_key, n, COALESCE(mean,0), COALESCE(sigma,0), COALESCE(p01,0), COALESCE(p50,0), COALESCE(p99,0),
                   COALESCE(min_v,0), COALESCE(max_v,0), COALESCE(last_ts,''), status, COALESCE(qstate,'')
            FROM normal_models WHERE source=@s AND model=@m AND signal_key=@k";
        cmd.Parameters.AddWithValue("@s", source);
        cmd.Parameters.AddWithValue("@m", model ?? "");
        cmd.Parameters.AddWithValue("@k", signalKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new NormalModelRow
        {
            Source = r.GetString(0), Model = r.GetString(1), SignalKey = r.GetString(2),
            N = r.GetInt64(3), Mean = r.GetDouble(4), Sigma = r.GetDouble(5),
            P01 = r.GetDouble(6), P50 = r.GetDouble(7), P99 = r.GetDouble(8),
            MinV = r.GetDouble(9), MaxV = r.GetDouble(10),
            LastTs = r.GetString(11), Status = r.GetString(12), QState = r.GetString(13),
        };
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet run --project FctAggregator/selftest`
Expected: 新增断言全部 `[OK]`，无任何 `[FAIL]`

- [ ] **Step 5: 提交**

```bash
git add FctAggregator/Intelligence/NormalModelStore.cs FctAggregator/Db/Database.cs FctAggregator/selftest/SelfTest.cs
git commit -m "feat(learn): normal_models table + NormalModelStore with welford/p2 persistence"
```

---

### Task 3: Config 开关（默认关）

**Files:**
- Modify: `FctAggregator/Infra/Config.cs`（属性区 ~68-75 行加属性；Load 解析 ~258-269 行加解析；Save ~433-440 行加写出）
- Test: `FctAggregator/selftest/SelfTest.cs`（`RunNormalStateTests` 开头追加）

**Interfaces:**
- Produces: `AppConfig.LearnNormalEnabled`（默认 `false`）、`AppConfig.LearnNormalMinSamples`（默认 `30`）、`AppConfig.LearnNormalStaleDays`（默认 `14`）；JSON 键 `learn_normal_enabled` / `learn_normal_min_samples` / `learn_normal_stale_days`。Task 4/5 依赖。

- [ ] **Step 1: 写失败测试**

在 `RunNormalStateTests` 最前面加：

```csharp
        // ── Task3: config 默认关闭 ──
        var defCfg = new AppConfig();
        Check(!defCfg.LearnNormalEnabled, "learn_normal_enabled 默认 false");
        Check(defCfg.LearnNormalMinSamples == 30 && defCfg.LearnNormalStaleDays == 14, "正常态默认参数 30/14");
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet build FctAggregator/selftest/SelfTest.csproj`
Expected: 编译错误 `LearnNormalEnabled` 未定义

- [ ] **Step 3: 实现**

`Config.cs` 属性区（`LearnBaselineMinSamples` 之后）：

```csharp
    public bool LearnNormalEnabled { get; set; } = false;
    public int LearnNormalMinSamples { get; set; } = 30;
    public int LearnNormalStaleDays { get; set; } = 14;
```

Load 解析区（`learn_baseline_min_samples` 之后）：

```csharp
                if (root.TryGetProperty("learn_normal_enabled", out v)) cfg.LearnNormalEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_normal_min_samples", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lnm) && lnm >= 1) cfg.LearnNormalMinSamples = lnm;
                if (root.TryGetProperty("learn_normal_stale_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lns) && lns >= 1) cfg.LearnNormalStaleDays = lns;
```

Save 写出区（`learn_baseline_min_samples` 之后）：

```csharp
                writer.WriteBoolean("learn_normal_enabled", LearnNormalEnabled);
                writer.WriteNumber("learn_normal_min_samples", LearnNormalMinSamples);
                writer.WriteNumber("learn_normal_stale_days", LearnNormalStaleDays);
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet run --project FctAggregator/selftest`
Expected: 全绿（含既有 config 往返断言，证明 Save/Load 对称）

- [ ] **Step 5: 提交**

```bash
git add FctAggregator/Infra/Config.cs FctAggregator/selftest/SelfTest.cs
git commit -m "feat(learn): normal-state config switches (default off)"
```

---

### Task 4: NormalLearners —— 三个学习器 + 心跳计数

**Files:**
- Create: `FctAggregator/Intelligence/NormalLearners.cs`
- Test: `FctAggregator/selftest/SelfTest.cs`（`RunNormalStateTests` 追加）

**Interfaces:**
- Consumes: Task2 `NormalModelStore.Observe(db, minSamples, source, model, key, value, ts)`；`MeasurementRow`（`TestName/Value`）、`TdmsFeatureAnalyzer.TdmsFeatureRow`（`GroupName/ChannelName/Mean/Std/Max`）、`TimeSlot.SlotOfHour(int hour)`、`Database.GetMeta/SetMeta`
- Produces:
  - `NormalLearners.ObserveMeasurement(Database db, AppConfig cfg, TestRecord rec)` — PASS 过滤后逐 `MeasurementRow` 建模，键 `(measurement, rec.Model, TestName)`，ts 用 `rec.BatchTimestamp`
  - `NormalLearners.ObserveWaveform(Database db, AppConfig cfg, TestRecord rec, List<TdmsFeatureAnalyzer.TdmsFeatureRow> rows)` — PASS 过滤，对每通道 vmean/vstd/vmax 建子键 `{group}/{channel}/vmean|vstd|vmax`
  - `NormalLearners.ObserveDeviceSample(Database db, AppConfig cfg, double cpu, double memPct, double diskFreeGb, string? ts = null)` — 键 `(device, "", cpu|mem|disk@slot)`
  - `string SnapshotHeartbeat(Database db)` — 内存计数落 `app_meta` 键 `learn_normal_heartbeat` 并返回 JSON；计数每 200 次 observe 自动落库一次
- Task 5 在三个调用点接入这些方法；Task 6 断言行为。

- [ ] **Step 1: 写失败测试**

`RunNormalStateTests` 追加：

```csharp
        // ── Task4: 学习器行为 ──
        var lrDb = new Database(Path.Combine(work, "lr.db"));
        var lrCfg = new AppConfig { LearnNormalEnabled = true, LearnNormalMinSamples = 2 };

        var pass = new TestRecord { Model = "G49", Result = "PASS", Sn = "SN1", BatchTimestamp = "2026-09-07 10:00:00" };
        pass.Measurements.Add(new MeasurementRow { TestName = "T_VOLT", Value = 3.3 });
        NormalLearners.ObserveMeasurement(lrDb, lrCfg, pass);
        NormalLearners.ObserveMeasurement(lrDb, lrCfg, pass);
        var m = lrDb.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_VOLT");
        Check(m != null && m!.Status == "ready", "测量学习器：PASS 数据入模");

        var failRec = new TestRecord { Model = "G49", Result = "FAIL", Sn = "SN2", BatchTimestamp = "2026-09-07 10:01:00" };
        failRec.Measurements.Add(new MeasurementRow { TestName = "T_VOLT", Value = 9.9 });
        NormalLearners.ObserveMeasurement(lrDb, lrCfg, failRec);
        m = lrDb.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_VOLT");
        Check(m != null && m!.N == 2 && m.Mean < 3.4, "FAIL 数据不入模（正常态不被污染）");

        var wfCfg = lrCfg;
        var wfRec = new TestRecord { Model = "G49", Result = "PASS", Sn = "SN3", BatchTimestamp = "2026-09-07 10:02:00" };
        var wfRows = new List<TdmsFeatureAnalyzer.TdmsFeatureRow>
            { new() { GroupName = "G1", ChannelName = "CH1", Mean = 1.2, Std = 0.1, Max = 2.0 } };
        NormalLearners.ObserveWaveform(lrDb, wfCfg, wfRec, wfRows);
        Check(lrDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vmean") != null, "波形学习器：vmean 建模");
        Check(lrDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vstd") != null, "波形学习器：vstd 建模");
        Check(lrDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vmax") != null, "波形学习器：vmax 建模");

        NormalLearners.ObserveDeviceSample(lrDb, lrCfg, 15.5, 45.0, 120.0, "2026-09-07 23:30:00");
        Check(lrDb.GetNormalModel(NormalModelStore.SourceDevice, "", "cpu@3") != null, "设备学习器：按时段建键（23 点→slot3）");

        NormalLearners.FlushHeartbeat(lrDb);
        var hb = lrDb.GetMeta("learn_normal_heartbeat");
        Check(!string.IsNullOrWhiteSpace(hb) && hb!.Contains("measurement") && hb.Contains("errors"), "心跳落库 learn_normal_heartbeat");

        // 开关关闭全链路 no-op
        var offDb = new Database(Path.Combine(work, "off.db"));
        var offCfg = new AppConfig { LearnNormalEnabled = false };
        NormalLearners.ObserveMeasurement(offDb, offCfg, pass);
        NormalLearners.ObserveWaveform(offDb, offCfg, pass, wfRows);
        NormalLearners.ObserveDeviceSample(offDb, offCfg, 10, 40, 100, "2026-09-07 08:30:00");
        Check(offDb.GetNormalModel(NormalModelStore.SourceMeasurement, "G49", "T_VOLT") == null
           && offDb.GetNormalModel(NormalModelStore.SourceWaveform, "G49", "G1/CH1/vmean") == null
           && offDb.GetNormalModel(NormalModelStore.SourceDevice, "", "cpu@1") == null,
            "开关关闭：三学习器全部 no-op");
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet build FctAggregator/selftest/SelfTest.csproj`
Expected: 编译错误 `NormalLearners` 未定义

- [ ] **Step 3: 实现**

创建 `FctAggregator/Intelligence/NormalLearners.cs`：

```csharp
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 正常态自学习入口：三个学习器（测量/波形/设备行为）。
/// 铁律：开关关=纯 no-op；只学 PASS；只学有限值；吞异常只计数，绝不阻塞摄取链路。
/// </summary>
public static class NormalLearners
{
    public const string MetaHeartbeat = "learn_normal_heartbeat";
    private const int FlushEveryUpdates = 200;

    private static readonly object _hbLock = new();
    private static long _mUpd, _wUpd, _dUpd, _mErr, _wErr, _dErr;
    private static long _sinceFlush;

    public static void ObserveMeasurement(Database db, AppConfig cfg, TestRecord rec)
    {
        if (!cfg.LearnNormalEnabled || db == null) return;
        try
        {
            if (!string.Equals(rec.Result, "PASS", StringComparison.OrdinalIgnoreCase)) return;
            var ts = string.IsNullOrWhiteSpace(rec.BatchTimestamp)
                ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                : TimeUtil.Normalize(rec.BatchTimestamp);
            if (ts.Length == 0) ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var model = rec.Model ?? "";
            foreach (var m in rec.Measurements)
            {
                if (m.Value is not { } v || !double.IsFinite(v)) continue;
                if (string.IsNullOrWhiteSpace(m.TestName)) continue;
                NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceMeasurement, model, m.TestName, v, ts);
                Interlocked.Increment(ref _mUpd);
            }
            MaybeFlush(db);
        }
        catch (Exception ex) { Interlocked.Increment(ref _mErr); Logger.Warning($"[正常态] 测量学习异常(已吞并): {ex.Message}"); }
    }

    public static void ObserveWaveform(Database db, AppConfig cfg, TestRecord rec, List<TdmsFeatureAnalyzer.TdmsFeatureRow> rows)
    {
        if (!cfg.LearnNormalEnabled || db == null || rows == null || rows.Count == 0) return;
        try
        {
            if (!string.Equals(rec.Result, "PASS", StringComparison.OrdinalIgnoreCase)) return;
            var ts = string.IsNullOrWhiteSpace(rec.BatchTimestamp)
                ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                : TimeUtil.Normalize(rec.BatchTimestamp);
            if (ts.Length == 0) ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var model = rec.Model ?? "";
            foreach (var f in rows)
            {
                if (string.IsNullOrWhiteSpace(f.GroupName) || string.IsNullOrWhiteSpace(f.ChannelName)) continue;
                var baseKey = $"{f.GroupName}/{f.ChannelName}";
                if (f.Mean is { } vm && double.IsFinite(vm))
                { NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceWaveform, model, baseKey + "/vmean", vm, ts); Interlocked.Increment(ref _wUpd); }
                if (f.Std is { } vs && double.IsFinite(vs))
                { NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceWaveform, model, baseKey + "/vstd", vs, ts); Interlocked.Increment(ref _wUpd); }
                if (f.Max is { } vx && double.IsFinite(vx))
                { NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceWaveform, model, baseKey + "/vmax", vx, ts); Interlocked.Increment(ref _wUpd); }
            }
            MaybeFlush(db);
        }
        catch (Exception ex) { Interlocked.Increment(ref _wErr); Logger.Warning($"[正常态] 波形学习异常(已吞并): {ex.Message}"); }
    }

    public static void ObserveDeviceSample(Database db, AppConfig cfg, double cpu, double memPct, double diskFreeGb, string? ts = null)
    {
        if (!cfg.LearnNormalEnabled || db == null) return;
        try
        {
            var t = string.IsNullOrWhiteSpace(ts) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : TimeUtil.Normalize(ts);
            if (t.Length < 13) t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var slot = TimeSlot.SlotOfHour(int.Parse(t.Substring(11, 2)));
            foreach (var (name, val) in new[] { ("cpu", cpu), ("mem", memPct), ("disk", diskFreeGb) })
            {
                if (!double.IsFinite(val)) continue;
                NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceDevice, "", $"{name}@{slot}", val, t);
                Interlocked.Increment(ref _dUpd);
            }
            MaybeFlush(db);
        }
        catch (Exception ex) { Interlocked.Increment(ref _dErr); Logger.Warning($"[正常态] 设备学习异常(已吞并): {ex.Message}"); }
    }

    /// <summary>计数达阈值时落库；提供显式 Flush 供 selftest/UI 用。</summary>
    private static void MaybeFlush(Database db)
    {
        if (Interlocked.Increment(ref _sinceFlush) % FlushEveryUpdates == 0) FlushHeartbeat(db);
    }

    public static string FlushHeartbeat(Database db)
    {
        lock (_hbLock)
        {
            var json = JsonSerializer.Serialize(new
            {
                measurement = new { updates = _mUpd, errors = _mErr },
                waveform = new { updates = _wUpd, errors = _wErr },
                device = new { updates = _dUpd, errors = _dErr },
                flushed_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            try { db?.SetMeta(MetaHeartbeat, json); } catch { }
            return json;
        }
    }
}
```

> 执行注意：设备学习器 ts 解析若遇非标准格式，`int.Parse` 抛异常由外层 catch 吞并计数——这是可接受行为（采样周期 5 分钟，丢一笔无碍）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet run --project FctAggregator/selftest`
Expected: `[正常态]` 分组全部 `[OK]`（含开关关闭 no-op 断言）

- [ ] **Step 5: 提交**

```bash
git add FctAggregator/Intelligence/NormalLearners.cs FctAggregator/selftest/SelfTest.cs
git commit -m "feat(learn): three normal-state learners + heartbeat counters"
```

---

### Task 5: 摄取链路挂接（Engine / TdmsFeatureCollector / DeviceSampleRecorder）

**Files:**
- Modify: `FctAggregator/Core/Engine.cs`（`ProcessRealtime` 内 `_db.InsertOne(rec);` 成功后，约 315 行）
- Modify: `FctAggregator/Intelligence/TdmsFeatureCollector.cs`（`CaptureOne` 内 `db.InsertTdmsFeaturesFor(rec, recordId, path, rows);` 之后，约 89 行）
- Modify: `FctAggregator/Intelligence/DeviceSampleRecorder.cs`（`RecordOnce` 内 `db.InsertLocalDeviceSample(cpu, memPct, diskFree);` 之后，约 113 行）

**Interfaces:**
- Consumes: Task4 的三个 `Observe*` 方法
- Produces: 无新接口——挂接点生效即交付（开 config 后实测数据入模）

- [ ] **Step 1: 接入测量学习钩子**

`Engine.cs` `ProcessRealtime` 中，定位：

```csharp
        try
        {
            _db.InsertOne(rec);
            Logger.Info($"[入库] {rec.Model} | {rec.Result} | {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Logger.Error($"[错误] 入库失败: {path} | {ex.Message}"); return; }
```

在 try/catch 块之后、`// 规格06期3：TDMS 特征化实时入队` 注释之前插入：

```csharp
        // 正常态自学习：PASS 测量值入模（开关关 no-op，吞异常不阻塞采集）
        NormalLearners.ObserveMeasurement(_db, _cfg, rec);
```

- [ ] **Step 2: 接入波形学习钩子**

`TdmsFeatureCollector.cs` `CaptureOne` 中，定位：

```csharp
            var rows = TdmsFeatureAnalyzer.Compute(channels, cfg.AnalyzeTdmsMaxChannels);
            Interlocked.Increment(ref FilesOk);
            if (rows.Count == 0 || db == null) return rows.Count;
            db.InsertTdmsFeaturesFor(rec, recordId, path, rows);
            return rows.Count;
```

将 `db.InsertTdmsFeaturesFor(...);` 之后追加一行：

```csharp
            NormalLearners.ObserveWaveform(db, cfg, rec, rows);
```

- [ ] **Step 3: 接入设备学习钩子**

`DeviceSampleRecorder.cs` `RecordOnce` 中，定位：

```csharp
            var db = Database.Current;
            if (db != null)
            {
                db.InsertLocalDeviceSample(cpu, memPct, diskFree);
            }
```

改为：

```csharp
            var db = Database.Current;
            if (db != null)
            {
                db.InsertLocalDeviceSample(cpu, memPct, diskFree);
                NormalLearners.ObserveDeviceSample(db, AppConfig.Instance, cpu, memPct, diskFree);
            }
```

- [ ] **Step 4: 构建 + 全量自检（重点回归既有断言）**

Run: `dotnet build FctAggregator/FctAggregator.csproj` 然后 `dotnet run --project FctAggregator/selftest`
Expected: 构建零警告零错误；全部断言 `[OK]`（挂接为纯新增调用，默认关不影响任何既有行为）

- [ ] **Step 5: 手动冒烟（可选但推荐）**

临时把 `dist/data/config.json` 加 `"learn_normal_enabled": true`，跑一次主程序观察日志无 `[正常态]` 异常告警、`app_meta` 出现 `learn_normal_heartbeat`。验证后**还原 config**（默认关交付）。

- [ ] **Step 6: 提交**

```bash
git add FctAggregator/Core/Engine.cs FctAggregator/Intelligence/TdmsFeatureCollector.cs FctAggregator/Intelligence/DeviceSampleRecorder.cs
git commit -m "feat(learn): wire normal-state learners into ingestion chain"
```

---

### Task 6: selftest 补全 + 交付验证

**Files:**
- Modify: `FctAggregator/selftest/SelfTest.cs`（补齐 Task2/3 遗留断言已在前序任务完成；本任务只做全量验证与收尾）

**Interfaces:**
- Consumes: 前五个任务全部产物
- Produces: 全绿 selftest（961 基线 + `[正常态]` 新增断言）

- [ ] **Step 1: 全量构建（主程序 + 自检项目）**

Run: `dotnet build FctAggregator/FctAggregator.csproj` 与 `dotnet build FctAggregator/selftest/SelfTest.csproj`
Expected: 零警告零错误

- [ ] **Step 2: 全量自检**

Run: `dotnet run --project FctAggregator/selftest`
Expected: 末行 `==== 全部通过 ====`；`【正常态自学习】` 分组约 20 条断言全 `[OK]`

- [ ] **Step 3: ps1 脚本无涉，确认版本号策略**

P1 属功能开发，发布时按仓库惯例 bump 版本（v3.30.0）——本任务不改 csproj（版本随发布打包任务统一处理，见 `scripts/make_release.ps1` 流程）。

- [ ] **Step 4: 汇总提交状态**

```bash
git log --oneline -8
git status
```

Expected: 5 个 feat 提交 + 工作区干净。P1 完成，P2（回填+偏离评分）另立计划。
