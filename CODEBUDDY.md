# Argus 测试工程师工作台 — Agent 开发规范（唯一入口 Spec）

> **本文件是任何 agent 参与本项目前必读、且流程层面仅需读的唯一 Spec。** CodeBuddy Code 每次会话自动加载本文件；其他 agent 工具请在动手前先读本文件。
> **强制规章见 `AGENT_RULES.md`（仓库根，命令式硬性条款，任何改动前必读）。** 本文件是规章的依据与细节，规章与本文件冲突时以本文件为准（规章是本文件的提炼）。
> 维护规则：全仓库流程规范**只维护这一个文件**。随每次发版更新 §0 版本快照；架构/历史细节一律走 §8 索引，不在此重复。最后更新：2026-09-21（v3.48.6）。

---

## 0. 当前版本快照

| 项 | 值 |
|---|---|
| 主程序 | FctAggregator **v3.48.2**（**纯单机工具**——v3.31.0 起删除聚合/互联/Web 看板，.NET 8 `net8.0-windows`，单 exe） |
| 分发器 | FctDistributor **v1.9.2**（**2026-09-02 起独立仓库** `e:/FctDistributor`，与本仓库彻底分离：各自 git、各自发版、代码持副本） |
| 数据层 | SQLite 单写者 + WAL 短租读；本机库幂等 Init（test_measurements/fail_items/tdms_features/normal_models/device_samples_local 等），**无 DbMigrator/无聚合库** |
| 自检 | **1123 项断言**（v3.48.6 审计修复批四 +12，实跑 1123；提交前以 selftest 实跑 [OK] 计数为准），全绿是任何提交的硬门槛 |
| 智能化 | 正常态自学习 P1～P4（模型底座 + 偏离评分/回填回放 + 嵌入「分析」页 + 归因偏离证据，`learn_normal_enabled` 默认开；规格 05 `learn_baseline` 仍关、`learn_fail_merge` 群挂已从看板移除）+ 规格 06 分析（**PASS 测量值-时间趋势图 + 规格上下限参考带**（v3.37.0 替换原 |z| 折线）+ 时段归因 + TDMS 默认开；drift/CPK/tester 默认关）+ 无感热升级 + 客户端主页工业大屏 + 飞书卡片 JSON 2.0（**共 10 张**） |
| 算法路线 | 正常态自学习 P1～P4 已交付（`learn_normal_enabled` 默认开）+ 规格 05/06 本机能力保留；规格 01～04 聚合侧实现已随 v3.31.0 删除 |
| 远程 | Gitea `http://192.168.1.16:3000/zenix/Argus.git`，分支 `main`；分发器独立远程 `zenix/FCT-Distributor.git` |
| 近期结构变更 | 2026-09-21：**v3.48.6 审计修复批四（自学习口径 5 处）**（`LearnBackfill` 水位改 `AdvanceWatermark` 单调推进——回填与实时并发不再双计；二次回填 0 条明示「水位已覆盖」；`BuildSlotInterruptBaseline` 日期归一（M11 第三处漏网）；`SelfBaseline.Std` 总体 σ→样本 σ 与 Welford/SQL 对齐；`DeviationScorer.IsNearConstant` 近常量闸门——waveform/device 源不再把噪声放大成满分偏离；断言 **1111→1123**）；2026-09-21：**v3.48.5 审计修复批三（UI 批 10 处）**（MainForm 1s Tick 的 `FctIni.Snapshot` 移后台；UpdatePromptForm/LearningPanel 回填回调/维修导出/日志导出移出 UI 线程；新增 `Theme.CachedFont`/`BoldOf` Font 单一缓存入口修 GDI 泄漏（17 处赋值改道）；9 处自绘 OnPaint/Paint 统一 try/catch 兜底；FailExporter 小计行进项目/值/限值列（原写进 SN/时间列）；待办/备份/学习页刷新失败可见化（列头「加载失败」徽标）；LiveAlertPanel 机台徽标改实色底；FAIL 页 FilterKeys 缓存；断言 **1106→1111**）；2026-09-21：**v3.48.4 审计修复批二（数据安全 8 处）**（瘦表重建 DDL 单事务——崩溃不留 `*_slim` 空表；`Database.CheckpointTruncate` 读 `wal_checkpoint` 返回值——busy≠0 跳过备份不留残缺；备份清理 glob 排除 `.sha256`——名义 7 份真的留 7 份；`StageUpdate` 备份前 checkpoint；`busy_timeout` 5s→30s + 历史扫描失败批次整批入重试队列——VACUUM 窗口不漏采；维修终态参数化收口 `MaintenanceMeta`；`ListTodoView` 区间时间拼装前 `replace` 归一 dash；`UpdateMaintenanceStatus/UpdateMaintenance` 单连接单事务 + `AcknowledgeTodo` 补偿删除；断言 **1090→1106**）；2026-09-21：**v3.48.3 审计修复批一（静默失效 6 条）**（待办区间视图合并键与入库同源 `MergeKeyOf`——spec 合并项不再被区间筛选整体丢弃；`fail_alerted` 离开 FAIL 时复位——FAIL→PASS→FAIL 复发重新告警；`UpdateChecker.RestartCommand` 识别 dotnet 宿主（启动.bat）补 `Argus.dll`——热升级重启不再静默失败；`CompactStorage` 保留期钳制与 `LearnPipeline` 同源 365——不再多删 180~365 天明细；`upgrade_drill.ps1` 版本断言按 csproj 动态化；随包 config 模板改取 `config.example.json` 全量 78 键——现场调参不再被交集合并丢弃；断言 **1078→1090**）；2026-09-21：**v3.48.2 日期双格式收口 + 热升级交集合并 + 死代码剔除**（待办/今日 KPI/当月统计/启动补推/FAIL 导出承认 yyyy-MM-dd；小时柱未知时间不再灌「现在」；`MergeConfigJson` 模板键保留现场值含 auto_update/feishu/learn；重试与实时共用 `_inFlight`；删 Theme 死令牌/`CsvUtil` 写出三件套/G49 别名/`ConfigValidator`；断言 **1055→1078**）；2026-09-16：**v3.48.1 全项目审计修复批**（热升级 `--post-update` 死代码导致升级后 GUI 不启动、待办角标 `_todoBadgeBusy` 卡死、`Config.ReloadFromDisk` 失败静默复位配置、冷归档日期口径误删近期记录、飞书 HTTP 200 业务失败被当成功、`SwapIn` 覆盖现场 data/、`rank` 工具输入区布局错位；断言 **1030→1055**）；2026-09-16：**v3.48.0 撤回盘口动效**（删 `UiMotion`，回到去卡顿；断言 **1065→1030** 经授权下调）；2026-09-16：**v3.44.2 去卡顿**（图表 SetData 签名跳过、总览以外 Tick 不刷大屏、`BufferedTabControl`、`StyleGrid` 双缓冲、自绘 OptimizedDoubleBuffer、间距吃 Theme.Gap；断言 **1016→1030**）；2026-09-16：**v3.47.0 切页横滑+点击**（`SlidePair` / `MotionButton` / 卡片按下缩放；断言 **1057→1065**）；2026-09-16：**v3.46.0 KPI 数位滚轮**（`DigitAtlas` 按位裁切 + `MotionClock` 共享 16ms；断言 **1048→1057**）；2026-09-16：**v3.45.0 盘口动效**（`UiMotion` 280ms ease-out：KPI 跳动+涨跌闪、小时图/TopFail 柱过渡、悬停逼近；断言 **1030→1048**）；2026-09-16：**v3.44.2 去卡顿**（图表 SetData 签名跳过、总览以外 Tick 不刷大屏、`BufferedTabControl`、`StyleGrid` 双缓冲、自绘 OptimizedDoubleBuffer、间距吃 Theme.Gap；断言 **1016→1030**）；2026-09-16：**v3.44.1 发版**（精简审计：FailRanker 树事件接线、`update_dir` 四方同步、死代码清理；口径收敛批不升版本号：CSV/`ByteUtil`/`StatsUtil`/大屏 `Theme.Chart*`，断言 **1001→1016**）；2026-09-14：**v3.41.0～v3.44.0 飞书卡片批（2 → 10 张）**：新增 `Infra/FeishuFailBatcher.cs`（FAIL 告警窗口合并，单条仍走原卡，`FeishuSendOutcome` 三态 + 失败留给「今日补推」）、`Intelligence/CollectHealthMonitor.cs`（采集异常告警 + 恢复通知，首次给 `parse_failure_log` 加读取入口 `Db/Database.CollectHealth.cs`）、`Intelligence/LearnAlertMonitor.cs`（章节群挂 + 正常态偏离）、`Intelligence/OpsAlertMonitor.cs`（存储健康 + 待办超期）；`DbMaintenance` 加每日运行摘要卡与两个注入点；断言 821→**1001**；2026-09-14：**v3.40.5 UI 体验批（审计报告 §4 UI-3/4/7/8/9/10/11）**：新增 `Ui/AppForm.cs`（**全项目窗体基类**：统一 `AutoScaleMode.Dpi` + 96dpi 基准 + 无障碍角色），15 个窗体改继承它；csproj 加 `ApplicationHighDpiMode=PerMonitorV2`（默认 SystemAware 会整窗位图拉伸）；`Theme.SetButtonPrimary` 修 primary 按钮被 `FlatStyle.System` 吞色；`Theme.TextMuted`（#6D6D6D，白底 5.2:1 对比度）+ `Tiny` 7.5→8pt；看板卡片加 Tab/Enter/Shift+F10 与 AccessibleName；截断控件补 ToolTip；日志改复用 StreamWriter；断言 807→**821**；2026-09-14：**v3.40.4 文件名时间锚点改目录日**（`Parsing/DefaultResultParser.cs` 不再用 `DateTime.Now` 做 `ResolveFileNameTime` 的 anchor——回填 >30 天历史 XML 时文件名时间曾被丢弃、回落目录日 `T00:00:00`，与 v3.40.1 的 90/365 天回填口径矛盾；断言 805→**807**）+ **自检夹具去漂移**（`SelfTest.cs` 时间源/采样用例改相对 `DateTime.Now/Today` 生成，见 §5）；2026-09-11：**v3.40.3 趋势图 GDI OverflowException**（FAIL 掉坑像素坐标钳位，OnPaint 吞异常）；2026-09-11：**v3.40.2 趋势图纵轴按 PASS 定标、横轴缩到有数据区间**（FAIL 掉坑不再撑轴；空/稀疏窗口提示库内测量实际日期）；2026-09-11：**v3.40.1 分析趋势与测量保留期对齐**（`EffectiveMeasureRetentionDays` 统一维护/入库/清理；默认 `analyze_measure_retention_days` 90、上限 365；回填前可写 config）；2026-09-11：**v3.40.0 趋势图叠加 FAIL 曲线（PASS 实线 + FAIL 同色虚线、去散点）+ 移除 Seq 代码管理**（整删 `Ui/SeqChangeForm.cs`/`Core/FctSeqMonitor.cs`（含 UiBridge）/`Core/FctSeqSnapshot.cs`/`Db/Database.FctSeq.cs`，`BackupCodePanel` 单栏化，Init 加 `DROP TABLE IF EXISTS fct_seq_change_log`；断言基线 798→**795** 经授权下调）；2026-09-11：**v3.39.0 FAIL 页测项级明细 + 日/周/月/季度导出**（新增 `Db/Database.FailDetail.cs`（FailItemDetail / QueryFailItemDetails，`test_records` **LEFT JOIN** `fail_items`，老记录退回 fail_reason）+ `Ui/FailExporter.cs` + `TimeUtil.NaturalPeriod`/`NaturalRange`；FAIL 表格改「一个失败项一行」6 列、默认按失败项分组）；2026-09-11：v3.38.x 趋势图刷新改**代际模型**（删 `_chartBusy` 悲观锁）+ 桶查询按勾选下推（查询量降约 50 倍）；2026-09-11：**v3.37.0 分析页折线改「测量值 vs 时间」+ 规格上下限参考带**（新增 `Intelligence/MeasurementTrendBuilder.cs`（桶宽自适应/归一化/基准带，纯函数）+ `Ui/MeasurementTrendChart.cs`，删 `Ui/SignalDeviationChart.cs`；分桶下推 SQL `GROUP BY`，桶号两端都在 SQL 内同口径取整，避开 `strftime('%s')` 把本地时间当 UTC 的按天错位坑）+ `tools/diag_yield.ps1`（现场良率对账，双日期口径证据）；2026-09-10：v3.34→v3.36.23 审计修复五批（`docs/Argus-全面调试报告-20260910.md` 31 条 / 🔴7 全清：ALGO-2 入库失败入重试队列堵漏采、ALGO-3 漂移窗口真正零重叠、ALGO-4 σ 不再 NaN、ALGO-6 空白 SN 补 TRIM；PERF-1/2/3 每秒注册表+串口枚举、FCT.ini 全盘重扫、扫描统计节流；UI-1/2/5/6 最小尺寸、清库口径、FAIL 页脏数据、重按钮移出 UI 线程）+ v3.36.0 性能完整优化 + 热更新共享文件夹只读源；2026-09-08：v3.31.0 **纯单机化改造**（删除聚合/互联/Web 看板：`Agg/`、`Mesh/`、`public/`、`AggDatabase*.cs`、`DbMigrator.cs`、`Ui/AggCenterForm`、设备/预测/告警/布局类整目录删除；Engine/DbMaintenance/MainForm/Config/Program/FeishuNotifier 收窄本机链路；`config*.json`/发版脚本/四份文档去聚合残留；selftest 基线 **1148→568**）；2026-09-08：v3.30.1 审计 Critical 修复（C1 config.json untrack+导出密钥守卫 / C3 回滚 admin 门禁+只认 config_backups / C4 热升级旧进程原地换装 SwapIn+失败保留 pending+MarkPrompted 后置，+15 断言）；2026-09-08：v3.30.0 正常态自学习 P1（StreamingStats welford/P2 + normal_models 表 + NormalModelStore + 三学习器接入摄取链路，learn_* 默认关）+ SN 补采（ReadFactoryOnly 取 DUT SN 兜底 + BackfillMissingSn 启动自动/CLI 手动）+ 采集链路加固（Changed 订阅+在途去重+暂时性失败重试+重试队列落盘、历史扫描批量锁重试、mesh 离线周期扫描 IsPeerTimedOut、当月良率 SN 终检口径、TSV 清洗、config GetStringSafe、全局异常兜底）；2026-09-07：v3.29.0 规格 06 期 3（TDMS 特征化 FAIL+PASS 全采 + tdms_features 本机表幂等 Init + TdmsFeatureAnalyzer/TdmsFeatureCollector 实时队列与 backfill 补采 + AnalysisPanel TDMS 区，analyze_tdms_enabled 默认关）；2026-09-07：selftest 修复心跳轮询读撞文件锁 flaky（ReadTextSafe）；2026-09-05：v3.28.0 规格 06 期 2（fail_items 明细落表 value/value_text 双轨 + FailAttributor 多维归因 + FixtureComparator tester 效应量 + AnalysisPanel 归因/效应量/群挂 UI，analyze_* 新五键）；2026-09-05：v3.27.0 规格 06 期 1（PASS 测量值实时采集 analyze_collect_pass + test_measurements/fail_items 本机新表 + DriftAnalyzer/CpkAnalyzer + AnalysisPanel 深度分析页 + backfill CLI，本机库不走 DbMigrator 走幂等 Init）；2026-09-04：v3.26.3 开源准备（FallbackWebhookUrl 置空 + 全仓库注释剥离）；2026-09-04：v3.26.0 移除暗黑模式（Theme 固定浅色，ui_theme 废弃）+ 无感热升级（auto_update 默认开：自动暂存+托盘告知+分离进程重启提交，UpdateChecker.ScheduleRestart）；2026-09-04：v3.25.0 待办合并 spec 原理级（G49TodoRules 章节信号族+电源轨别名归并，todo_spec_merge 默认开）；2026-09-04：v3.24.3 UUT Status Err 全链路排除（纯 UUT F_ 降级 INTERRUPTED 不计 Fail/不推送，XmlParser 口径对齐）；2026-09-04：v3.24.2 飞书 webhook 写死兜底（Config.FallbackWebhookUrl，config 未配时机台端告警回落内置群）；2026-09-04：v3.24.1 飞书卡片抬头图（config feishu_banner_img_key，img 负 margin 贴 header，全告警卡统一）+ 主页大屏今日→当月综合良率（FetchMonthlyStats yyyymm 前缀）；2026-09-04：v3.24.0 规格 05 机台端自学习（G49ProductDictionary/FailReasonMerger/SelfBaseline/LearningEngine/DeviceSampleRecorder + v13 迁移 + learn_* 十键）+ 飞书卡片 2.0（FeishuCardV2）；2026-09-03：v3.23.3 客户端主页工业大屏看板升级；2026-09-03：v3.23.2 FAIL 记录文件名时间高精度识别 + 相同 SN 合并问题与树形分组折叠；2026-09-03：v3.23.1 部署脚本覆盖兜底与防关窗；2026-09-03：v3.23.0 暗黑模式双令牌；2026-09-02：FctDistributor 拆分独立仓库 |

