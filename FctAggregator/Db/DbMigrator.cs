using Microsoft.Data.Sqlite;

namespace FctAggregator;

public static class DbMigrator
{
    public const int LatestVersion = 13;

    private static readonly (int Version, string Name, string[] Sql)[] Migrations =
    {
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
        (
            12,
            "agg_records.fixture_id 列（规格01 fixture 归因真治具 ID）",
            new string[]
            {
                @"ALTER TABLE agg_records ADD COLUMN fixture_id TEXT;",
            }
        ),
        (
            13,
            "device_samples_local 本机设备自采样历史表（规格05 机台自学习）",
            new string[]
            {
                @"CREATE TABLE IF NOT EXISTS device_samples_local (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts TEXT NOT NULL,
                    cpu_usage REAL NOT NULL,
                    mem_used_pct REAL NOT NULL,
                    disk_free_gb REAL NOT NULL
                );",
                @"CREATE INDEX IF NOT EXISTS idx_device_samples_local_ts ON device_samples_local(ts);",
            }
        ),
    };

    public static void Migrate(SqliteConnection conn)
    {
        int current = GetUserVersion(conn);
        if (current >= LatestVersion) return;

        foreach (var (version, name, sqls) in Migrations)
        {
            if (version <= current) continue;
            using var tx = conn.BeginTransaction(deferred: false);
            try
            {
                foreach (var sql in sqls)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                SetUserVersion(conn, tx, version);
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                throw new InvalidOperationException($"聚合库 schema 迁移到 v{version}（{name}）失败，已回滚：{ex.Message}", ex);
            }
        }
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection conn, SqliteTransaction tx, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }
}