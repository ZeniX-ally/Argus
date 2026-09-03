using Microsoft.Data.Sqlite;

namespace FctAggregator;

/// <summary>
/// 聚合库 schema 版本化迁移：用 PRAGMA user_version 记录当前版本，
/// 把建表/建索引 DDL 从 AggDatabase.Open() 的硬编码抽成有序迁移数组。
/// 后续新增表（yld_daily/users/audit 等）在 <see cref="Migrations"/> 末尾追加条目即可：
/// 老库自动从当前版本逐级升到 <see cref="LatestVersion"/>，任一级失败整体回滚并抛给调用方。
/// </summary>
public static class DbMigrator
{
    /// <summary>当前期望的 schema 版本（= 迁移数组最后一项的版本号）；库已达该值即视为最新。</summary>
    public const int LatestVersion = 12;

    /// <summary>
    /// 有序迁移列表：Version 严格升序、与库的 user_version 一一对应；
    /// 每项在独立事务内执行（见 <see cref="Migrate"/>）。
    /// </summary>
    private static readonly (int Version, string Name, string[] Sql)[] Migrations =
    {
        // v1：agg_records 初始表 —— v3.5.3 既有 DDL 原样迁入。
        // 注意兼容性（v3.8.0 bugfix）：老库 user_version=0 且 agg_records 已存在（v3.5.3 直接
        // CREATE TABLE IF NOT EXISTS，从未写 user_version），若用精确 CREATE 会报
        // 「table agg_records already exists」导致 Open 崩 → 恢复 IF NOT EXISTS 语义：
        // 全新库建表、老库跳过建表只补索引，再置 user_version=1，两场景幂等收敛。
        (
            1,
            "agg_records 初始表（v3.5.3 既有 DDL 原样迁入）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS agg_records (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  machine TEXT NOT NULL,
                  seq INTEGER NOT NULL,
                  type TEXT NOT NULL DEFAULT 'fail',
                  ts TEXT, ingest_ts TEXT,
                  station_id TEXT, model TEXT, category TEXT, test_date TEXT, sn TEXT,
                  result TEXT, fail_reason TEXT, tester TEXT, panel_status TEXT,
                  batch_timestamp TEXT, has_fail_items INTEGER, file_size INTEGER, xml_path TEXT,
                  UNIQUE(machine, seq)
                );",
                @"CREATE INDEX IF NOT EXISTS idx_agg_records_ingest ON agg_records(ingest_ts);",
                @"CREATE INDEX IF NOT EXISTS idx_agg_records_machine ON agg_records(machine);",
                @"CREATE INDEX IF NOT EXISTS idx_agg_records_ingest_id ON agg_records(ingest_ts, id);",
                @"CREATE INDEX IF NOT EXISTS idx_agg_records_machine_id ON agg_records(machine, id);",
                @"CREATE INDEX IF NOT EXISTS idx_agg_records_date_id ON agg_records(test_date, id);",
            }
        ),
        // v2：良率日统计（总纲 P1 D1）。机台心跳携带今日 total/pass/fail/interrupted/products，
        // 聚合端按 (machine, test_date) 幂等 upsert —— 良率 = pass/total，不再推全量 PASS 明细。
        (
            2,
            "yld_daily 良率日统计表（心跳携带日统计，总纲 P1 D1）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS yld_daily (
                  machine TEXT NOT NULL,
                  test_date TEXT NOT NULL,
                  total INTEGER NOT NULL DEFAULT 0,
                  pass INTEGER NOT NULL DEFAULT 0,
                  fail INTEGER NOT NULL DEFAULT 0,
                  interrupted INTEGER NOT NULL DEFAULT 0,
                  products INTEGER NOT NULL DEFAULT 0,
                  updated_ts TEXT NOT NULL DEFAULT '',
                  PRIMARY KEY (machine, test_date)
                );",
            }
        ),
        // v3：权限与审计（总纲 P1 D3）。users 三角色 viewer/engineer/admin，
        // token 为每用户静态令牌（与 agg_token 同通道：header/query/cookie 均可携带）；
        // audit_log 记录导出/设置/删除等高危操作（who/when/what），admin 可查。
        (
            3,
            "users 三角色 + audit_log 审计表（总纲 P1 D3）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS users (
                  name TEXT PRIMARY KEY COLLATE NOCASE,
                  pwd_hash TEXT NOT NULL,
                  role TEXT NOT NULL DEFAULT 'viewer',
                  token TEXT NOT NULL DEFAULT '',
                  layout TEXT,
                  favorites TEXT,
                  created_at TEXT NOT NULL DEFAULT ''
                );",
                @"CREATE TABLE IF NOT EXISTS audit_log (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  ts TEXT NOT NULL,
                  who TEXT NOT NULL,
                  action TEXT NOT NULL,
                  detail TEXT NOT NULL DEFAULT ''
                );",
                @"CREATE INDEX IF NOT EXISTS idx_audit_ts ON audit_log(ts);",
            }
        ),
        // v4：维修/待办全功能（总纲 P5，线上Dashboard对照 §2.3）：聚合端服务端化 ——
        // maintenance_records / todo_items / dismissed_todos / resolvers 四表 + app_meta
        // 与机台本地库 Database.Init() 同构，保证口径一致（状态机、合并、导出 12 列均同源逻辑）。
        // 聚合端维护集中看板，机台本地仍保留同表（独立文件，离线可用）；迁移幂等、无损老数据。
        (
            4,
            "维修/待办四表（P5 服务端化，聚合端集中看板）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS maintenance_records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    station_id TEXT,
                    equipment_model TEXT,
                    equipment_sn TEXT,
                    fail_item TEXT NOT NULL,
                    fail_reason TEXT,
                    severity TEXT DEFAULT 'major',
                    status TEXT DEFAULT 'open',
                    resolver TEXT,
                    resolution TEXT,
                    notes TEXT,
                    created_at TEXT DEFAULT (datetime('now','localtime')),
                    updated_at TEXT DEFAULT (datetime('now','localtime'))
                );",
                @"CREATE INDEX IF NOT EXISTS idx_maint_status ON maintenance_records(status);",
                @"CREATE INDEX IF NOT EXISTS idx_maint_updated ON maintenance_records(updated_at);",
                @"CREATE INDEX IF NOT EXISTS idx_maint_station ON maintenance_records(station_id);",
                @"CREATE TABLE IF NOT EXISTS resolvers (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    created_at TEXT DEFAULT (datetime('now','localtime'))
                );",
                @"CREATE TABLE IF NOT EXISTS dismissed_todos (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    fail_item TEXT NOT NULL,
                    station_id TEXT,
                    model TEXT,
                    dismissed_at TEXT DEFAULT (datetime('now','localtime'))
                );",
                @"CREATE INDEX IF NOT EXISTS idx_dismissed_item ON dismissed_todos(fail_item);",
                @"CREATE TABLE IF NOT EXISTS todo_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    group_key TEXT NOT NULL,
                    station_id TEXT NOT NULL DEFAULT '',
                    title TEXT NOT NULL,
                    model TEXT,
                    variants TEXT,
                    variant_count INTEGER NOT NULL DEFAULT 1,
                    fail_count INTEGER NOT NULL DEFAULT 0,
                    first_seen TEXT,
                    last_seen TEXT,
                    state TEXT NOT NULL DEFAULT 'pending',
                    maintenance_id INTEGER,
                    resolved_at TEXT,
                    created_at TEXT DEFAULT (datetime('now','localtime')),
                    updated_at TEXT DEFAULT (datetime('now','localtime'))
                );",
                @"CREATE UNIQUE INDEX IF NOT EXISTS idx_todo_group ON todo_items(group_key, station_id);",
                @"CREATE INDEX IF NOT EXISTS idx_todo_state ON todo_items(state);",
                @"CREATE TABLE IF NOT EXISTS app_meta (
                    k TEXT PRIMARY KEY,
                    v TEXT
                );",
                @"CREATE TABLE IF NOT EXISTS todo_sync_state (
                    origin_machine TEXT NOT NULL,
                    todo_id INTEGER NOT NULL,
                    owner TEXT,
                    state TEXT,
                    version INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT,
                    PRIMARY KEY (origin_machine, todo_id)
                );",
            }
        ),
        // v5：设备监控（总纲 P6，设备监控面板 D1-D3）：device_info 快照 + device_samples 趋势 + device_fct 详情；
        // ≤30 台规模，5 分钟采样 8640 行/天，无压力；硬件约束：单写者 WAL、不走 mesh 补偿队列、不参与 (machine,seq) 幂等。
        (
            5,
            "设备监控三表（P6 设备监控，D1-D3）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS device_info (
                    machine TEXT PRIMARY KEY,
                    hostname TEXT, os TEXT, os_version TEXT, ip TEXT, mac TEXT,
                    cpu_model TEXT, cpu_cores INTEGER, cpu_usage REAL,
                    mem_total_mb INTEGER, mem_used_mb INTEGER,
                    disk_total_gb REAL, disk_free_gb REAL,
                    uptime_sec INTEGER, argus_version TEXT,
                    last_seen TEXT, updated_at TEXT
                );",
                @"CREATE TABLE IF NOT EXISTS device_samples (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    machine TEXT NOT NULL, ts TEXT NOT NULL,
                    cpu_usage REAL, mem_used_mb INTEGER, disk_free_gb REAL
                );",
                @"CREATE INDEX IF NOT EXISTS idx_samples_machine_ts ON device_samples(machine, ts);",
                @"CREATE INDEX IF NOT EXISTS idx_samples_ts ON device_samples(ts);",
                @"CREATE TABLE IF NOT EXISTS device_fct (
                    machine TEXT PRIMARY KEY,
                    ini_path TEXT, found INTEGER DEFAULT 0, error TEXT,
                    models TEXT, fw_versions TEXT, devices TEXT, a2l_files TEXT,
                    last_seen TEXT, updated_at TEXT
                );",
            }
        ),
        // v6：程序调整日志 + 报告归档（P7 报告中心完整 + P7 程序日志，Lite-Fetch，2026-08-28）
        // proc_change_log 按《测试工程师Dashboard功能规划》§7 D4 定义；report_archive 用于报告归档与版本对比留痕。
        (
            6,
            "程序调整日志 + 报告归档（P7 报告/日志，Lite-Fetch）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS proc_change_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    version TEXT NOT NULL,
                    changed_at TEXT NOT NULL,
                    changed_by TEXT,
                    content TEXT,
                    scope_machines TEXT,
                    params_snapshot TEXT,
                    related_reports TEXT,
                    created_at TEXT DEFAULT (datetime('now','localtime'))
                );",
                @"CREATE INDEX IF NOT EXISTS idx_proc_log_at ON proc_change_log(changed_at);",
                @"CREATE INDEX IF NOT EXISTS idx_proc_log_version ON proc_change_log(version);",
                @"CREATE TABLE IF NOT EXISTS report_archive (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    machine TEXT, sn TEXT, model TEXT, test_date TEXT, result TEXT,
                    xml_path TEXT, archived_path TEXT, archived_at TEXT, archived_by TEXT, note TEXT,
                    summary_json TEXT
                );",
                @"CREATE INDEX IF NOT EXISTS idx_report_archive_machine ON report_archive(machine);",
                @"CREATE INDEX IF NOT EXISTS idx_report_archive_date ON report_archive(test_date);",
            }
        ),
        // v7：告警历史（P8 告警规则中心，Lite-Infra）：yield/disk/cpu/offline 统一落库
        // 规则阈值走 Config 的 device_alert_* + yield_alert_*，历史供 /api/alerts/history 与前端告警页
        (
            7,
            "告警历史表（P8 告警规则中心，yield/disk/cpu/offline）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS alert_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    machine TEXT NOT NULL,
                    rule TEXT NOT NULL,
                    level TEXT NOT NULL DEFAULT 'warn',
                    metric TEXT,
                    detail TEXT
                );",
                @"CREATE INDEX IF NOT EXISTS idx_alert_ts ON alert_history(ts);",
                @"CREATE INDEX IF NOT EXISTS idx_alert_machine ON alert_history(machine);",
                @"CREATE INDEX IF NOT EXISTS idx_alert_rule ON alert_history(rule);",
            }
        ),
        // v8 domain4
        (
            8,
            "v8 domain4",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS fct_change_log (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, machine TEXT NOT NULL, detail TEXT NOT NULL DEFAULT '', hash TEXT NOT NULL DEFAULT '');",
                @"CREATE INDEX IF NOT EXISTS idx_fct_change_machine ON fct_change_log(machine);",
                @"CREATE INDEX IF NOT EXISTS idx_fct_change_ts ON fct_change_log(ts);",
                @"CREATE TABLE IF NOT EXISTS device_predict_log (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, machine TEXT NOT NULL, metric TEXT NOT NULL, level TEXT NOT NULL DEFAULT 'warn', predicted REAL, days_to_exhaust INTEGER, detail TEXT);",
                @"CREATE INDEX IF NOT EXISTS idx_predict_machine ON device_predict_log(machine);",
                @"CREATE INDEX IF NOT EXISTS idx_predict_ts ON device_predict_log(ts);",
            }
        ),
        // v9 告警预测（domain9）
        (
            9,
            "v9 alert_predict",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS alert_predict_log (id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, machine TEXT NOT NULL, rule TEXT NOT NULL, level TEXT NOT NULL DEFAULT 'warn', current REAL, predicted REAL, detail TEXT);",
                @"CREATE INDEX IF NOT EXISTS idx_alert_pred_machine ON alert_predict_log(machine);",
                @"CREATE INDEX IF NOT EXISTS idx_alert_pred_ts ON alert_predict_log(ts);",
            }
        ),
        // v10 预测准确率对账表（规格 02）：把 alert_predict_log / device_predict_log 的预测
        // 与后来实际值对账，落 (predict_table, predict_id) UNIQUE 一行。
        // predict_table = 'alert' | 'device'；rule 在 alert_predict_log 中是 yield，
        // 在 device_predict_log 中是 metric（cpu/disk/offline）—— 对账层做归一。
        // 滚动保留 180 天（DbMaintenance.RunNow 顺带清理）；hit / lead_days / actual_value 供 /api/predict/accuracy 与阈值自调。
        (
            10,
            "predict_accuracy_log 预测准确率对账表（规格 02）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS predict_accuracy_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    rule TEXT NOT NULL,
                    machine TEXT NOT NULL,
                    predict_id INTEGER NOT NULL,
                    predict_table TEXT NOT NULL,
                    predicted_value REAL,
                    actual_value REAL,
                    threshold REAL,
                    hit INTEGER NOT NULL,
                    lead_days REAL,
                    predicted_at TEXT NOT NULL,
                    reconciled_at TEXT NOT NULL,
                    note TEXT
                );",
                @"CREATE UNIQUE INDEX IF NOT EXISTS idx_acc_unique ON predict_accuracy_log(predict_table, predict_id);",
                @"CREATE INDEX IF NOT EXISTS idx_acc_rule_time ON predict_accuracy_log(rule, reconciled_at);",
                @"CREATE INDEX IF NOT EXISTS idx_acc_machine_rule ON predict_accuracy_log(machine, rule);",
            }
        ),
        // v11 慢查询索引补齐（v3.21.1 P1）：idx_agg_date, idx_agg_model, idx_agg_fixture, idx_yld_daily_date
        (
            11,
            "慢查询索引补齐（P1 性能优化）",
            new string[]
            {
                @"CREATE INDEX IF NOT EXISTS idx_agg_date ON agg_records(test_date);",
                @"CREATE INDEX IF NOT EXISTS idx_agg_model ON agg_records(model);",
                @"CREATE INDEX IF NOT EXISTS idx_agg_fixture ON agg_records(fail_reason);",
                @"CREATE INDEX IF NOT EXISTS idx_yld_daily_date ON yld_daily(test_date);",
            }
        ),
        // v12 fixture_id 列（v3.22.0 规格01 fixture 归因补全）：agg_records 增加真治具 ID。
        // 老行为兼容：列可空，老数据/老对端为 NULL → DecomposeByFixture 回落 fail_reason 前缀分组（原口径不变）。
        (
            12,
            "agg_records.fixture_id 列（规格01 fixture 归因真治具 ID）",
            new string[]
            {
                @"ALTER TABLE agg_records ADD COLUMN fixture_id TEXT;",
            }
        ),
    };

    /// <summary>
    /// 把连接所在库迁移到 <see cref="LatestVersion"/>（幂等：当前版本 >= 目标直接返回，Close 后重开也只会补跑）。
    /// 从当前版本 + 1 起逐级执行：BEGIN IMMEDIATE → 执行该版本全部 SQL 语句 → PRAGMA user_version = 版本号 → COMMIT；
    /// 任一语句失败 → ROLLBACK 并把异常抛给调用方（不吞、不降级继续），错误信息带版本号与名称。
    /// </summary>
    public static void Migrate(SqliteConnection conn)
    {
        int current = GetUserVersion(conn);
        if (current >= LatestVersion) return;   // 幂等：已是最新，不做任何事

        foreach (var (version, name, sqls) in Migrations)
        {
            if (version <= current) continue;   // 只升不降；防御性跳过（数组升序，正常不会触发）
            using var tx = conn.BeginTransaction(deferred: false);  // deferred:false = BEGIN IMMEDIATE，先取写锁
            try
            {
                foreach (var sql in sqls)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                SetUserVersion(conn, tx, version);  // user_version 随事务提交，失败随回滚撤销
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();  // 该版本未完成：整体回滚，不降级继续，异常上抛
                throw new InvalidOperationException($"聚合库 schema 迁移到 v{version}（{name}）失败，已回滚：{ex.Message}", ex);
            }
        }
    }

    /// <summary>读当前 user_version（新库缺省 0）。</summary>
    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>把 user_version 写入指定事务（随 COMMIT 落盘，回滚时一并撤销）。</summary>
    private static void SetUserVersion(SqliteConnection conn, SqliteTransaction tx, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA user_version = {version};";  // PRAGMA 不走参数化，版本号来自代码常量，无注入面
        cmd.ExecuteNonQuery();
    }
}