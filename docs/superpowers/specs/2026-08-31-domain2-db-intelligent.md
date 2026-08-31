# 域 2 本地数据库智能化 Spec — 查询/巡检/分层/备份（离线零第三方）

> 版本：v1.0（2026-08-31）｜4 项全做，与域 1 同版本发布

## 1. 目标

- A 查询智能：慢查询日志 + 自动索引建议
- B 健康巡检：完整性 + 冗余/缺失检测
- C 冷热分层：按 TestDate 自动归档/清理
- D 备份自愈：增量校验 + 损坏自愈

## 2. 设计

**A 查询智能：**
- DbQueryLogger：记录 >500ms 查询，落 db_slow_log(machine, sql, ms, ts)
- IndexAdvisor：统计高频 WHERE(TestDate, Machine, FailReason)，缺索引时提示 CREATE INDEX，建议仅日志不自动建

**B 健康巡检：**
- 每日 02:00 巡检：PRAGMA integrity_check + 外键 + UNIQUE 冲突计数 → db_health_log
- 异常自动标记 db_suspect，UI 提示

**C 冷热分层：**
- 热 0-30天 在线，温 30-90天 索引保留，冷 >90天 导出 data/archive/test_records_yyyyMM.db 并 DELETE，todo_items/maintenance 永久保留

**D 备份自愈：**
- BackupDaily 保留 7 份，增加 SHA256 校验文件 .sha256
- 启动时若 SQLITE_CORRUPT，自动还原最近有效备份并日志告警

## 3. 存储与接口

- 新表：db_slow_log, db_health_log
- API：GET /api/db/health, GET /api/db/slow

## 4. 测试与发布

- 单元：慢查询记录、归档边界、分层保留
- 版本：v3.19.0 随域 1 一起发布
