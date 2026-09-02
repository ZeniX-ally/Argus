# Argus 测试工程师工作台 — Agent 开发规范（唯一入口 Spec）

> **本文件是任何 agent 参与本项目前必读、且流程层面仅需读的唯一 Spec。** CodeBuddy Code 每次会话自动加载本文件；其他 agent 工具请在动手前先读本文件。
> 维护规则：全仓库流程规范**只维护这一个文件**。随每次发版更新 §0 版本快照；架构/历史细节一律走 §8 索引，不在此重复。最后更新：2026-09-02（v3.21.0）。

---

## 0. 当前版本快照

| 项 | 值 |
|---|---|
| 主程序 | FctAggregator **v3.21.0**（WinForms + Web 看板，.NET 8 `net8.0-windows`，单 exe） |
| 分发器 | FctDistributor **v1.9.2**（独立工具，与主程序"彻底分家"、代码持副本，勿跨工程共用文件） |
| 数据层 | SQLite 单写者 + WAL 短租读；DbMigrator schema 当前 **v10**（predict_accuracy_log） |
| 互联 | P2P Mesh（无中心聚合端），mesh_port 默认 8081，(machine,seq) 幂等，Gossiper 自适应 10/60s |
| 自检 | **1139 项断言**（v3.21.0 基线，随功能递增），全绿是任何提交的硬门槛 |
| 智能化 | v3.19.0 九域（采集/DB/配置/设备/维修/UI/告警智能）+ v3.20.0 预测准确率自反馈（对账器，仅推荐不自动应用）+ v3.21.0 季节性分解（默认关，残差告警叠加） |
| 算法路线 | 4 规格中 02、03 已落地；04 健康分 → 01 良率归因 依次排队（见 docs/算法优化/） |
| 远程 | Gitea `http://192.168.1.16:3000/zenix/Argus.git`，分支 `main` |
| 近期结构变更 | 2026-09-02：v3.21.0 季节性分解（规格 03，纯加法默认关）；2026-09-01：源码按域分组（§1）、根目录加 Argus.sln、清理过期文档 |

## 1. 全局结构（重组后）

```
e:/4.9/
├── CODEBUDDY.md              ← 本文件（唯一流程 Spec）
├── Argus.sln                 ← 三工程一键构建（FctAggregator + SelfTest + FctDistributor）
├── FctAggregator/            ← 主程序（源码按域分组，2026-09-01 起）
│   ├── Program.cs            入口（子命令分发）
│   ├── Core/                 采集引擎与数据模型（Engine/Processor/XmlParser/TestRecord/...）
│   ├── Db/                   Database + AggDatabase.*.cs(partial) + DbMigrator + DbMaintenance
│   ├── Mesh/                 MeshNode/Pusher/Receiver/Gossiper/QueryService + TodoSync
│   ├── Agg/                  WebAggServer(256K 巨文件)/RoutePipeline/HttpIngest/AggAlertService/...
│   ├── Intelligence/         智能化 12 文件（Predictor/Healer/Scorer/Advisor/Reconciler/...）
│   ├── Ui/                   WinForms 全家（MainForm/各 Panel/Theme/UiWidgets/...）
│   ├── Infra/                Config/Logger/TimeUtil/CsvUtil/Xlsx/FeishuNotifier/UpdateChecker
│   ├── Parsing/              插件式解析器（parsers.json 可配置）
│   ├── modules/              FailRanker / Fetcher / TdmsViewer（各自 namespace）
│   ├── public/               Web 看板静态前端（SPA，零第三方，黑白红视觉）
│   ├── selftest/             SelfTest.cs（显式分组）+ SelfTest.csproj（目录通配编主源码）
│   ├── tools/                打包/冒烟/诊断脚本（make_package*.ps1、inidiag、xlsxdump）
│   └── 更新日志.md           每版本一条目（自检/坑/明确不做 三段式）——先读最新 2 条再动手
├── FctDistributor/           分发器（16 文件平铺，勿重组勿共用主程序源码）
├── docs/                     总纲.md（主线）+ 算法优化/（4 规格）+ 项目开发与发布规范.md + 机台端通信规范.md
├── scripts/                  make_release.ps1（一键发版，自动读 csproj 版本）
├── Releases/                 历史发版 zip（忽略，禁删禁改）
└── data/                     本地运行数据（全忽略，禁提交）
```

## 1.5 核心文件速查（按域，新 agent 快速定位）

**入口**：`Program.cs` — 子命令分发（GUI / agg / agg --web / --service install|start|stop|status）。

