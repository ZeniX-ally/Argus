# Argus 测试工程师工作台 — Agent 开发规范（唯一入口 Spec）

> **本文件是任何 agent 参与本项目前必读、且流程层面仅需读的唯一 Spec。** CodeBuddy Code 每次会话自动加载本文件；其他 agent 工具请在动手前先读本文件。
> **强制规章见 `AGENT_RULES.md`（仓库根，命令式硬性条款，任何改动前必读）。** 本文件是规章的依据与细节，规章与本文件冲突时以本文件为准（规章是本文件的提炼）。
> 维护规则：全仓库流程规范**只维护这一个文件**。随每次发版更新 §0 版本快照；架构/历史细节一律走 §8 索引，不在此重复。最后更新：2026-09-08（v3.32.0）。

---

## 0. 当前版本快照

| 项 | 值 |
|---|---|
| 主程序 | FctAggregator **v3.32.0**（**纯单机工具**——v3.31.0 起删除聚合/互联/Web 看板，.NET 8 `net8.0-windows`，单 exe） |
| 分发器 | FctDistributor **v1.9.2**（**2026-09-02 起独立仓库** `e:/FctDistributor`，与本仓库彻底分离：各自 git、各自发版、代码持副本） |
| 数据层 | SQLite 单写者 + WAL 短租读；本机库幂等 Init（test_measurements/fail_items/tdms_features/normal_models/device_samples_local 等），**无 DbMigrator/无聚合库** |
| 自检 | **594 项断言**（v3.32.0 正常态 P2～P4 后；提交前以 selftest 实跑 [OK] 计数为准），全绿是任何提交的硬门槛 |
| 智能化 | 正常态自学习 P1～P4（模型底座 + 偏离评分/回填回放 + 自学习 Tab + 归因偏离证据，learn_* 默认关）+ 规格 06 多元深分析（TDMS 默认关）+ 无感热升级 + 客户端主页工业大屏 + 飞书卡片 JSON 2.0 |
| 算法路线 | 正常态自学习 P1～P4 已交付（现场开关仍关）+ 规格 05/06 本机能力保留；规格 01～04 聚合侧实现已随 v3.31.0 删除 |
| 远程 | Gitea `http://192.168.1.16:3000/zenix/Argus.git`，分支 `main`；分发器独立远程 `zenix/FCT-Distributor.git` |
| 近期结构变更 | 2026-09-08：v3.31.0 **纯单机化改造**（删除聚合/互联/Web 看板：`Agg/`、`Mesh/`、`public/`、`AggDatabase*.cs`、`DbMigrator.cs`、`Ui/AggCenterForm`、设备/预测/告警/布局类整目录删除；Engine/DbMaintenance/MainForm/Config/Program/FeishuNotifier 收窄本机链路；`config*.json`/发版脚本/四份文档去聚合残留；selftest 基线 **1148→568**）；2026-09-08：v3.30.1 审计 Critical 修复（C1 config.json untrack+导出密钥守卫 / C3 回滚 admin 门禁+只认 config_backups / C4 热升级旧进程原地换装 SwapIn+失败保留 pending+MarkPrompted 后置，+15 断言）；2026-09-08：v3.30.0 正常态自学习 P1（StreamingStats welford/P2 + normal_models 表 + NormalModelStore + 三学习器接入摄取链路，learn_* 默认关）+ SN 补采（ReadFactoryOnly 取 DUT SN 兜底 + BackfillMissingSn 启动自动/CLI 手动）+ 采集链路加固（Changed 订阅+在途去重+暂时性失败重试+重试队列落盘、历史扫描批量锁重试、mesh 离线周期扫描 IsPeerTimedOut、当月良率 SN 终检口径、TSV 清洗、config GetStringSafe、全局异常兜底）；2026-09-07：v3.29.0 规格 06 期 3（TDMS 特征化 FAIL+PASS 全采 + tdms_features 本机表幂等 Init + TdmsFeatureAnalyzer/TdmsFeatureCollector 实时队列与 backfill 补采 + AnalysisPanel TDMS 区，analyze_tdms_enabled 默认关）；2026-09-07：selftest 修复心跳轮询读撞文件锁 flaky（ReadTextSafe）；2026-09-05：v3.28.0 规格 06 期 2（fail_items 明细落表 value/value_text 双轨 + FailAttributor 多维归因 + FixtureComparator tester 效应量 + AnalysisPanel 归因/效应量/群挂 UI，analyze_* 新五键）；2026-09-05：v3.27.0 规格 06 期 1（PASS 测量值实时采集 analyze_collect_pass + test_measurements/fail_items 本机新表 + DriftAnalyzer/CpkAnalyzer + AnalysisPanel 深度分析页 + backfill CLI，本机库不走 DbMigrator 走幂等 Init）；2026-09-04：v3.26.3 开源准备（FallbackWebhookUrl 置空 + 全仓库注释剥离）；2026-09-04：v3.26.0 移除暗黑模式（Theme 固定浅色，ui_theme 废弃）+ 无感热升级（auto_update 默认开：自动暂存+托盘告知+分离进程重启提交，UpdateChecker.ScheduleRestart）；2026-09-04：v3.25.0 待办合并 spec 原理级（G49TodoRules 章节信号族+电源轨别名归并，todo_spec_merge 默认开）；2026-09-04：v3.24.3 UUT Status Err 全链路排除（纯 UUT F_ 降级 INTERRUPTED 不计 Fail/不推送，XmlParser 口径对齐）；2026-09-04：v3.24.2 飞书 webhook 写死兜底（Config.FallbackWebhookUrl，config 未配时机台端告警回落内置群）；2026-09-04：v3.24.1 飞书卡片抬头图（config feishu_banner_img_key，img 负 margin 贴 header，全告警卡统一）+ 主页大屏今日→当月综合良率（FetchMonthlyStats yyyymm 前缀）；2026-09-04：v3.24.0 规格 05 机台端自学习（G49ProductDictionary/FailReasonMerger/SelfBaseline/LearningEngine/DeviceSampleRecorder + v13 迁移 + learn_* 十键）+ 飞书卡片 2.0（FeishuCardV2）；2026-09-03：v3.23.3 客户端主页工业大屏看板升级；2026-09-03：v3.23.2 FAIL 记录文件名时间高精度识别 + 相同 SN 合并问题与树形分组折叠；2026-09-03：v3.23.1 部署脚本覆盖兜底与防关窗；2026-09-03：v3.23.0 暗黑模式双令牌；2026-09-02：FctDistributor 拆分独立仓库 |