## 1. 全局结构（重组后）

```
e:/Argus-main/
├── AGENT_RULES.md             ← **强制规章（任何改动前必读，命令式条款）**
├── CODEBUDDY.md              ← 本文件（唯一流程 Spec）
├── Argus.sln                 ← 两工程一键构建（FctAggregator + SelfTest；分发器已拆分，勿加回）
├── FctAggregator/            ← 主程序（**纯单机**，源码按域分组）
│   ├── Program.cs            入口（子命令分发：GUI / fetch / tdms / rank / upgrade / backfill / learn / logs）
│   ├── Core/                 采集引擎与数据模型（Engine/Processor/XmlParser/TestRecord/BackfillTool/FctProgramBackup/...）
│   ├── Db/                   Database（本机库，幂等 Init，按功能 partial 拆分）+ DbMaintenance（每日维护器）+ StorageOptimizer（存储健康/优化）
│   ├── Intelligence/         本机分析与自学习（AnalysisEngine/LearningEngine/NormalLearners/DeviationScorer/LearnPipeline/FailAttributor/Drift·Cpk·Tdms Analyzer/...）
│   ├── Ui/                   WinForms 全家（MainForm/AnalysisPanel/LearningPanel/各 Panel/Theme/UiWidgets/...）
│   ├── Infra/                Config/ConfigWatcher/Logger/TimeUtil/CsvUtil/Xlsx/FeishuNotifier/UpdateChecker
│   ├── Parsing/              插件式解析器（parsers.json 可配置）
│   ├── modules/              FailRanker / Fetcher / TdmsViewer / Upgrader（各自 namespace）
│   ├── selftest/             SelfTest.cs（显式分组）+ SelfTest.csproj（目录通配编主源码）
│   ├── tools/                打包/冒烟/诊断脚本（make_package.ps1、inidiag、xlsxdump）
│   └── 更新日志.md           每版本一条目（自检/坑/明确不做 三段式）——先读最新 2 条再动手
├── docs/                     README.md（索引）+ superpowers/（正常态 spec/plan）+ 算法优化/05 + archive/agg-era/（历史，勿作实现依据）
├── scripts/                  make_release.ps1（一键发版，自动读 csproj 版本，只出主程序 2 包：完整 + 更新）
├── Releases/                 历史发版 zip（忽略，禁删禁改）
└── data/                     本地运行数据（全忽略，禁提交）

e:/FctDistributor/            ← 分发器独立仓库（2026-09-02 拆分；勿在本仓库维护其源码）
```

