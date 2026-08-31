# 域 1 采集引擎智能化 Spec — 时间策略 + 自适应采集（离线零模型）

> 版本：v1.0（2026-08-31）｜基于用户确认的三类时间策略；与 B1 设计互补，本域聚焦采集链

## 1. 目标

- A 自动识别新机型/新测试项，无需手动改 parsers.json
- B 文件稳定等待与重试自适应（按大小分级）
- C 解析失败自动诊断与分类
- D 采集去重与增量更精准
- E 时间识别智能化（TsSegRe 误匹配 + 系统时间近邻校验）

## 2. 时间策略（用户拍板三类）

**T1 即时性 = 系统时间直接取（DateTime.Now）：**
- 推送：FeishuNotifier 3 类卡片 ts、AggAlertService 汇总/离线/设备/良率卡片、MeshPusher 心跳 ts
- 事件：TestRecord 产生瞬间、MaintenanceRecord.UpdatedAt、FetcherService.CreatedAt/FinishedAt
- 落库字段：IngestTs、AlertHistory.ts、DeviceInfo.LastSeen

**T2 入库时间 = 文件名时间为准：**
- BatchTimestamp/TestDate 优先文件名 14/17 位（TsSegRe），其次目录 yyyyMMdd
- FileTime（文件系统创建时间）仅作校验，不作主源

**T3 启动解析 = 文件时间戳直接取：**
- Engine.HistoricalScan 阶段，FileInfo.CreationTime 与文件名时间双读，缺失文件名时间时取 CreationTime，避免读 XML 内 PANEL/BATCH 不可信时间

**系统时间近邻校验（新增）：**
- 候选时间与 SystemNow 偏差最小者胜出，正常 <5min，>30 天判 time_suspect，TsSegRe 纯数字 14 位若与系统偏差 >30 天视为 SN 误匹配直接排除
- 历史回扫文件偏差大时回退到 4 源投票，不强制近邻

## 3. 组件

- TimeResolver（TimeUtil.ResolveWithSystemAnchor）：5 源投票 + 系统近邻校验 + time_suspect 标记
- AdaptiveWaiter：文件大小分级 stableChecks（<100KB 2次 / 100KB~1MB 3次 / >1MB 4次），重试 30s 指数退避
- ParseFailureClassifier：Error 码 xml_malformed/encoding/truncated/time_suspect，落 parse_failure_log
- ModelAutoDetector：ParserRegistry 未命中 model 时走 DefaultResultParser 兜底，提取 TEST NAME 前缀聚类提示新增规则

## 4. 数据流

- 历史：EnumerateFiles → FileInfo.CreationTime + 文件名 TsSegRe → TimeResolver → Processor → Database.BatchInsert
- 实时：FileSystemWatcher → AdaptiveWaiter → Processor → BatchInsert → 触发 Diagnose/推送（ts=Now）

## 5. 接口与存储

- 新增表 parse_failure_log(id, path, code, ts)
- API：GET /api/diagnosis 时间字段扩展 time_suspect

## 6. 测试与发布

- 单元：TsSegRe 误匹配排除、系统近邻 5min 阈值、大小分级等待
- 版本：v3.19.0 随 B1 一起发布