## 1. 全局结构（重组后）

```
e:/Argus-main/
├── AGENT_RULES.md             ← **强制规章（任何改动前必读，命令式条款）**
├── CODEBUDDY.md              ← 本文件（唯一流程 Spec）
├── Argus.sln                 ← 两工程一键构建（FctAggregator + SelfTest；分发器已拆分，勿加回）
├── FctAggregator/            ← 主程序（**纯单机**，源码按域分组）
│   ├── Program.cs            入口（子命令分发：GUI / fetch / tdms / rank / upgrade / backfill）
│   ├── Core/                 采集引擎与数据模型（Engine/Processor/XmlParser/TestRecord/...）
│   ├── Db/                   Database（本机库，幂等 Init）+ DbMaintenance（本机库维护器）
│   ├── Intelligence/         本机分析与自学习（AnalysisEngine/LearningEngine/NormalLearners/FailAttributor/Drift·Cpk·Tdms Analyzer/ConfigValidator/...）
│   ├── Ui/                   WinForms 全家（MainForm/各 Panel/Theme/UiWidgets/...）
│   ├── Infra/                Config/Logger/TimeUtil/CsvUtil/Xlsx/FeishuNotifier/UpdateChecker
│   ├── Parsing/              插件式解析器（parsers.json 可配置）
│   ├── modules/              FailRanker / Fetcher / TdmsViewer（各自 namespace）
│   ├── selftest/             SelfTest.cs（显式分组）+ SelfTest.csproj（目录通配编主源码）
│   ├── tools/                打包/冒烟/诊断脚本（make_package.ps1、inidiag、xlsxdump）
│   └── 更新日志.md           每版本一条目（自检/坑/明确不做 三段式）——先读最新 2 条再动手
├── docs/                     总纲.md（主线）+ 算法优化/（4 规格）+ 项目开发与发布规范.md + 机台端通信规范.md（mesh/聚合协议文档，保留备查，代码已删）
├── scripts/                  make_release.ps1（一键发版，自动读 csproj 版本，只出主程序 2 包：完整 + 更新）
├── Releases/                 历史发版 zip（忽略，禁删禁改）
└── data/                     本地运行数据（全忽略，禁提交）

e:/FctDistributor/            ← 分发器独立仓库（2026-09-02 拆分；勿在本仓库维护其源码）
```