## 1.5 核心文件速查（按域，新 agent 快速定位）

**入口**：`Program.cs` — 子命令分发（GUI / fetch / tdms / rank / upgrade / backfill / learn / logs）。

| 域 | 文件 → 职责（何时动它） |
|---|---|
| **Core/** 采集引擎 | `Engine.cs`(29K) 后台引擎：型号发现/实时监控/历史扫描/重试队列/每日维护调度 · `Processor.cs` 单文件处理流水线（读前 512MB 硬上限、暂时性/永久性失败分类、解析责任链调度；**无独立 Classifier.cs——前缀分类/debug 判定在 `Parsing/DefaultResultParser.cs`**，该类 v3.31.0 删除）· `XmlParser.cs` BATCH XML 解析（深度/轻量/报告三模式）· `StationDetector.cs` 机台号 IP 映射/型号校验 · `TestRecord.cs` 数据模型 · `AppState.cs` 线程安全统计 · `FctIni.cs` FCT.ini 解析+COM 检测 · `BackfillTool.cs` 存量测量值/SN 补采（backfill CLI）· `FctProgramBackup.cs` C:\FTS 压缩备份（Seq 目录变更监控与基线对比已于 v3.40.0 移除） |
| **Db/** 数据层 | `Database.cs`(162K) **本机库**：建表/批量入库/统计/维修/待办 + 本机分析表（test_measurements/fail_items/tdms_features/normal_models，幂等 Init，**无 DbMigrator/无聚合库**）· `Database.{FctBackup,Storage,FailDetail,CollectHealth}.cs` 同 partial 扩展（备份记录、存储盘点、FAIL 测项级明细查询、`parse_failure_log` 采集健康查询；FctSeq 变更表已于 v3.40.0 移除）· `StorageOptimizer.cs` 存储分层健康评分 + 轻量/深度优化（VACUUM）· `DbMaintenance.cs` 每日维护器（备份+清本机采样+Learning/Analysis RunOnce+滚动清理+轻量巡检+**运维告警与每日摘要**；含 `RetryQueueDepthProvider`/`StationIdProvider` 两个 Engine 注入点） |
| **Intelligence/** 智能 | `AnalysisEngine.cs` 多元深分析引擎（漂移/CPK/归因/效应量/TDMS 快照）· `LearningEngine.cs`+`SelfBaseline.cs`+`FailReasonMerger.cs`+`G49ProductDictionary.cs`+`G49TodoRules.cs` 规格 05 自学习/待办归并 · `NormalLearners.cs`/`NormalModelStore.cs`/`StreamingStats.cs`(Welford/P²) 正常态自学习 P1 · `DeviationScorer.cs`/`DeviationSeriesBuilder.cs` 偏离评分与偏离序列（`DeviationSeriesBuilder` UI 已无引用但是探针依赖，勿删）· `LearnPipeline.cs`/`LearnBackfill.cs`/`LearnCli.cs`/`ReplayRunner.cs`/`LearningSnapshot.cs` 学习编排/水位回填/现场回放 · `FailAttributor.cs`/`FixtureComparator.cs` 多维归因/tester 效应量 · `DriftAnalyzer.cs`/`CpkAnalyzer.cs`/`TdmsFeatureAnalyzer.cs`/`TdmsFeatureCollector.cs` 分析器 · `MeasurementTrendBuilder.cs` 趋势分桶/归一化纯函数 · `DeviceSampleRecorder.cs` 本机采样 · **飞书告警评估**：`CollectHealthMonitor.cs`（采集异常/恢复 + 每日摘要载荷）、`LearnAlertMonitor.cs`（章节群挂去重 / 偏离封顶）、`OpsAlertMonitor.cs`（存储健康 / 待办超期，纯函数 `SeverityRank`/`IsOverdue`/`SelectOverdue`/`DayAllows`） |
| **Ui/** WinForms | `MainForm.cs`(28K) 主窗口/TabControl 7 页导航/KPI · `AppForm.cs` **窗体基类**（DPI 基准 + 无障碍角色，新增窗体一律继承它，自检有反射兜底断言）· `Theme.cs` 视觉令牌单一来源 · `UiWidgets.cs` 自绘基础控件 · `DashboardWidgets.cs` 主页大屏（小时趋势/设备在线/TopFail/实时告警）· `UiAsync.cs` UI 异步刷新/防抖工具 · `MaintenanceBoard/Panel/Status/Exporter.cs` 维修看板/面板/状态机/导出 · `FailListPanel.cs`+`FailExporter.cs` FAIL 页（测项级明细：项目/值/限值）+XmlViewerForm+日/周/月/季度导出（**Excel 列宽预设** / CSV；按项目分组、每组小计与空行分隔） · `DeviceStatusPanel.cs`/`DebugPanel.cs` 设备/调试页 · `AnalysisPanel.cs`+`MeasurementTrendChart.cs` 深度分析页与趋势图（规格06；PASS 实线 + FAIL 同色虚线双线，v3.40.0）· `LearningPanel.cs` 正常态自学习区 · `BackupCodePanel.cs`（测试程序备份页，单栏） · `BoardCard.cs`/`CardPreview.cs`/`TodoCard.cs`/`TodoGrouping.cs`/`FailItemPicker.cs`/`ResolverManager.cs` 卡片交互/预览/待办/合并/故障项/维修人 · `SplashForm.cs`/`DesktopNotifier.cs`/`UpdatePromptForm.cs`/`AppIcon.cs` 启动/托盘/更新提示/图标 |
| **Infra/** 横切 | `Config.cs` AppConfig 单例（新键 Load/Save/known-keys 三处同步）· `ConfigWatcher.cs` config.json 热重载 · `Logger.cs` 日志 · `TimeUtil.cs` 时间解析+自然周期（日/周/月/季度） · `CsvUtil.cs` `Esc`（CWE-1236 防护；**CSV 转义唯一入口**，写出由各导出点手写 StringBuilder）· `ByteUtil.cs` 字节格式化唯一入口（`MbGb` 十进制 / `Human` 二进制，**两套进制并存是有意为之，勿合并**）· `StatsUtil.cs` 良率口径唯一入口（`YieldPct` 先乘后除 / `YieldPctDivFirst` 先除后乘，**两者浮点结果不同，勿合并**）· `Xlsx.cs` 手写 OOXML（各处导出共用）+ `TableStyles()` 共享表头/正文样式· `FeishuNotifier.cs` 全部卡片构建与推送出口（`PostCard` 三态结果）+ `FeishuCardV2.cs` 卡片 2.0 原语 + `FeishuFailBatcher.cs` FAIL 告警窗口合并 · `UpdateChecker.cs` 两阶段自更新 · `LeftoverAgg.cs` 聚合时代遗留服务/防火墙探测（只告警不自动删）· `LogsCli.cs` logs 子命令 |
| **Parsing/** 解析 | `IResultParser.cs` 接口+注册表 · `ParserConfig.cs` 配置模型（路径/前缀规则） · `DefaultResultParser.cs` 内置兜底（**承载原 Classifier.cs 的路径/前缀分类+debug 判定**）· `ConfigurableResultParser.cs` parsers.json 驱动 |
| **modules/**（各自 namespace，勿与主程序互改） | `FailRanker/`（XmlScanner/CsvExporter/XlsxExporter/XmlViewerForm）· `Fetcher/`（Scanner/Exporter/Packager/XlsxWriter/FileLocator/Diagnostics）· `TdmsViewer/`（TdmsDoc/WaveformPanel/Exporter/JsonDumper）· `Upgrader/`（UpgradeWizard，`upgrade` 子命令） |
| **selftest/** | `SelfTest.cs` 全部断言（显式分组；v3.48.6 起 **1123** 项基线）· `SelfTest.csproj` 目录通配编主源码（无需为新文件改动） |
| **脚本** | `scripts/make_release.ps1` 一键发版（只出主程序 2 包：完整+更新）· `FctAggregator/tools/make_package.ps1` 打包器 · `tools/smoke_{gui,notify,cli}.ps1` 冒烟 · `tools/snap_gui.ps1` 截图 · `tools/xlsx_check.ps1` xlsx 对比 · `tools/{deploy_update,upgrade_drill,diag_fctini}.ps1` · `tools/inidiag/`+`tools/xlsxdump/` 诊断工具（递归通配编主源码） |

根级随包文件（**禁止移动**，打包脚本按根相对路径引用）：`config.json`、`config.example.json`、`parsers.example.json`、`app_icon.ico`、`启动.bat`、`更新日志.md`、`版本更新说明.md`、`packages.lock.json`。

## 1.6 任务路由表（要改什么 → 去哪里）

| 要改什么 | 位置 |
|---|---|
| 数据库表/查询 | `Db/Database.cs`（本机库幂等 Init/建表/统计/维修/待办，无 DbMigrator 无聚合库） |
| 采集/解析行为 | `Core/Engine.cs`、`Core/Processor.cs`、`Parsing/` |
| 良率统计/口径 | `Core/Engine.cs`（实时/历史入库）+ `Db/Database.cs`（FetchDaily/Monthly/TopFails 统计） |
| 维修/待办流转 | `Ui/Maintenance*.cs`（状态机/面板/导出）+ `Ui/Todo*.cs`（本机待办登记/合并）+ `Core/` |
| 配置项增改 | `Infra/Config.cs`（Load/Save/known-keys 三处同步）+ `config.example.json` |
| 导出格式（xlsx/csv） | `Infra/Xlsx.cs` + `Infra/CsvUtil.cs` |
| 飞书通知文案/卡片 | `Infra/FeishuNotifier.cs`（共 10 张卡：FAIL 告警 / FAIL 批量 / 待办状态变更 / 采集异常 / 采集恢复 / 每日摘要 / 章节群挂 / 正常态偏离 / 存储健康 / 待办超期）+ `Infra/FeishuCardV2.cs` 原语 + `Infra/FeishuFailBatcher.cs` 合并窗口 |
| 飞书告警的触发与节流 | `Intelligence/CollectHealthMonitor.cs`（采集异常+恢复+摘要载荷）· `LearnAlertMonitor.cs`（群挂按天按章节去重 / 偏离按天封顶）· `OpsAlertMonitor.cs`（存储健康 / 待办超期，均按天去重）· `Core/Engine.cs` 5 分钟健康巡检 · `Db/DbMaintenance.cs` 每日维护收口 |
| 本机分析/自学习 | `Intelligence/AnalysisEngine.cs`·`LearningEngine.cs`·`NormalLearners.cs`·`DeviationScorer.cs`·`LearnPipeline.cs`·`DriftAnalyzer.cs` 等（见 §1.5） |
| 测试程序备份 | `Core/FctProgramBackup.cs` + `Ui/BackupCodePanel.cs` + `Db/Database.FctBackup.cs`（Seq 代码管理已于 v3.40.0 整块移除，`fct_seq_change_log` 表随 Init DROP） |
| 库体积/存储优化 | `Db/StorageOptimizer.cs` + `Db/Database.Storage.cs` + `Db/DbMaintenance.cs` |
| 升级向导 | `modules/Upgrader/UpgradeWizard.cs` + `Infra/UpdateChecker.cs` |
| 自检断言 | `selftest/SelfTest.cs`（沿用显式分组注释风格，基线 1123） |
| 打包/发版 | `scripts/make_release.ps1`（版本自动读 csproj）+ `tools/make_package.ps1` |

## 2. 代码概况与关键约束

| 约束 | 说明 |
|---|---|
| 零第三方 UI 依赖 | 图表 canvas 自绘、xlsx 手写 OOXML（Infra/Xlsx.cs）、CSS 无框架；禁止引入 ML/图表/前端库 |
| 依赖白名单 | 仅 Microsoft.Data.Sqlite 8.0.29 + SQLitePCLRaw 2.1.12（CVE 修复线）+ System.IO.Ports + TDMSReader |
| namespace 不分目录 | 全部主源码 `namespace FctAggregator`（文件级），目录只是物理分组，移动/新增文件不影响解析（已知例外：Parsing/ 用 `FctAggregator.Parsing`、modules/ 各自独立 namespace、Infra/Xlsx.cs 共享单写者 `FctShared`——selftest 全限定引用，勿改） |
| 编译通配 | 主 csproj 默认通配自动收录新 .cs；selftest 用目录通配——**新增文件不需要改任何 csproj**（新目录名避开 `selftest*`/`tools`） |
| 巨文件禁拆 | Database.cs / MainForm.cs 保持整体（用户决策），新代码放对应域新文件 |
| 口径单一来源 | 状态机=MaintenanceMeta、合并=TodoGrouping、CSV 防护=CsvUtil、时间=TimeUtil；**禁止在第二处重实现** |
| 兼容性铁律 | 老机台库直开自动补表（Database 构造幂等 Init）、config 缺省可运行——任一破坏即不可提交 |

## 3. Agent 标准工作流（Tier 0 必做，1~3 按改动量选）

**Tier 0 · 开发前（任何改动，无例外）**
1. 读本文件 + `FctAggregator/更新日志.md` 最新 2 条（了解当前坑与"明确不做"边界）。
2. `git status` 确认工作区干净；定位目标域目录（§1），读要改的文件。
3. 明确本次改动的 Tier 级别与验收标准，写入提交计划。

**Tier 1 · 小修/bug fix（补丁级）**
- 改代码 → `dotnet build Argus.sln -c Release`（0 警告 0 错误）→ selftest 全绿（§5）→ 更新日志追加 patch 条目（可短）→ commit。

**Tier 2 · 新功能（minor 级）**
- Tier 1 全部 + **自检断言随行**（每功能 +N 条断言与功能同一提交）→ csproj 版本 minor+1（三处同步：Version/AssemblyVersion/FileVersion）→ 更新日志完整条目（三段式，含坑）。

**Tier 3 · 发版（用户确认后）**
- Tier 2 全部 → 先更新本文件 §0 快照与用户说明 → `scripts/make_release.ps1`（自动出 2 zip + RELEASE.txt，纯净性校验须全过）→ `git push origin main`。

**提速规则（兼顾速度）**：Tier 1 迭代期允许只 build 主工程快速自查，但**提交前必须完整跑一次 sln build + selftest**；禁止以"改动小"为由跳过 selftest 提交。

## 4. 编码纪律（改动规则）

| 场景 | 规则 |
|---|---|
| 新增 .cs | 放对应域目录；一个域一个职责，不确定放哪就先看同职责文件在哪 |
| config 新键 | Config.cs **Load/Save/known-keys 三处同步**，缺省值必须可运行，config.example.json 同步 |
| 数据库变更 | 本机库改表走 **Database.Init 幂等 CREATE TABLE IF NOT EXISTS**（沿 device_samples_local/规格06 惯例），禁止绕过 Init 直接改表 |
| 安全 | CSV 导出必须走 CsvUtil（CWE-1236 防护）；XML 解析禁 DTD |
| 提交 | `feat:/fix:/chore:/docs:/refactor:` 英文单行；一个逻辑一次提交；`git mv` 保历史 |

## 5. 测试与审计

- **selftest 唯一入口**：`FctAggregator/selftest/`，断言显式分组写在 SelfTest.cs；新增分组沿用现有注释风格。
- **运行方式（硬性）**：`cd FctAggregator/selftest/bin/Release/net8.0-windows && ./FctAggregator.SelfTest.exe > selftest_run.log 2>&1; tail -3 selftest_run.log` —— **禁止管道直连**（`| tail` 会使进程挂死，实测 1.5 小时无输出）；必须文件重定向。验收线：尾部 `==== 全部通过 ====`，出现任何 `[FAIL]` 即不通过。
- **断言要求**：新功能必须带断言（正常路径 + 边界 + 权限 403 + 兼容旧行为），数量与功能风险成正比；修 bug 至少加 1 条回归断言。
- **夹具日期铁律**：凡带「窗口 / 新鲜度」语义的夹具**禁止写死日期**——产品侧窗口查询（`GetLocalDeviceSamples` 的 `datetime('now','-N days')`、漂移窗口、`ResolveFileNameTime` 的 anchor）都以「现在」为锚，写死日期会让套件**在真实日期跨过阈值那天突然变红**，极易被误判成产品回归（2026-09-14 实例：`2026-09-05` 采样滑出 7 天窗、`20260814` 文件名时间超出 30 天 anchor）。夹具一律 `DateTime.Now/Today ± N` 生成。
- **审计清单（提交前逐项过）**：
  - [ ] sln build 0 警告 0 错误
  - [ ] selftest 全绿且断言数 ≥ 基线（只增不减）
  - [ ] `git status` 只含预期文件（无 bin/obj/data/临时产物混入）
  - [ ] 兼容性三铁律（§2）未被破坏
  - [ ] 更新日志已写（含踩坑记录，坑是给下一个 agent 的）
  - [ ] 版本号策略正确（功能 minor / 修复 patch / 纯结构不升版本）

## 6. 版本与发布

- 版本号唯一来源：`FctAggregator/FctAggregator.csproj`（GUI 启动页、UpdateChecker 全部读程序集，改一处全局生效）。
- 发版脚本 `scripts/make_release.ps1` 自动读 csproj，产出 `Releases/v{ver}/`：Argus 完整/更新包（纯单机，无 Agg 包）+ RELEASE.txt（SHA256 清单）；FCT-Distributor 已独立仓库，自行发版。
- 随包文件（启动.bat、三份 md、config.json、app_icon.ico）**必须在 FctAggregator 根目录原位**——打包脚本按根相对路径引用，移动即断发布。

## 7. 禁止事项

1. **禁止随意写脚本**：一次性分析/临时验证**不落盘**（用后即删或不创建）；确需沉淀的脚本只允许放 `FctAggregator/tools/`（打包冒烟诊断类）或 `scripts/`（发版级），并在更新日志说明存在理由；临时脚本禁止提交。
2. 禁止提交：bin/obj、data/、logs/、Releases/、selftest 运行日志、截图、临时文件（gitignore 已兜底，勿 force-add）。
3. 禁止改动：`Releases/`、历史发版、更新日志旧条目、`.gitignore` 兼容性条目。
4. 禁止引入新第三方依赖（§2 白名单外一律不进）。
5. 禁止绕过 selftest 直接提交；禁止在自检外新增测试框架。
6. 禁止拆分巨文件（§2）。分发器已拆分独立仓库（e:/FctDistributor），本仓库不再维护其源码；两侧的 Xlsx.cs/StationDetector.cs/AppIcon.cs 为各自持有的副本，改一处必须同步另一处（无自动比对，靠纪律）。
7. Windows 环境命令用正斜杠 `e:/Argus-main`；工具输出 GBK 乱码是显示问题不是错误。

## 8. 深入文档索引（按需读，不要求通读）

| 要做什么 | 读哪个 |
|---|---|
| 文档总索引 | `docs/README.md` |
| 全面调试/审计报告（v3.36.18 基线，含修复进度表） | `docs/Argus-全面调试报告-20260910.md` |
| 工程目录与日常命令 | `FctAggregator/README.md` |
| 版本历史、每版踩坑 | `FctAggregator/更新日志.md`（倒序，最新在前） |
| 正常态自学习设计 | `docs/superpowers/specs/2026-09-07-normal-state-learning-design.md` |
| 规格 05 机台自学习 | `docs/算法优化/05-机台自学习.md` |
| （历史）聚合/Web/Mesh 时代 | `docs/archive/agg-era/`（v3.31.0 起勿作实现依据） |
| 发布产物说明 | `Releases/v{ver}/RELEASE.txt` |