| 域 | 文件 → 职责（何时动它） |
|---|---|
| **Core/** 采集引擎 | `Engine.cs` 后台引擎：型号发现/实时监控/历史扫描/FAIL 推送+心跳日统计 · `Processor.cs` 单文件处理流水线 · `Classifier.cs` 前缀分类+debug 判定 · `XmlParser.cs` BATCH XML 解析（深度/轻量/报告三模式）· `StationDetector.cs` 机台号 IP 映射/型号校验 · `TestRecord.cs` 数据模型 · `AppState.cs` 线程安全统计 · `FctIni.cs` FCT.ini 解析+COM 检测 · `FctIniWatcher.cs` FCT.ini 变更哈希检测 · `AgentPusher.cs`/`AggWatcher.cs` 遗留 Obsolete 勿扩展 |
| **Db/** 数据层 | `Database.cs`(94K) 机台本地库：建表/批量入库/统计/维修/待办 · `AggDatabase.cs` 聚合库主体（单写者 WAL）· `AggDatabase.Accuracy/Alert/AlertPredict/Device/DeviceIntelligent/Maintenance/ProcLog/Settings.cs` 聚合库 partial 按功能分文件 · `DbMigrator.cs` schema 迁移（当前 v10，改表必加新版本号）· `DbMaintenance.cs` 每日维护（checkpoint/VACUUM/备份/对账） |
| **Mesh/** P2P | `MeshNode.cs` 节点组装/邻居管理 · `MeshPusher.cs` 事件推送（per-peer 并发，(machine,seq) 幂等）· `MeshReceiver.cs` 接收入库 · `MeshGossiper.cs` 对账补推（自适应 10/60s）· `MeshQueryService.cs` 跨机查询（含 PASS 检索）· `TodoSync.cs` 全局待办池广播/合并 |
| **Agg/** 服务端 | `WebAggServer.cs`(256K) **全部 /api/\* 路由**（新端点进新 region，旧 region 禁改）· `RoutePipeline.cs` 路由管线 · `HttpIngest.cs` HTTP 接收 · `AggDeployer.cs` 一键部署 · `AggAlertService.cs` 告警轮询/飞书卡/alert_history · `HeadlessService.cs` 无头服务组装 · `ServiceManager.cs` Windows Service 管理 · `FetcherService.cs` 异步导出（真实 xlsx/zip）· `DeviceModels.cs`+`DeviceInfoCollector.cs` 设备模型/采集 · `XmlReportHtml.cs` 在线 XML 渲染 |
| **Intelligence/** 智能 | `ConfigValidator.cs`/`ConfigAdvisor.cs` 配置校验/推荐 · `DevicePredictor.cs`/`DeviceInspector.cs` 设备预测/巡检 · `PriorityScorer.cs`/`FlowAdvisor.cs`/`TodoSuggester.cs` 维修优先级/流转/待办 · `LayoutAdvisor.cs`/`HighlightEngine.cs` 布局/高亮 · `AlertHealer.cs`/`AlertPredictor.cs` 告警自愈/预警 · `PredictAccuracyReconciler.cs` 预测对账+阈值推荐 · `YieldDecomposer.cs` 季节性分解（规格 03，默认关） |
| **Ui/** WinForms | `MainForm.cs`(43K) 主窗口/导航/KPI · `AggCenterForm.cs`(54K) 聚合看板 GUI · `Theme.cs` 视觉令牌单一来源 · `UiWidgets.cs` 自绘控件 · `MaintenanceBoard/Panel/Status/Exporter.cs` 维修看板/面板/状态机/导出 · `FailListPanel.cs` FAIL 页+XmlViewerForm · `DeviceStatusPanel.cs`/`DebugPanel.cs` 设备/调试页 · `BoardCard.cs`/`CardPreview.cs`/`TodoCard.cs`/`TodoGrouping.cs`/`FailItemPicker.cs`/`ResolverManager.cs` 卡片交互/预览/待办/合并/故障项/维修人 · `SplashForm.cs`/`DesktopNotifier.cs`/`UpdatePromptForm.cs`/`AppIcon.cs` 启动/托盘/更新提示/图标 |
| **Infra/** 横切 | `Config.cs` AppConfig 单例（新键 Load/Save/known-keys 三处同步）· `Logger.cs` 日志 · `TimeUtil.cs` 时间解析 · `CsvUtil.cs` CSV 写出+CWE-1236 防护（所有导出必走）· `Xlsx.cs` 手写 OOXML（四处导出共用）· `FeishuNotifier.cs` 飞书卡片 · `UpdateChecker.cs` 两阶段自更新 |
| **Parsing/** 解析 | `IResultParser.cs` 接口+注册表 · `ParserConfig.cs` 配置模型 · `DefaultResultParser.cs` 内置兜底 · `ConfigurableResultParser.cs` parsers.json 驱动 |
| **modules/**（各自 namespace，勿与主程序互改） | `FailRanker/`（XmlScanner/CsvExporter/XlsxExporter/XmlViewerForm）· `Fetcher/`（Scanner/Exporter/Packager/XlsxWriter/FileLocator）· `TdmsViewer/`（TdmsDoc/WaveformPanel/Exporter/JsonDumper） |
| **public/** 前端（零依赖 SPA） | `index.html` · `css/theme.css` 视觉令牌 · `js/loader.js` 路由注册 · `js/core.js` App 壳（auth/layout/favorites/search）· `js/page-{overview,fails,yield,seasonality,devices,maintenance,fetch,report,proc,alerts,settings,compare,xml}.js` 十三页面 |
| **selftest/** | `SelfTest.cs` 全部断言（显式分组）· `SelfTest.csproj` 目录通配编主源码（无需为新文件改动） |
| **脚本** | `scripts/make_release.ps1` 一键发版 · `FctAggregator/tools/make_package.ps1`/`make_package_agg.ps1` 打包器 · `tools/smoke_{gui,notify,cli}.ps1` 冒烟 · `tools/snap_gui.ps1` 截图 · `tools/xlsx_check.ps1` xlsx 对比 · `tools/{deploy_update,upgrade_drill,diag_fctini}.ps1` · `tools/inidiag/`+`tools/xlsxdump/` 诊断工具（递归通配编主源码） |
| **FctDistributor/**（16 文件平铺，稳定期） | `Distributor.cs` 核心 · `Planner.cs` 计划 · `NameParser.cs` · `TemplateGenerator.cs` · `SnExporter.cs` · `Xlsx.cs`+`XlsxWriter.cs` · `XmlTimeScanner.cs` · `FileTimeUtil.cs` · `StationDetector.cs` · `MainForm.cs` · `Program.cs` · `RestampScopeDialog.cs` · `AppIcon.cs` · `tools/make_package.ps1` |

根级随包文件（**禁止移动**，打包脚本按根相对路径引用）：`config.json`、`config.example.json`、`parsers.example.json`、`app_icon.ico`、`启动.bat`、`更新日志.md`、`版本更新说明.md`、`聚合服务说明.md`、`TECHNICAL_DOCUMENTATION.md`、`packages.lock.json`。

## 1.6 任务路由表（要改什么 → 去哪里）

| 要改什么 | 位置 |
|---|---|
| Web API 新端点 | `Agg/WebAggServer.cs`（新 region + 鉴权 + audit） |
| 数据库表/查询/迁移 | `Db/DbMigrator.cs`（新版本号）+ `Db/AggDatabase.*.cs` |
| 采集/解析行为 | `Core/Engine.cs`、`Core/Processor.cs`、`Parsing/` |
| 良率统计/口径 | `Core/Engine.cs`（心跳日统计）+ `Agg/WebAggServer.cs`（yield API） |
| 告警规则/阈值/推送 | `Agg/AggAlertService.cs` + `Intelligence/AlertHealer·AlertPredictor.cs` |
| 设备监控/预测/巡检 | `Agg/DeviceInfoCollector.cs` + `Intelligence/DevicePredictor·DeviceInspector.cs` |
| 维修/待办流转 | `Ui/Maintenance*.cs` + `Mesh/TodoSync.cs` + `Intelligence/PriorityScorer·FlowAdvisor.cs` |
| 配置项增改 | `Infra/Config.cs`（三处同步）+ `config.example.json` |
| 前端页面/样式 | `public/js/page-*.js` + `public/css/theme.css`（令牌在 Theme.cs 与 theme.css 同步） |
| 导出格式（xlsx/csv） | `Infra/Xlsx.cs` + `Infra/CsvUtil.cs` |
| 飞书通知文案/卡片 | `Infra/FeishuNotifier.cs` |
| 自检断言 | `selftest/SelfTest.cs`（沿用显式分组注释风格） |
| 打包/发版 | `scripts/make_release.ps1`（版本自动读 csproj）+ `tools/make_package*.ps1` |
| 服务化/CLI 子命令 | `Agg/HeadlessService.cs` + `Agg/ServiceManager.cs` + `Program.cs` |
| 在线 XML 查看 | `Agg/XmlReportHtml.cs` + `Agg/WebAggServer.cs`（/api/xmlview）+ `public/js/page-xml.js` |

## 2. 代码概况与关键约束

| 约束 | 说明 |
|---|---|
| 零第三方 UI 依赖 | 图表 canvas 自绘、xlsx 手写 OOXML（Infra/Xlsx.cs）、CSS 无框架；禁止引入 ML/图表/前端库 |
| 依赖白名单 | 仅 Microsoft.Data.Sqlite 8.0.29 + SQLitePCLRaw 2.1.12（CVE 修复线）+ System.IO.Ports + TDMSReader |
| namespace 不分目录 | 全部主源码 `namespace FctAggregator`（文件级），目录只是物理分组，移动/新增文件不影响解析 |
| 编译通配 | 主 csproj 默认通配自动收录新 .cs；selftest 用目录通配——**新增文件不需要改任何 csproj**（新目录名避开 `selftest*`/`tools`） |
| 巨文件禁拆 | WebAggServer.cs / Database.cs / MainForm.cs 保持整体（用户决策），新代码放对应域新文件 |
| 口径单一来源 | 状态机=MaintenanceMeta、合并=TodoGrouping、CSV 防护=CsvUtil、时间=TimeUtil；**禁止在第二处重实现** |
| 兼容性铁律 | 老库自动迁移（DbMigrator 版本化，失败回滚）、mesh 协议向后兼容、config 缺省可运行——三者任一破坏即不可提交 |

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
| WebAggServer 新路由 | 集中单一新 region（现有 A/B/C/Lite-Fetch/Lite-Settings/Lite-Infra/Lite-Ops **禁改**），鉴权与审计与既有端点同规格 |
| 数据库变更 | 必走 DbMigrator 新版本号（幂等迁移 + 失败整体回滚），禁止绕过迁移直接改表 |
| 权限 | 新端点默认 viewer 可读 / engineer 写 / admin 高危，写 `audit_log` |
| 安全 | CSV 导出必须走 CsvUtil（CWE-1236 防护）；XML 解析禁 DTD；token/白名单机制不动 |
| 提交 | `feat:/fix:/chore:/docs:/refactor:` 英文单行；一个逻辑一次提交；`git mv` 保历史 |
| 前端 | public/ 模块化单文件（page-*.js），黑白红令牌、零依赖、PascalCase 响应字段（System.Text.Json 默认） |

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

- 版本号唯一来源：`FctAggregator/FctAggregator.csproj`（GUI 启动页、UpdateChecker、/api/status 全部读程序集，改一处全局生效）。
- 发版脚本 `scripts/make_release.ps1` 自动读 csproj，产出 `Releases/v{ver}/`：Argus 完整/更新包、Argus-Agg 后端包、FCT-Distributor 完整/更新包 + RELEASE.txt（SHA256 清单）。
- 随包文件（启动.bat、三份 md、config.json、app_icon.ico）**必须在 FctAggregator 根目录原位**——打包脚本按根相对路径引用，移动即断发布。

## 7. 禁止事项

1. **禁止随意写脚本**：一次性分析/临时验证**不落盘**（用后即删或不创建）；确需沉淀的脚本只允许放 `FctAggregator/tools/`（打包冒烟诊断类）或 `scripts/`（发版级），并在更新日志说明存在理由；临时脚本禁止提交。
2. 禁止提交：bin/obj、data/、logs/、Releases/、selftest 运行日志、截图、临时文件（gitignore 已兜底，勿 force-add）。
3. 禁止改动：`Releases/`、历史发版、更新日志旧条目、`.gitignore` 兼容性条目、region 禁改清单（§4）。
4. 禁止引入新第三方依赖（§2 白名单外一律不进）。
5. 禁止绕过 selftest 直接提交；禁止在自检外新增测试框架。
6. 禁止拆分巨文件（§2）、禁止在 FctDistributor 与 FctAggregator 间共享源码文件（副本政策是文档化约定）。
7. Windows 环境命令用正斜杠 `e:/4.9`；工具输出 GBK 乱码是显示问题不是错误。

## 8. 深入文档索引（按需读，不要求通读）

| 要做什么 | 读哪个 |
|---|---|
| 架构/数据库/设计约定 | `FctAggregator/TECHNICAL_DOCUMENTATION.md` |
| 工程目录与日常命令 | `FctAggregator/README.md` |
| 版本历史、每版踩坑 | `FctAggregator/更新日志.md`（倒序，最新在前） |
| 主线规划与算法路线 | `docs/总纲.md`、`docs/算法优化规格总览.md` |
| 开发发版流程细则（人读版） | `docs/项目开发与发布规范.md` |
| 机台↔Web 通信契约 | `docs/机台端通信规范.md` |
| 发布产物说明 | `Releases/v{ver}/RELEASE.txt` |