## 1.5 核心文件速查（按域，新 agent 快速定位）

**入口**：`Program.cs` — 子命令分发（GUI / fetch / tdms / rank / upgrade / backfill）。

| 域 | 文件 → 职责（何时动它） |
|---|---|
| **Core/** 采集引擎 | `Engine.cs` 后台引擎：型号发现/实时监控/历史扫描/FAIL 列表/每日维护调度 · `Processor.cs` 单文件处理流水线 · `Classifier.cs` 前缀分类+debug 判定 · `XmlParser.cs` BATCH XML 解析（深度/轻量/报告三模式）· `StationDetector.cs` 机台号 IP 映射/型号校验 · `TestRecord.cs` 数据模型 · `AppState.cs` 线程安全统计 · `FctIni.cs` FCT.ini 解析+COM 检测 |
| **Db/** 数据层 | `Database.cs`(94K) **本机库**：建表/批量入库/统计/维修/待办 + 本机分析表（test_measurements/fail_items/tdms_features/normal_models，幂等 Init，**无 DbMigrator/无聚合库**）· `DbMaintenance.cs` 本机库维护器（每日备份+清本机采样+LearningEngine/AnalysisEngine RunOnce+滚动清理） |
| **Intelligence/** 智能 | `AnalysisEngine.cs` 多元深分析引擎（漂移/CPK/归因/效应量/TDMS 快照）· `LearningEngine.cs`+`SelfBaseline.cs`+`FailReasonMerger.cs`+`G49ProductDictionary.cs` 规格 05 自学习 · `NormalLearners.cs`/`NormalModelStore.cs`/`StreamingStats.cs` 正常态自学习 P1 · `FailAttributor.cs`/`FixtureComparator.cs` 多维归因/tester 效应量 · `DriftAnalyzer.cs`/`CpkAnalyzer.cs`/`TdmsFeatureAnalyzer.cs`/`TdmsFeatureCollector.cs` 分析器 · `ConfigValidator.cs` 配置校验 · `DeviceSampleRecorder.cs` 本机采样 |
| **Ui/** WinForms | `MainForm.cs`(43K) 主窗口/导航/KPI · `Theme.cs` 视觉令牌单一来源 · `UiWidgets.cs` 自绘控件 · `MaintenanceBoard/Panel/Status/Exporter.cs` 维修看板/面板/状态机/导出 · `FailListPanel.cs` FAIL 页+XmlViewerForm · `DeviceStatusPanel.cs`/`DebugPanel.cs` 设备/调试页 · `AnalysisPanel.cs` 深度分析页（规格06）· `BoardCard.cs`/`CardPreview.cs`/`TodoCard.cs`/`TodoGrouping.cs`/`FailItemPicker.cs`/`ResolverManager.cs` 卡片交互/预览/待办/合并/故障项/维修人 · `SplashForm.cs`/`DesktopNotifier.cs`/`UpdatePromptForm.cs`/`AppIcon.cs` 启动/托盘/更新提示/图标 |
| **Infra/** 横切 | `Config.cs` AppConfig 单例（新键 Load/Save/known-keys 三处同步）· `Logger.cs` 日志 · `TimeUtil.cs` 时间解析 · `CsvUtil.cs` CSV 写出+CWE-1236 防护（所有导出必走）· `Xlsx.cs` 手写 OOXML（四处导出共用）· `FeishuNotifier.cs` 飞书卡片 · `UpdateChecker.cs` 两阶段自更新 |
| **Parsing/** 解析 | `IResultParser.cs` 接口+注册表 · `ParserConfig.cs` 配置模型 · `DefaultResultParser.cs` 内置兜底 · `ConfigurableResultParser.cs` parsers.json 驱动 |
| **modules/**（各自 namespace，勿与主程序互改） | `FailRanker/`（XmlScanner/CsvExporter/XlsxExporter/XmlViewerForm）· `Fetcher/`（Scanner/Exporter/Packager/XlsxWriter/FileLocator）· `TdmsViewer/`（TdmsDoc/WaveformPanel/Exporter/JsonDumper） |
| **selftest/** | `SelfTest.cs` 全部断言（显式分组；v3.31.0 起 568 项基线）· `SelfTest.csproj` 目录通配编主源码（无需为新文件改动） |
| **脚本** | `scripts/make_release.ps1` 一键发版（只出主程序 2 包：完整+更新）· `FctAggregator/tools/make_package.ps1` 打包器 · `tools/smoke_{gui,notify,cli}.ps1` 冒烟 · `tools/snap_gui.ps1` 截图 · `tools/xlsx_check.ps1` xlsx 对比 · `tools/{deploy_update,upgrade_drill,diag_fctini}.ps1` · `tools/inidiag/`+`tools/xlsxdump/` 诊断工具（递归通配编主源码） |

根级随包文件（**禁止移动**，打包脚本按根相对路径引用）：`config.json`、`config.example.json`、`parsers.example.json`、`app_icon.ico`、`启动.bat`、`更新日志.md`、`版本更新说明.md`、`TECHNICAL_DOCUMENTATION.md`、`packages.lock.json`。

## 1.6 任务路由表（要改什么 → 去哪里）

| 要改什么 | 位置 |
|---|---|
| 数据库表/查询 | `Db/Database.cs`（本机库幂等 Init/建表/统计/维修/待办，无 DbMigrator 无聚合库） |
| 采集/解析行为 | `Core/Engine.cs`、`Core/Processor.cs`、`Parsing/` |
| 良率统计/口径 | `Core/Engine.cs`（实时/历史入库）+ `Db/Database.cs`（FetchDaily/Monthly/TopFails 统计） |
| 维修/待办流转 | `Ui/Maintenance*.cs`（状态机/面板/导出）+ `Ui/Todo*.cs`（本机待办登记/合并）+ `Core/` |
| 配置项增改 | `Infra/Config.cs`（Load/Save/known-keys 三处同步）+ `config.example.json` |
| 导出格式（xlsx/csv） | `Infra/Xlsx.cs` + `Infra/CsvUtil.cs` |
| 飞书通知文案/卡片 | `Infra/FeishuNotifier.cs` |
| 本机分析/自学习 | `Intelligence/AnalysisEngine.cs`·`LearningEngine.cs`·`NormalLearners.cs`·`DriftAnalyzer.cs` 等（见 §1.5） |
| 自检断言 | `selftest/SelfTest.cs`（沿用显式分组注释风格，基线 568） |
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
- Tier 2 全部 → `scripts/make_release.ps1`（自动出 5 zip + RELEASE.txt，纯净性校验须全过）→ `git push origin main` → 更新本文件 §0 快照。

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
3. 禁止改动：`Releases/`、历史发版、更新日志旧条目、`.gitignore` 兼容性条目、region 禁改清单（§4）。
4. 禁止引入新第三方依赖（§2 白名单外一律不进）。
5. 禁止绕过 selftest 直接提交；禁止在自检外新增测试框架。
6. 禁止拆分巨文件（§2）。分发器已拆分独立仓库（e:/FctDistributor），本仓库不再维护其源码；两侧的 Xlsx.cs/StationDetector.cs/AppIcon.cs 为各自持有的副本，改一处必须同步另一处（无自动比对，靠纪律）。
7. Windows 环境命令用正斜杠 `e:/Argus-main`；工具输出 GBK 乱码是显示问题不是错误。

## 8. 深入文档索引（按需读，不要求通读）

| 要做什么 | 读哪个 |
|---|---|
| 架构/数据库/设计约定 | `FctAggregator/TECHNICAL_DOCUMENTATION.md` |
| 工程目录与日常命令 | `FctAggregator/README.md` |
| 版本历史、每版踩坑 | `FctAggregator/更新日志.md`（倒序，最新在前） |
| 主线规划与算法路线 | `docs/总纲.md`、`docs/算法优化规格总览.md` |
| 开发发版流程细则（人读版） | `docs/项目开发与发布规范.md` |
| （历史）机台↔Web/mesh 契约 | `docs/机台端通信规范.md`（v3.31.0 起功能已删，文档保留备查） |
| 发布产物说明 | `Releases/v{ver}/RELEASE.txt` |
