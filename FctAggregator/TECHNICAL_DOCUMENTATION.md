# Argus 技术文档（C# 版）

**项目**：FCT 测试结果聚合系统（工控机端）
**当前版本**：v3.8.0（FCT 工具套件：单 exe + 工具内嵌 + 风格统一 + 去重 + 正式图标 + **P2P Mesh 去中心化聚合 + Web 看板**；数据分发独立成 FCT-Distributor）
**技术栈**：C# / .NET 8 / WinForms / SQLite
**文档更新**：2026-08-27
**前身**：Python v1.0.2（PyQt6 + FastAPI + PyInstaller），已于 2026-07-27 删除。

> ⚠ **本文档自 v3.5.0 起已按 P2P Mesh 架构更新**：v3.0.0~v3.4.x 的「中心化聚合（共享目录 / 单一聚合机）」已
> 被「每机一个 Mesh 节点 + 纯 HTTP 互相推送 + 任意节点可起 Web 看板」取代。老的中心化类
> （`AgentPusher` / `AggWatcher` / `HttpIngest` / `AggCenterForm`）仍保留但已 `[Obsolete]`，仅自检回归用。
> 文档与代码以实际源码为准，改动细节见 `更新日志.md`。

> ⚠ **源码备份已丢**：原文档写「保留在 `F:\Programe\G4_9-QA\fct-aggregator-python-v1.0.2-源码备份.tar.gz`」，
> 2026-07-31 仓库改名为 `Argus` 后核对发现：该文件在新旧路径、整个 F: 盘均**不存在**，
> 且 git 历史里也没有（首次提交 `b122059` 就已是 C# 重写版，`*.tar.gz` 还在 `.gitignore` 里）。
> 即 **Python 版源码目前无任何存档**，只剩本文第 10 章的差异描述可考。
> （`legacy/fct-fetcher-python/` 是取数小工具的 Python 版，**不是**主程序。）

> 本文档由 Python 版 `TECHNICAL_DOCUMENTATION.md`（停留在 v1.0.1）迁移重写，
> 全部内容已按 C# 版实际源码核对。用户可读的变更说明见 `更新日志.md` / `版本更新说明.md`。

---

## 目录

1. [项目概述](#1-项目概述)
2. [目录结构](#2-目录结构)
3. [整体架构](#3-整体架构)
4. [模块详细说明](#4-模块详细说明)
5. [核心算法与业务规则](#5-核心算法与业务规则)
6. [数据库设计](#6-数据库设计)
7. [配置说明](#7-配置说明)
8. [界面结构](#8-界面结构)
9. [构建与部署](#9-构建与部署)
10. [与 Python 版的差异](#10-与-python-版的差异)
11. [已知限制与后续规划](#11-已知限制与后续规划)

---

## 1. 项目概述

部署在 FCT 工控机上的单机程序：监控 FCT 测试软件产出的 XML 结果文件，
解析 → 分类 → 入 SQLite → 实时统计良率，FAIL 时推送飞书告警；
附带设备状态查看（读 FCT.ini）、维修记录看板（拖拽改状态，含 Excel/CSV 导出）、
FAIL 记录报告查看、调试工具页；**v3.5.0 起多机台经 P2P Mesh 互相聚合，任意节点可开 Web 看板**。

### 1.1 核心功能

| 功能 | 说明 |
|------|------|
| 实时监控 | 每个 `{category}/{model}` 目录挂 `FileSystemWatcher`，新 XML 落盘即处理 |
| 历史扫描 | 启动后台两阶段扫描（收集文件 → 分批解析入库），带进度条，不限天数 |
| XML 解析 | BATCH 格式；PASS 走轻量路径、F_/O_ 走深度路径 |
| 分类统计 | PASS / FAIL / INTERRUPTED / INVALID，累计 + 当日 + 良率 |
| 飞书告警 | FAIL 记录推交互卡片，含失败步骤名称 / 实测值 / 上下限 |
| 设备状态 | 解析 `FCT.ini`：型号列表、固件版本、COM/USB 设备在线灯、A2L 文件 |
| 维修记录 / 待办 | **看板（5 列拖拽改状态）/ 列表**两视图；增 / 查 / **改（全字段编辑）** / 删 + 导出 xlsx / csv<br>**故障项目从 FAIL 记录去重后挑选**（不显示 SN），可一次建多条；维修人可下拉选历史值<br>**待办（v2.5.0 起整合进看板）**：未确认的不良以 `TodoCard` 直接排在看板「待办」列顶部，双击确认 / 拖到别列 = 确认并置状态 / 右键忽略 |
| 界面外壳 | 左侧导航（待办红色角标）+ 顶栏状态胶囊 + 总览 KPI 卡 + 底部状态栏；Ctrl+1~5 切页 / Ctrl+R 刷新 |
| 卡片预览 | **单击**卡片弹 `CardPreviewForm`：完整故障项名 / 明细（可复制）/ **合并的 fail 项 + 各自次数**；延迟一个 DoubleClickTime 触发，避免与双击冲突 |
| 待办登记 | 只扫近 `todo_scan_days`(默认30) 天的 FAIL，按 `TodoGrouping.KeyOf()` **同类合并成大项**，落 `todo_items` 表**永久保留**；可按时间区间查看，按 fail 次数排优先级（高/中/低） |
| 桌面提示 | FAIL 落库即弹 **Windows 原生通知**（托盘 `NotifyIcon` + BalloonTip，无 UWP 依赖），节流合并，点通知直达待办看板 |
| FAIL 记录 | **表格 / 树形双视图**（工具栏一键切换）：表格 = 4 列对齐（失败项/SN/型号/时间，按失败项次数倒序分组、高频标红）；树形 = 按失败项聚合、子节点 SN；共用搜索框即时过滤失败项/SN/型号；双击行/SN 弹出报告式查看器（完整测试流程，失败项标红） |
| 调试工具 | 推送测试 / 数据库状态 / 机台检测 / 型号发现 / 查询 / 自启开关 / 清库 |
| 多机聚合（v3.5.0+） | **P2P Mesh**：每机一个节点，FAIL/心跳互相推送 + 30s 对账；任意节点 `argus agg --web` 起 Web 看板（机台卡片 / FAIL 明细 / XML 在线报告 / CSV 导出 / 文件浏览 / 设置页）；飞书离线告警 + 定时汇总；详见 §3.1 |
| 运行保障 | 单实例 Mutex、开机自启（启动文件夹快捷方式，手动开关）、两阶段自更新（`UpdateChecker`） |

### 1.2 运行环境

- Windows 10（工控机）
- **.NET 8 Desktop Runtime**（框架依赖发布，机台需一次性安装）
- 发布体积约 1.2 MB（Python 版 129 MB）
- 无需管理员权限；`data/` `logs/` 与程序同级

---

## 2. 目录结构

```
FctAggregator/
├── Program.cs                # 入口：子命令分发 → 单实例 Mutex → 读配置 → 启动 Engine → Application.Run
├── Core/                     # 采集引擎与数据模型：Engine / Processor / Classifier / XmlParser /
│                             #   StationDetector / TestRecord / AppState / FctIni / FctIniWatcher
│                             #   （遗留 AgentPusher / AggWatcher 也在此，已标 Obsolete）
├── Db/                       # SQLite 数据层：Database（本地库）/ AggDatabase.*.cs（聚合库 partial：
│                             #   Accuracy/Alert/AlertPredict/Device/DeviceIntelligent/Maintenance/ProcLog/Settings）
│                             #   / DbMigrator（schema 版本化迁移）/ DbMaintenance（每日维护）
├── Mesh/                     # v3.5.0 P2P 去中心化：MeshNode / MeshPusher / MeshReceiver /
│                             #   MeshGossiper / MeshQueryService / TodoSync（全局待办池）
├── Agg/                      # 聚合服务端：WebAggServer / RoutePipeline / HttpIngest / AggDeployer /
│                             #   AggAlertService / HeadlessService / ServiceManager / FetcherService /
│                             #   DeviceModels / DeviceInfoCollector / XmlReportHtml（/api/xmlview 渲染）
├── Intelligence/             # 智能化域（v3.19/3.20）：ConfigValidator / ConfigAdvisor / DevicePredictor /
│                             #   DeviceInspector / PriorityScorer / FlowAdvisor / TodoSuggester /
│                             #   LayoutAdvisor / HighlightEngine / AlertHealer / AlertPredictor /
│                             #   PredictAccuracyReconciler（预测准确率对账）
├── Ui/                       # WinForms 界面层：MainForm / SplashForm / Theme / UiWidgets / AppIcon /
│                             #   AggCenterForm / 各 Panel 与看板（DeviceStatusPanel / Maintenance* /
│                             #   FailListPanel / DebugPanel）/ BoardCard / CardPreview / TodoCard /
│                             #   TodoGrouping / FailItemPicker / ResolverManager / MaintenanceExporter /
│                             #   DesktopNotifier / UpdatePromptForm
├── Infra/                    # 横切基础设施：Config / Logger / TimeUtil / CsvUtil / Xlsx（手写 OOXML）/
│                             #   FeishuNotifier / UpdateChecker
├── Parsing/                  # v3.5.0 插件式解析器：IResultParser / ParserConfig / ParserRegistry / Default / Configurable
├── FctAggregator.csproj      # net8.0-windows / WinForms / SelfContained=false
├── config.json               # 运行配置
├── public/                   # v3.8.0 Web 看板静态前端（SPA 模块化，随构建复制到输出目录）
├── app_icon.ico              # 多分辨率图标(16~256)，同时作为 EmbeddedResource
├── assets/icon/              # 图标源 PNG（tools/make_icon.py 可重建 ico）
├── modules/                  # v2.9.0 合入、v3.0.0 拆分后的三个工具（各自保留 namespace）
│   ├── FailRanker/           #   原 fct-fail-ranker（FctFailRanker）
│   ├── Fetcher/              #   原 fct-fetcher-cs，含 CLI（FctFetcher）
│   └── TdmsViewer/           #   原 fct-tdm-viewer，含 CLI，依赖 TDMSReader（FctTdmsViewer）
├── selftest/                 # 自检工程（不属主程序，按目录通配编主源码，1000+ 项断言）
├── data/                     # SQLite 数据库（{station_id}.db + mesh_agg.db + agg_xml/）
├── logs/                     # app.log
└── dist/                     # 发布产物（含 启动.bat）
```

> 2026-09-01 起根目录源码按域分组到 Core/Db/Mesh/Agg/Intelligence/Ui/Infra 七个子目录，
> 命名空间不变（全部 `namespace FctAggregator`），移动不影响解析与行为。

### 2.1 依赖包

| 包 | 版本 | 用途 |
|----|------|------|
| `Microsoft.Data.Sqlite` | 8.0.10 | SQLite 访问 |
| `System.IO.Ports` | 8.0.0 | `SerialPort.GetPortNames()` 检测 COM 口 |
| `TDMSReader` | 3.1.0 | TDMS 波形解析（内嵌 TdmsViewer 工具） |

Excel 导出**不依赖**任何第三方库（手写 OOXML）。

---

## 3. 整体架构

```
                        ┌──────────────────┐
                        │   Program.Main   │  单实例 Mutex
                        └────────┬─────────┘
                     ┌───────────┴────────────┐
                     ▼                        ▼
             ┌───────────────┐        ┌────────────────┐
             │    Engine     │        │    MainForm    │ WinForms UI
             │  (后台线程)   │        │  Timer 轮询刷新 │
             └───┬───────┬───┘        └───┬────────────┘
                 │       │                 │ 读
   FileSystemWatcher   历史扫描 Task        ▼
                 │       │           ┌────────────┐
                 └───┬───┘           │  AppState  │ 线程安全统计快照
                     ▼               └────────────┘
              ┌─────────────┐               ▲
              │  Processor  │               │ RefreshStats
              │  XmlParser  │               │
              │  Classifier │               │
              └──────┬──────┘               │
                     ▼                      │
              ┌─────────────┐  ─────────────┘
              │  Database   │  SQLite (data/{station}.db)
              └──────┬──────┘
                     ▼ result==FAIL
              ┌───────────────┐
              │ FeishuNotifier│  → 飞书群机器人
              └───────────────┘
```

**线程模型**：
- UI 线程：WinForms 消息循环，用 `Timer` 定期读 `AppState.Snapshot()` 刷新界面（不阻塞）
- 历史扫描：`Task.Run` 单后台任务，分批（100 条/批）解析入库，每批刷新统计
- 实时事件：每个 watcher 事件 `Task.Run` 独立处理（先等文件稳定）
- 共享状态：`AppState` / `Logger` 内部 `lock` / `ConcurrentQueue` 保证线程安全

**启动流程**：
1. `Mutex("Global\Argus_SingleInstance")` → 已运行则弹框退出
2. `AppConfig.Instance` 读 `config.json`（缺失走默认值并告警）；`Logger.SetLevel`
3. `new Engine(cfg)`：解析机台号 → 建 `data/{station}.db` → 打印启动信息
4. `engine.Start()`：校验 results_root → 型号发现 → 挂 watcher → 起历史扫描任务
5. `Application.Run(new MainForm(engine))`；窗口关闭后 `engine.Stop()` 释放 watcher

> 注意：开机自启**不在启动时自动写入**（`AutoStart.EnsureFirstRun()` 已注释），
> 因为“启动即自我持久化”是 EDR（SentinelOne）重点拦截的行为特征，改为 Debug 页手动开关。

### 3.1 去中心化 P2P Mesh 聚合（v3.5.0 起，取代中心化）

> v3.5.0 起聚合从「单一聚合机 + 共享目录/HTTP 接收」改为**每机一个 Mesh 节点**：
> 没有专用聚合宿主，任何一台机器都能是看板宿主，数据靠节点间互相推送 + 对账补全。

```
Machine A（MeshNode）
  Database.RecordsInserted（本地 FAIL 入库）
     ├─ MeshPusher：内存队列 → POST /api/mesh/fail + /api/mesh/heartbeat 给每个 peer
     │   （config.json 的 peers，X-Agg-Token 头；断网落盘 mesh_queue.json，恢复后补推）
     ├─ MeshGossiper（30s）：GET peer /api/mesh/summary → 对比 max_seq → GET /api/mesh/fetch 拉缺口 → 批量入库
     └─ TodoSync：本地维修状态变更 → POST /api/mesh/event（尽力而为，不排队）

Machine B（MeshNode，同一份 exe，可以是看板宿主）
  WebAggServer（HttpListener，mesh_port 默认 8081，单端口承载看板 + mesh 入站）
     ├─ POST /api/mesh/fail|heartbeat → MeshReceiver → 组提交(50ms/100行) → AggDatabase（data/mesh_agg.db）
     ├─ 心跳 → PeerView 在线状态 → PeerOffline/PeerOnline → AggAlertService → 飞书离线/汇总告警
     ├─ GET / 看板 HTML + /public/* 静态前端 + /api/machines|fails|file|xmlview|export.csv|health|stats
     └─ GET /api/mesh/summary|fetch → 供其它节点的 Gossiper 对账；/api/mesh/xml → 跨机 XML 拉取
```

| 组件 | 职责 |
|---|---|
| `MeshNode.cs` | 编排：组装 Pusher（出站广播）+ Receiver（入站）+ Gossiper（对账）+ TodoSync + DbMaintenance；启动/停止生命周期 |
| `MeshPusher.cs` | 出站 HTTP 推送（fail/heartbeat 排队 + 落盘持久化，watchdog 自动重启，per-peer 链路状态，断线补扫） |
| `MeshReceiver.cs` | 入站 fail/heartbeat：**组提交**微批（50ms 窗口 / 100 行/事务）→ `AggDatabase.InsertBatch`；心跳更新 peer 视图；XML 按需拉取 |
| `MeshGossiper.cs` | 每 30s 拉 peer 汇总（machine→max_seq），与本机副本比对，经 `/api/mesh/fetch` 拉缺口批量补录 |
| `AggDatabase.cs` | 副本库（`data/mesh_agg.db`，单表 `agg_records`，`UNIQUE(machine,seq)` 幂等）；**不启用 ADO 连接池**（防句柄占用），写=长连接 WAL writer，读=短连接 |
| `DbMigrator.cs` | `PRAGMA user_version` 驱动 schema 迁移（当前 v1：agg_records + 5 索引），每版本独立事务，失败回滚 |
| `DbMaintenance.cs` | 每日凌晨 `wal_checkpoint(TRUNCATE)` + 超阈值（512MB）VACUUM |
| `TodoSync.cs` | 跨机台全局待办池：本地变更 → 版本号广播 → 远程 `ApplyRemoteTodoEvent`（last-write-wins + 认领抢占） |
| `AggAlertService.cs` | 机台离线（心跳超时 90s）/恢复飞书告警（10 分钟防抖）+ 每 `agg_summary_minutes` 定时汇总卡 |
| `HeadlessService.cs` | `agg --web` 组装层：副本库 + 本地库 + MeshNode + WebAggServer + AggAlertService 统一生命周期；设置页保存热生效 |
| `RoutePipeline.cs` | 极简路由表（精确 + 通配，404/405，命中计数），替代原 Handle() 大 switch |
| `WebAggServer.cs` | 23 条路由的 HTTP 服务（鉴权 / 并发闸 SemaphoreSlim(64) / 文件白名单 / CSV 公式注入防护 / 内嵌看板） |

**关键语义**：
- 传输纯 HTTP，无 SMB；XML 报告**不入副本库**，只记 `xml_path` + `xml_available`，看板按需从源机 `/api/mesh/xml` 实时拉取（源机离线则 404）。
- 幂等：`(machine, seq)` 唯一约束 + 入站一律回 200，重复推送天然去重。
- 端口：headless（`agg --web`）与机台节点都监听 **`mesh_port`（默认 8081）**；旧 `agg_http_port`（8080）只是遗留中心模式（`HttpIngest`）用，Web 设置页读写的是 `mesh_port`（v3.8.0 已对齐）。
- 机器端接入：`config.json` 填 `peers`（如 `["http://10.0.0.2:8081/"]`）即可互推；`agg --install` 一键部署会自动生成 config + 防火墙放行（仅内网网段）+ 启动文件夹自启。
- Web 看板深色 SPA（`public/`）：总览 / FAIL 明细 / XML 报告三页已完成；良率 / 维修 / 设备 / 设置四页是「建设中」占位（良率 `yld_daily` 是 v3.8.1 起的 P1 工作）。

### 3.2 HTTP API 参考（v3.8.0）

基址：`http://{看板宿主}:{mesh_port}/`（默认 8081）。单端口同时承载看板、mesh 入站与对账。

**鉴权**（配了 `agg_token` 时）：推送/对账带 `X-Agg-Token` 头；浏览器首次用 `?token=` 访问，
服务端下发 HttpOnly `SameSite=Strict` Cookie 后翻页/下载免带；`/api/login` 豁免 token 校验。

**推送入口（POST）**——v3.8.0 收敛后唯一入口为 `/api/mesh/*`，`POST /` 保留兼容遗留 AgentPusher：

| 端点 | 调用方 | 说明 |
|---|---|---|
| `/api/mesh/fail` | MeshPusher | FAIL 事件（body 同旧格式：machine/seq/ts/data…；幂等 `(machine,seq)`） |
| `/api/mesh/heartbeat` | MeshPusher | 心跳 + 当日统计（yld_daily） |
| `/api/mesh/event` | MeshPusher | 全局待办事件（TodoSync 合并） |
| `/` | 遗留 AgentPusher | 按 body 顶层 `type`（fail/heartbeat）分发 |

**节点间对账（GET）**：

| 端点 | 调用方 | 说明 |
|---|---|---|
| `/api/mesh/summary` | MeshGossiper | `{machines:[{machine,max_seq}]}`，对比副本缺口 |
| `/api/mesh/fetch?machine&from&to` | MeshGossiper | 返回 `{events:[...]}` 该机台 `(from,to]` 区间增量 |
| `/api/mesh/peers` | 看板 | 邻居机台视图 |
| `/api/mesh/xml?id=` | 看板 | 跨机 XML 拉取（源机自校验白名单） |

**看板数据（GET）**：

| 端点 | 说明 |
|---|---|
| `/api/machines` | 机台状态列表（在线/离线、FAIL 数、心跳时间） |
| `/api/fails?limit&offset&machine&q` | FAIL 明细分页（limit 默认 200，上限 1000；q 搜 SN/型号/失败项） |
| `/api/fails/count?machine&q` | FAIL 总数（分页用） |
| `/api/export.csv?limit&machine` | FAIL CSV（UTF-8 BOM + 公式注入防护） |
| `/api/file?id=|path=` | XML 报告下载（resultsRoot 白名单；**id= 本地缺失自动回退跨机拉取**，v3.8.0） |
| `/api/list?path=` | 目录浏览（白名单内） |
| `/api/xmlview?id=` | 在线 XML 报告（服务端渲染 HTML） |
| `/api/health` | `{ok,uptime_sec,received}` |
| `/api/stats` | 良率日统计（yld_daily，P1 D1） |
| `/api/todos` | 全局待办池 |

**管理后台**（P1 D3 权限/审计）：`/api/settings`(GET/POST)、`/api/login`(POST)、`/api/status`、
`/api/audit`(admin)、`/api/users`(GET/POST/DELETE，角色 viewer/engineer/admin)。

**前端**：`/` 在 public/ 存在时 302 → `/public/`（模块化 SPA）；`/legacy` = 旧内嵌单页看板；
`/public/*` = SPA 静态资源。

> ⚠ 遗留中心模式另有独立 `HttpIngest` 服务（`agg_http_port` 8080）：`/api/fail`、`/api/heartbeat`、`/`，
> 仅 `[Obsolete]` 的 AgentPusher / AggCenterForm 用，mesh 模式不启。

---

## 4. 模块详细说明

### 4.1 `Program.cs`
入口 + 子命令分发。单实例保护（仅 GUI 分支）→ 配置 → 引擎 → 窗口。退出时 `engine.Stop()`，`GC.KeepAlive` 保住 Mutex。

子命令（`argus <cmd>`）：
| 命令 | 作用 |
|---|---|
| （无参数） | 完整 GUI（采集引擎 + 内嵌工具页） |
| `fetch [--diag --start --end --pack ...]` | 取数工具 CLI（转 `FctFetcher.Program.RunCliEntry`） |
| `tdms [file.tdms]` | TDMS 查看 GUI / CLI |
| `rank` | FAIL 排行 GUI（`FctFailRanker.MainForm`） |
| `agg --install` | 一键部署聚合服务（生成 config + 防火墙 + 自启 + 启动 headless） |
| `agg --web` | headless Mesh 节点常驻（Web 看板 + mesh 入站，`RunAggWebService`） |
| `--post-update` | 更新重启后先提交暂存更新（`UpdateChecker.CommitPendingUpdate`） |
| `--debug` | 主 GUI 显示「调试工具」页（现场默认隐藏） |

### 4.2 `Config.cs` — `AppConfig`
- 单例 `AppConfig.Instance`，从 `AppContext.BaseDirectory/config.json` 读取。
- **`BaseDir` 用 `AppContext.BaseDirectory` 而非 `Environment.ProcessPath`**：
  用 `dotnet Argus.dll` 启动时 ProcessPath 是 `dotnet.exe`，会导致 db/log 落错目录。
- `Save()` 保留未知 JSON 字段，token 为空自动生成随机值。
- 字段：`station_id` / `results_root` / `fct_ini_path` / `webhook_url` /
  `skip_historical_scan` / `log_level` / **`desktop_notify`** / **`notify_min_interval_sec`** / **`todo_scan_days`** /
  **聚合（v3.5.0+）**：`agg_token` / `agg_webhook_url` / `agg_summary_minutes` / `mesh_port`(默认8081) /
  `peers` / `db_maintenance_hour` / `db_vacuum_threshold_mb` / 看板卡自定义 `card_*` / `update_dir`
  （旧 `agg_enabled` / `agg_share_root` / `agg_transport` / `agg_http_url` / `agg_http_port` 仅遗留中心模式用）。

### 4.3 `Engine.cs`
| 方法 | 职责 |
|------|------|
| 构造 | 机台号解析（config 优先 → IP 识别）、建库、**组装 MeshNode**（P2P 节点 + 副本库）、写启动日志 |
| `Start()` | results_root 校验（不存在→status=error 并停止采集）、型号发现、挂 watcher、起历史扫描、起 Mesh 节点 |
| `DiscoverModels()` | 扫 `Online`/`Offline` 下符合 `^E\d{7}$` 的目录名，去重排序 |
| `HistoricalScan()` | 两阶段：①枚举全部 xml（每 200 个更新总数）②分批 100 解析入库 |
| `ProcessRealtime()` | 等文件稳定 → 解析 → 入库 → 刷新统计 → FAIL 推送（含 Mesh 出站） |
| `WaitForStable()` | 文件大小连续 2 次不变（500ms 间隔，10s 超时）视为写完 |
| `RestartPusher()` | 聚合设置变更后停旧建新 MeshNode（读最新 peers/mesh_port） |
| `Stop()` | 取消 token、关闭并释放所有 watcher、停 Mesh 节点 |

**推送闸门**：`result==FAIL` 且 `station_id != UNKNOWN` 且历史扫描已完成才推飞书
（避免首次全量扫描时刷屏、避免无机台号的数据误报）。

### 4.4 `Processor.cs`
- `ParsePath()`：正则 `[\\/](Online|Offline)[\\/](model)[\\/](\d{8})[\\/](file)$` 提取
  类别 / 型号目录 / 目录日期 / 文件名；文件名按 `_` 切分取第 6 段为 SN，SN 前 8 位为型号。
- `ParseAndClassify()`：
  - 非 `P_/F_/O_` 前缀 → 警告并跳过（返回 null）
  - `P_`：`XmlParser.ReadUserOnly` 只读 USER 判 debug → PASS（**不深度解析，性能优化**）
  - `F_/O_`：深度解析 → debug 跳过 → `ClassifyByPrefix` → INVALID 计 parse_error 并跳过
  - `batch_timestamp` 保底：解析不到或长度 <10 时用目录日期补 `yyyy-MM-ddT00:00:00`
  - 机台号：config/传入 → `tester` 字段提取 `FCTx` → `UNKNOWN`

### 4.5 `XmlParser.cs`
三种模式，均用 `XmlReader` 流式读取（`DtdProcessing.Prohibit` + `XmlResolver=null` 防 XXE）：

| 方法 | 用途 | 提取内容 |
|------|------|----------|
| `Parse()` | F_/O_ 深度解析 | BATCH@TIMESTAMP、FACTORY@USER/@TESTER、首个 PANEL@STATUS、首个 DUT@ID、所有 `TEST[STATUS=Failed]` |
| `ReadUserOnly()` | PASS 轻量 | 只取 FACTORY@USER，读到 PANEL 即放弃 |
| `ParseReport()` | 报告查看器 | 头部信息 + **全部** TEST 项（含 Passed），用于展示完整流程 |

失败项收集时**跳过** `Get Unit Information`（不计入不良、不触发 FAIL 判定）。

### 4.6 `Classifier.cs`
- `IsDebug(user)`：`USER` 去空格转小写 == `debug`
- `ClassifyByPrefix(filename, hasFailItems)`：`P_`→PASS，`F_`→FAIL，
  `O_`→有真 fail 项则 FAIL 否则 INTERRUPTED，其它→INVALID

### 4.7 `StationDetector.cs` / `AutoStart`
- IP→机台映射：`172.28.55.11~16 → FCT1~6`，`172.28.55.18 → FCT7`（无 .17）
- `IsValidModel`：`^E\d{7}$`
- `ExtractStationFromTester`：从 `PEU_G49_FCT2-T001` 之类字符串提取 `FCT1~7`
- `AutoStart`：向**启动文件夹**写 `.lnk`（WScript.Shell 晚绑定 COM），
  **不写注册表 Run 键**；以 dotnet 运行时快捷方式指向 `dotnet.exe + dll` 参数

### 4.8 `Database.cs`
- 连接：`Microsoft.Data.Sqlite`，每次操作短连接（`Open()` 即用即关）
- 事件：`RecordsInserted`（FAIL 入库触发 → Engine → MeshPusher 出站）、`MaintenanceStatusChanged`（状态实际变化 → 飞书推送 / TodoSync 广播）
- `GetExistingPaths()`：分批 500 的 `IN` 查询做去重预筛
- `BatchInsert()`：单事务 + `INSERT OR IGNORE`（`xml_path UNIQUE` 兜底去重）
- `FetchGlobalStats()` / `FetchDailyStats()`：按 station 过滤，返回 PASS/FAIL/INTERRUPTED/INVALID/产品数
- **v3.5.0 全局待办池**（partial class）：`todo_sync_state` 表 + `BumpTodoVersion` / `ApplyRemoteTodoEvent`（last-write-wins + 认领抢占）/ `GetTodoSyncStates`
- 维修记录：
  - `CreateMaintenance(m)` —— `created_at` 可手工指定，`updated_at` 跟随同一取值（往期记录不会被顶到看板最前）
  - `ListMaintenance(statusFilter, limit = 500)` —— 取 `updated_at`；`ORDER BY updated_at DESC, id DESC`；limit 可参（列表 500 / 看板每列 120）
  - `CountMaintenanceByStatus()` —— 单条 `GROUP BY status`，供看板列头真实计数（**不受 limit 影响**）
  - `UpdateMaintenance(m)` —— 全字段 UPDATE（含 `created_at`），`updated_at = datetime('now','localtime')`
  - `UpdateMaintenanceStatus(id, status)` —— 只改状态，供看板拖拽使用
  - `GetMaintenance(id)` —— 按 id 取单条（v3.2.0，状态变更推送取快照用）
  - `MaintenanceStatusChanged` 事件 —— `UpdateMaintenance*` 在**状态实际变化**时触发
    `(记录, 旧key, 新key)`；`Engine` 构造期订阅 → 后台 `Task.Run` 推飞书（v3.2.0 待办推送，卡面与 FAIL 告警一致）。同一状态重复设置不触发
  - `FailItemSources(stationId, limit = 2000)` —— FAIL 源行（`FailItemSource`，**无 Sn 字段**），供故障项挑选
  - `DistinctResolvers(limit = 30)` —— 历史维修人（按使用次数倒序、已去重去空）
- 维修人员名单（v2.3.0）：`RosterResolvers()` / `ListResolvers()`（= 名单 ∪ 历史）/
  `AddResolver(name)` / `DeleteResolver(name)` / `RenameResolver(old, new, syncRecords)` / `CountRecordsByResolver(name)`
  —— 后三个均为**成员级**（一条记录可能多人）
  - `DeleteMaintenance(id)` —— 同事务内把 `sqlite_sequence.seq` 回抨到 `MAX(id)`，
    使「删空后新建回到 1 / 删最后一条后接着用」；中间空号不回填
- FAIL 查询：`RecentFails(limit)`（Debug 页）、`AllFails(stationId)`（FAIL 记录页，时间倒序）
- 待办（v2.6.0 重做为**登记表**，消费方 = 看板 `TodoCard`）：
  - `SyncTodoItems(scanDays = 30)` —— 把近 N 天的新 FAIL 并入 `todo_items`（增量 + 大项合并），返回新登记条数
  - `ListTodoView(from?, to?, limit)` —— 未确认待办，按 fail 次数倒序；给了区间则只留区间内出现过的，
    并填 `RangeCount`（区间内次数，现算）
  - `CountPendingTodos()` / `GetTodoItem(id)` / `GetMeta(key)`
  - `AcknowledgeTodo(todoId, resolver, severity, status)` —— 建维修记录并与待办行双向关联
  - `CountFailRecords(failItem, stationId)` —— 删维修记录时提示“会回到待办”
  - ⚠ 已删除：`ListPendingTodos()`（旧的实时聚合视图）；`DismissTodo()` 仍在但 `[Obsolete]`
- 待办（v2.4.0，历史）：
  - `ListPendingTodos(stationId)` —— 实时聚合 `test_records` 中 FAIL 项（按 `fail_reason` 分组），
    排除已有**活跃维修记录**（status 不在 resolved/closed）或已忽略的项
  - `DismissTodo(failItem, stationId, model)` —— ⚠ **v2.5.1 已 `[Obsolete]`**：待办不可忽略，
    写入仍会发生但 `ListPendingTodos` 已不再读，所以**调了也不会让待办消失**
  - `CountFailRecords(failItem, stationId)`（v2.5.1）—— 该故障项还有多少条真实 FAIL，
    删维修记录时用它提醒“删了会回到待办列”
  - `AcknowledgeTodo(failItem, stationId, model, resolver, severity)` —— 确认问题，内部调 `CreateMaintenance` 建一条 `open` 记录
- 其它：`MaxSn()`、`TotalRecords()`

### 4.9 `AppState.cs`
全局统计快照：累计 PASS/FAIL/INTERRUPTED/INVALID/ParseError/产品数/XML 文件数、
今日 PASS/FAIL/INTERRUPTED、扫描进度（phase/total/parsed）、状态（idle/running/error）。
`RefreshStats()` = 数据库统计 + 磁盘 xml 计数；`StatsSnapshot.YieldRate` 计算良率。

### 4.10 `Logger.cs`
双通道：`logs/app.log`（`yyyy-MM-dd HH:mm:ss | LEVEL | msg`，`lock` 串行化）+
GUI `ConcurrentQueue`（`[HH:mm:ss] [LEVEL] msg`，超 5000 行丢头）。级别 DEBUG<INFO<WARNING<ERROR 过滤。

### 4.11 `FeishuNotifier.cs`
静态 `HttpClient`（10s 超时）POST 飞书交互卡片（红色 header「FCT Fail 告警」+ markdown 表格）；
内容含机台 / 型号 / SN / 时间 / 失败步骤明细（名称、实测值、上下限）。
**失败重试 3 次**，退避 1s / 2s / 3s；webhook 为空直接 return。

### 4.12 `FctIni.cs`
- **共享读取模式**打开 ini（`FileShare.ReadWrite`），可读被 FCT 测试软件占用的文件
- config 路径找不到时依次尝试常见候选路径，并回传诊断信息
- 输出：型号列表、固件版本、设备（COM/USB + 在线状态）、A2L 文件
- COM 在线判定：注册表 `SERIALCOMM` 活动口 + `SerialPort.GetPortNames()`

### 4.13 `MaintenanceExporter.cs`
- `ExportCsv`：UTF-8 **BOM**（Excel 中文不乱码），字段转义
- `ExportXlsx`：手写 OOXML（`xl/worksheets/sheet1.xml` + `sharedStrings` + `styles`），
  表头加粗、ID 写为真数值；导出**当前筛选结果**，severity/status 转中文

---

## 5. 核心算法与业务规则

### 5.1 目录结构约定
```
{results_root}\{Online|Offline}\{型号}\{yyyyMMdd}\{前缀_Fts_PEU_G49_机台_SN_时间_时间.xml}
```
- 型号目录必须匹配 `^E\d{7}$`（如 `E3002781`），否则不视为型号目录
- 型号最终取 **SN 前 8 位**（文件名解析），目录名仅作兜底

### 5.2 结果分类决策树
```
文件名前缀
├── P_ ──────────────► USER==debug? ─是─► 跳过
│                            └─否─► PASS（不深度解析）
├── F_ ──────────────► 深度解析 ─► debug? ─是─► 跳过
│                                    └─否─► FAIL
├── O_ ──────────────► 深度解析 ─► 有真 fail 项? ─是─► FAIL
│                                        └─否─► INTERRUPTED
└── 其它 ────────────► 警告 + 跳过（不入库）
```
“真 fail 项” = `TEST[STATUS=Failed]` 且名称不含 `Get Unit Information`。

### 5.3 良率口径
```
良率 = PASS / (PASS + FAIL) × 100%      // INTERRUPTED / INVALID 不计入分母
```
- 产品总数：按 SN 去重
- XML 文件总数：磁盘真实文件数（含 debug），与入库数不同，用于核对差异
- **当日统计用目录日期 `test_date`**（不依赖 XML 内部时间戳格式），
  解决 Python 版“累计有 FAIL、当日为 0”的问题

### 5.4 去重策略
1. 入库前 `GetExistingPaths()` 批量预筛已存在路径
2. `xml_path` 建 `UNIQUE` 约束，`INSERT OR IGNORE` 兜底
3. 路径统一 `Path.GetFullPath()` 规范化后比较

### 5.5 文件稳定检测
大小连续 `2` 次（间隔 500ms）相同且非 0 → 视为写入完成；10s 未稳定则跳过并告警。

### 5.6 机台号识别（三级优先级）
`config.station_id` → 本机 IP 映射表 → XML `TESTER` 字段提取 → `UNKNOWN`（入库但不推送）

---

## 6. 数据库设计

数据库文件：`{程序目录}\data\{station_id}.db`（station_id 为空时用 `fct.db`）

### 6.1 `test_records`

| 字段 | 类型 | 说明 |
|------|------|------|
| `id` | INTEGER PK AI | |
| `station_id` | TEXT NOT NULL | FCT1~7 / UNKNOWN |
| `model` | TEXT | 型号（SN 前 8 位） |
| `category` | TEXT | Online / Offline |
| `test_date` | TEXT NOT NULL | 目录日期 `yyyyMMdd` |
| `sn` | TEXT | 产品 SN |
| `result` | TEXT | PASS / FAIL / INTERRUPTED / INVALID |
| `xml_path` | TEXT **UNIQUE** | 绝对路径，去重键 |
| `fail_reason` | TEXT | 首个失败项名称 |
| `tester` | TEXT | XML FACTORY@TESTER |
| `panel_status` | TEXT | PANEL@STATUS |
| `batch_timestamp` | TEXT | `yyyy-MM-ddTHH:mm:ss`（缺失时用目录日期补） |
| `has_fail_items` | INTEGER | 是否有真 fail 项 |
| `file_size` | INTEGER | 字节 |
| `created_at` | TEXT | `datetime('now','localtime')` |

索引：`idx_date(test_date)`、`idx_sn(sn)`、`idx_result(result)`

### 6.2 `maintenance_records`

| 字段 | 说明 |
|------|------|
| `id` | PK |
| `station_id` | 机台 |
| `equipment_model` / `equipment_sn` | 设备型号 / SN。**v2.3.0 起表单不再录入**（新记录为空），列与导出列保留以兼容历史数据 |
| `fail_item` NOT NULL | 故障项目 |
| `fail_reason` | 故障描述 |
| `severity` | `critical` 严重 / `major` 一般 / `minor` 轻微（默认 major） |
| `status` | `unknown` 未知问题 / `open` 待办 / `in_progress` 持续跟踪 / `resolved` 已完成（默认 `open`）<br>legacy：`closed` 已关闭 —— v2.2.0 起不再产生，Init() 会迁移为 `resolved`；`investigating` 正在排查 —— v3.3.0 起不再产生，归并显示为「待办」 |
| `resolver` / `resolution` / `notes` | 维修人 / 措施 / 备注 |
| `created_at` / `updated_at` | 时间戳（创建时间可在表单手动指定） |

索引：`idx_maint_status(status)`

### 6.3 `resolvers`（v2.3.0 新增）

| 字段 | 说明 |
|------|------|
| `id` | PK |
| `name` | 维修人姓名，**`UNIQUE COLLATE NOCASE`**（大小写不敏感唯一，同一人不会出两条） |
| `created_at` | 录入时间 |

- 建表同样是 `CREATE TABLE IF NOT EXISTS`，旧库首次启动自动添上，**无需迁移、无风险**。
#### 多人存储（无 DDL 变更）

`maintenance_records.resolver` 仍是一列 TEXT，多人存成 `张三、李四`（分隔符 `、`）。
读的时候必须用 `ResolverUtil` 按**成员**拆开：

| 方法 | 用途 |
|------|------|
| `Split(field)` | 拆成人名列表（兼容 `、, ， / ／ ; ； \|`，去空去重保序） |
| `Join(names)` / `Normalize(field)` | 拼回 / 规范化为顶号写法 |
| `Contains(field, name)` | 成员级包含判定——**不能用 LIKE**，否则「张三」会误中「张三丰」 |
| `Replace(field, old, new)` | 只换字段里的那个人，其余人与顺序不变 |

⚠ 改名同步**不能**写 `UPDATE ... SET resolver=@new WHERE resolver=@old`：
那会把「张三、李四」整行抹成「张三三」、把同事弄丢。现实现是逐行读出→成员级替换→写回。

- 名单与「历史记录里用过的名字」是两回事：删名单**不会**动 `maintenance_records.resolver`；
  只有改名时勾上「同步历史记录」才会 `UPDATE maintenance_records`。

> 表结构自 v2.0.0 起未变（`status` 是 TEXT 且无 CHECK 约束，加状态值不需 DDL），
> v2.0.0 / v2.1.0 → v2.2.0 可直接覆盖程序、原地复用 db。
>
> **v2.2.0 数据迁移**（`Database.MigrateClosedStatus()`，幂等）：
> 1. `SELECT COUNT(*) WHERE status='closed'`，为 0 直接返回；
> 2. 先备份 `{db}.bak-yyyyMMdd`（先 `SqliteConnection.ClearAllPools()` 释放句柄再 
>    `File.Copy`；同日已有备份则视为成功）；**备份失败就跳过迁移**并记 WARNING，
>    此时 UI 仍能靠 `MaintenanceMeta` 的 legacy 映射把 `closed` 显示为「已完成」；
> 3. `UPDATE ... SET status='resolved' WHERE status='closed'` —— **不动 `updated_at`**，
>    保留记录真实的最后人工更新时间。

### 6.4 `dismissed_todos`（v2.4.0 新增，**v2.5.1 已停用**）

> ⚠ v2.5.1 起待办来自真实不良，**不得忽略/删除**，此表**不再被读取**（也不再有 UI 写入）。
> 建表语句保留仅为：① 旧库兼容；② 万一回退到 v2.4/v2.5 时不报错。用户已有数据**不删**。

| 字段 | 说明 |
|------|------|
| `id` | PK |
| `fail_item` | 被忽略的故障项名（= `test_records.fail_reason`） |
| `station_id` | 机台号（同一故障项在不同机台可分别忽略） |
| `model` | 型号（仅作记录，不参与匹配） |
| `dismissed_at` | 忽略时间 |

索引：`idx_dismissed_item(fail_item)`

- 表很小，**只记「哪些故障项不用提醒」**，不存任何业务数据。
- 建表同样是 `CREATE TABLE IF NOT EXISTS`，旧库首次启动自动添上，**无需迁移、无风险**。
- 忽略不影响已存在的维修记录；若需取消忽略，目前需手动删表里对应行（后续可加 UI）。

---

## 7. 配置说明

`config.json`（程序目录，UTF-8）：

| 键 | 默认 | 说明 |
|----|------|------|
| `station_id` | `""` | 留空则按 IP 自动识别 |
| `results_root` | `D:\Results` | 测试结果根目录 + Web 看板文件白名单根 |
| `fct_ini_path` | `C:\FTS\Apps\PEU\Cfg\FCT.ini` | 设备状态页数据源 |
| `webhook_url` | `""` | 飞书群机器人；空则不推送 |
| `skip_historical_scan` | `false` | true 则只做实时监控 |
| `log_level` | `INFO` | DEBUG / INFO / WARNING / ERROR |
| `desktop_notify` | `true` | Windows 原生桌面提示总开关（**只有显式写 `false` 才关**） |
| `notify_min_interval_sec` | `15` | 两次桌面提示的最小间隔秒数，期间多条合并 |
| `todo_scan_days` | `30` | 待办扫描窗口（天）。只影响**新并入**；已登记的待办永久保留 |
| `mesh_port` | `8081` | **P2P 节点 / headless Web 看板监听端口（v3.5.0+ 实际端口，v3.8.0 对齐）** |
| `peers` | `[]` | 其它 Mesh 节点地址列表（如 `["http://10.0.0.2:8081/"]`），空=单节点 |
| `agg_token` | 自动生成 | 访问/推送令牌；浏览器 `?token=` 首登后 HttpOnly Cookie 接管 |
| `agg_webhook_url` | `""` | 聚合端飞书告警（离线/恢复 + 定时汇总）；只允许 https |
| `agg_summary_minutes` | `60` | 定时汇总推送间隔（分钟） |
| `db_maintenance_hour` | `3` | 每日 WAL checkpoint/VACUUM 执行小时 |
| `db_vacuum_threshold_mb` | `512` | 副本库超过该体积才 VACUUM |
| `card_*` | — | 看板机台卡自定义（密度/字段/排序） |
| `update_dir` | `data/updates` | 自更新扫描目录（`Argus-v{ver}-update.zip`） |

**v3.5.0 起仅遗留中心模式用**的键：`agg_enabled` / `agg_share_root` / `agg_transport` / `agg_http_url` / `agg_http_port`（只被 `[Obsolete]` 的 AgentPusher/HttpIngest/AggCenterForm 读取；**实际监听端口一律是 `mesh_port`**）。

历史遗留但**当前程序不读**的键：`auto_start`、`fts.*`、`api_key`、`cleanup_admin_key`、
`api_host`、`api_port`（HTTP API 已下线）。

---

## 8. 界面结构

主窗口 `1360×920`（默认，最小 `1240×860`；v2.9.1 起为容纳内嵌工具），顶部进度条（仅扫描时显示）+ 中部页面区 + 底部按钮栏。

| 页 | 内容 |
|----|------|
| **主页** | 上：今日一排 5 大卡（产品数/PASS/FAIL/良率/中断，**当日为主**）；下：累计数据 3×2 大数字格（产品总数/PASS/FAIL/良率/中断/待办，**累计为辅**）；无日志滚码区 |
| **设备状态** | FCT.ini 解析结果：型号、固件版本、COM/USB 设备（在线小灯）、A2L 文件；一屏无滚动、刷新不闪烁 |
| **维修记录** | 【看板】（默认）4 列：未知问题 / 待办（含未确认不良卡）/ 持续跟踪 / 已完成，列头带真实计数，**拖卡片即改状态**（拖到任意列都弹全字段编辑框，可记录详细原因）；双击卡片编辑<br>【列表】表格 + 状态筛选 + 双击编辑 + 右键（编辑 / 标记为 4 个状态 / 删除），含「最后更新」列<br>【从FAIL选故障项】去重后的测试项多选 → 一次建 N 条（可合并为 1 条）<br>导出 xlsx / csv（看板导已加载全部，列表导当前筛选） |
| **FAIL记录** | 本机全部 FAIL（SN / 首个失败项 / 型号 / 时间），双击 → `XmlViewerForm` 报告（完整测试流程，失败项标红） |
| **调试** | 推送测试、数据库状态、机台检测、型号发现、设备状态、最近 10 条 FAIL、今日统计、最大 SN、查看配置、开机自启开关、清空数据库 |

底部另有「清空日志」「退出」。窗口标题显示版本号，便于现场确认机台上的版本。

### 6.5 `todo_items` + `app_meta`（v2.6.0 新增）

待办从「SQL 实时聚合」改为**登记表**，这是「永久保留 + 累计次数」的落点。

| 字段 | 说明 |
|------|------|
| `group_key` | `TodoGrouping.KeyOf(fail_reason)` 归一化键，**同 key = 同一张待办卡**（大项合并） |
| `station_id` | 机台号；`UNIQUE(group_key, station_id)` —— 不同机台各算一条待办 |
| `title` | 展示名 = 组内最短的原始项名（去步骤号），稳定不跳 |
| `variants` / `variant_count` | 被合并的原始测试项名（换行分隔，上限 40 条防膨胀） |
| `fail_count` | **累计**不良次数（靠水位线增量累加，重复同步不会翻倍） |
| `first_seen` / `last_seen` | 统一 `yyyy-MM-dd HH:mm:ss`（见下方“坑”） |
| `state` | `pending` 未确认 / `ack` 已确认(有活跃记录) / `resolved` 已处理完 |
| `maintenance_id` / `resolved_at` | 与维修记录的关联、完成时间（复发判定用） |

`app_meta(k,v)` 目前只存一个 `todo_sync_last_id` —— 已并入待办的 `test_records` 最大 id（增量水位线）。

⚠ **坑**：`last_seen` 与 `resolved_at` 必须同格式才能比较。`batch_timestamp` 形如
`2026-07-30T10:00:00`，维修记录是 `2026-07-30 10:00:00`，而 `'T'(84) > ' '(32)`，
不归一化会把「刚完成的记录」误判成复发、立刻弹回待办。统一走 `NormalizeTs()`。

⚠ **坑**：不能简单剔掉开头数字。`5V_Rail` 的 `5` 是电压值，剔了变成 `V_Rail`，
还会把 `5V_Rail` 与 `12V_Rail` 错并。只剔三种确定是序号的形式：
`12) ` / `Step 3 ` / 多段号 `6.1.1.1 `；单个数字+空格宁可不剔。

### 6.6 `agg_records` 副本库 + `todo_sync_state`（v3.5.0）

副本库 `data/mesh_agg.db`（`AggDatabase`，schema 由 `DbMigrator` 按 `PRAGMA user_version` 管理，当前 v1）：

| 字段 | 说明 |
|------|------|
| `id` / `machine` / `seq` | PK；**`UNIQUE(machine, seq)`** —— mesh 推送幂等键 |
| `type` | fail / heartbeat |
| `ts` / `ingest_ts` | 事件时间 / 入库时间 |
| `station_id` / `model` / `category` / `test_date` / `sn` / `result` | 与 test_records 同口径 |
| `fail_reason` / `tester` / `panel_status` / `batch_timestamp` / `has_fail_items` / `file_size` | 与 test_records 同口径 |
| `xml_path` / `xml_available` | XML 路径标记；**内容不落库**，按需从源机 HTTP 拉取 |

`todo_items` 侧新增 `todo_sync_state`（跨机台全局待办池）：每台机器一条同步状态（版本号），
`TodoSync` 广播 `TodoEvent` 后由 `Database.ApplyRemoteTodoEvent` 按 last-write-wins 合并。

### 8.1 维修记录看板实现要点（v2.2.0）

```
MaintenanceBoard : Panel            外层 AutoScroll(横向) + TableLayoutPanel 5 等宽列
├─ StatusColumn : Panel            列头(中文 + 真实计数徐章) + 卡片区
│    └─ FlowLayoutPanel            TopDown / WrapContents=false / AutoScroll / DoubleBuffered
│         └─ MaintenanceCard       全自绘，无子控件
└─ QuickResolveForm : Form         拖到「已完成」时的补填框
```

| 要点 | 原因 |
|------|------|
| 卡片用 `OnPaint` 自绘，**不放任何子控件** | ① 子 Label 会吞掉 `MouseDown`/`MouseMove`，拖拽拿不到事件（WinForms 经典坑）；② 120 张卡 × 5 Label = 600+ 窗口句柄，自绘只要 120 个 |
| 起拖要过 `SystemInformation.DragSize` 阈值 | 否则**单击就起拖、双击编辑失灵** |
| 每张卡也 `AllowDrop = true` 并将 `DragEnter/DragOver/DragDrop` 转发给所属列 | WinForms 里子控件 `AllowDrop=false` 时拖拽事件**不会冒泡**到父容器，光标压在卡片上就放不下去 |
| 同列拖回 = no-op | 不打库、不刷 `updated_at` |
| 拖到 `resolved` 先弹 `QuickResolveForm` | 【确定】走 `UpdateMaintenance`（补填维修人/措施）；【跳过】走 `UpdateMaintenanceStatus`；【取消】整个变更撒销 |
| 落下后只重建**受影响的两列** | 避开整页重载的闪烁与滚动位置丢失 |
| 列头计数走 `CountMaintenanceByStatus()` | 每列只加载 120 张卡，但徐章要显示**库里真实总数** |
| 状态/严重度中文与颜色全部读 `MaintenanceMeta` | 单一来源；之前字典在 Panel 与 Exporter 各一份，加状态漏改一处就会导出英文 key |
| 卡片右键：`MouseDown(Right)` → `ContextRequested` 逐级冒到 Panel | 自绘 Panel 不会自己弹菜单；菜单由 `MaintenancePanel.BuildRecordMenu()` 统一提供，**与列表视图共用一份** |
| 右键按下时清 `_pressed` | 否则右键按住拖动会误入拖拽分支 |
| 未知/legacy 状态走 `MaintenanceMeta.Normalize()` | 归并到 5 列之一（`closed`→已完成，野值→待办），**不会丢卡片** |

### 8.2 故障项挑选（v2.3.0，`FailItemPicker.cs`）

| 设计 | 说明 |
|------|------|
| 去重口径 | 按**测试项名**（`OrdinalIgnoreCase` + 首尾空白归一），空值不入列 |
| 不显示 SN | 从数据层就不带：`FailItemSource` **根本没有 Sn 字段**（自检用反射盯住） |
| 两级取数 | 默认读 `fail_reason`（只有首项，秒出）；【深扫 XML】重解析 `xml_path` 拿全部失败项 |
| 深扫不卡界面 | 后台 `Task.Run` + `CancellationToken`，进度**每 300ms** 报一次（吸取 v1.8.1 教训：每条一次 `BeginInvoke` 会把消息队列打爆） |
| XML 丢了 | 计数并提示，该记录回退到库里的首项，不中断 |
| 多选后建单 | `MaintenanceForm.BatchResults()`：默认每项一条（`Id=0` 交 DB 自增）；勾「合并为一条」则用 `" / "` 拼接 |
| 维修人候选 | `ComboBox(DropDown)` + `AutoCompleteMode.SuggestAppend`，候选 = `ListResolvers()`（名单 ∪ 历史） |
| 【选择】按钮 | 开 `ResolverPickerForm`（`CheckedListBox` 多选 + 搜索 + 全选/清空 + 底部「添加并勾选」），确定后回填 `张三、李四` |
| 【+ 人员】按钮 | 开 `ResolverManagerForm`；关闭后若 `Changed` 则重拉候选并尽量保住当前输入 |
| 保存时自动登记 | 先 `ResolverUtil.Normalize()` 规范化，再**逐人** `AddResolver()` 进名单（失败只记 WARNING，不阔主流程） |

### 8.3 待办（v2.4.0 起；v2.5.0 整合进看板）

设计目标：**每次检测到 FAIL 都需工程师确认问题，然后解决问题**，且不用为此多切一个页面。

| 设计 | 说明 |
|------|------|
| 数据源（v2.6.0） | `todo_items` **登记表** + 现算的区间统计。v2.4/2.5 的纯实时聚合视图已废：那样无法“永久保留”，也无法累计次数 |
| 扫描窗口（v2.6.0） | 只并入近 `todo_scan_days`（默认 30）天的 FAIL；靠 `app_meta.todo_sync_last_id` 水位线增量，反复同步不重算 |
| 大项合并（v2.6.0） | `TodoGrouping.KeyOf()`：丢步骤号 / 通道号 / 位号 / 括号限定词，**保留数字+单位**（5V≠12V）。展示名取组内最短项，原始项存 `variants` |
| 永久保留（v2.6.0） | 登记行只改 `state`、**从不删除**；不良滑出扫描窗口后待办仍在 |
| 时间区间（v2.6.0） | `ListTodoView(from,to)`：区间统计从 `test_records` 现算再用同一套 `KeyOf` 归并；区间只影响显示，不删待办 |
| 优先级（v2.6.0） | 按 fail 次数倒序（有区间用区间内次数）；高 ≥20 / 中 ≥5 / 低，阈值在 `TodoGrouping` |
| 去重口径 | `GROUP BY fail_reason, station_id` —— 同一故障项在不同机台是两条待办 |
| 何时不显示（已确认） | `NOT EXISTS` 子查询：存在**活跃维修记录**（`status NOT IN ('resolved','closed')`）即视为有人在处理 |
| 不可忽略 / 不可删除（**v2.5.1**） | 待办 = 真实 FAIL 数据的**投影**，不是可划掉的事项。UI 无忽略入口（右键菜单只有确认 + 一条禁用说明）；`ListPendingTodos` **不再读 `dismissed_todos`**（旧库里被忽略过的项会自动重现）；看板上已无任何 `Dismiss*` 方法（自检断言防回流） |
| 删维修记录的后果（**v2.5.1**） | 删的只是“处理单”：只要 FAIL 还在库里，卡片**自动回到待办列**。删除确认框用 `CountFailRecords()` 把这个后果写清楚 |
| **复发自动重现** | 维修记录被拖到「已完成」后不再算活跃，同故障项再 FAIL 就**自动回到待办**（无需人工重开） |
| 呈现位置（v2.5.0） | 看板「待办」列（`open`）**列顶**，用 `TodoCard`（暖色底 + 虚线框 + 「未确认」红标）与记录卡区分；列头红徽章 `新 N` = 未确认数 |
| 确认动作 | `TodoConfirmForm`（轻量：维修人 + 严重度）→ `AcknowledgeTodo()` → 建一条 `status='open'` 记录 → 看板「待办」列 |
| 拖出待办列 | = 确认 + 弹全字段编辑框（预置目标状态），保存后 `AcknowledgeTodo(todoId, rec)` 一步到位（例如直接拖到「持续跟踪」） |
| 维修人候选 | 同样读 `ListResolvers()`（名单 ∪ 历史），手敲的新名字逐人 `AddResolver()`，与主表单一致 |
| 机台过滤 | 不再过滤 —— 库文件本身已按机台拆分（`data\{station}.db`），再按机台过滤是重复条件 |
| 自动出现 | `MainForm.Tick()` 监视 `AppState.Fail` 变化，页面可见时自动 `Refresh2()`，新不良无需手动刷新 |
| 与【从FAIL选故障项】的分工 | 待办 = **被动推送**（自动列出没人管的）；那个按钮 = **主动挑选**（含已处理的、可深扫 XML、可批量合并），两者不冲突 |

⚙ 预览"一弹出就全选"的坑（v2.7.1）：WinForms `TextBox` **因窗口激活/Tab 拿到焦点会自动全选**
（鼠标点进去的焦点不会）。预览里那个只读明细框是唯一 tab-stop 控件，`Show()+Activate()` 后
焦点落在它上面 → 整片蓝底，看着像"单击顺手选中了文字"。修法：明细框与清单 ListView 都
`TabStop=false`，并在 `OnShown` / `Activated` / `GotFocus` 里统一 `ClearAutoSelection()`
（`SelectionLength=0` + `ActiveControl=null`）。手动划选复制不受影响。

⚙ 单击预览的坑（v2.7.0）：单击**必须延迟一个 `SystemInformation.DoubleClickTime`** 再弹，
否则双击（确认/编辑）会先闪一下预览。而且 WinForms 消息顺序是
`Down→Up→Click→Down→DoubleClick→Up`，双击后还有一个 `MouseUp`，
不用 `_suppressClick` 拦掉，双击完 500ms 又会弹出预览。右键与起拖同样要排除在“单击”之外。

⚙ 合并清单显示的坑（v2.7.0）：维修记录卡**不能为了显示合并清单去查库** —— 一列上百张卡
每张一次 SQL 会明显卡顿。清单在确认待办时就写进了 `notes`（`TodoGrouping.SourceItemsTag`），
卡片用 `ParseSourceItems()` 从已加载的记录里回读；只有**预览**才会用
`GetTodoByMaintenance()` 拿权威清单 + `CountFailByItems()` 补每项次数。

⚙ 拖拽格式的坑（v2.5.0）：原来取拖拽对象用 `e.Data.GetData(typeof(MaintenanceCard))`，
`DataObject` 按**精确类型名**查格式，一旦出现第二种卡片（`TodoCard`）就会**静默取不到**——
拖放看起来失效但不报错。现两种卡片统一继承 `BoardCardBase`，
拖拽数据放在自定义格式 `"FctAggregator.BoardCard"` 里，落列判定改用 `card.ColumnKey`。

### 8.4 Windows 原生桌面提示（v2.5.0，`DesktopNotifier.cs`）

| 设计 | 说明 |
|------|------|
| 技术选型 | 托盘 `NotifyIcon` + `ShowBalloonTip`。Win10/11 下即系统原生 Toast（进通知中心、跟随专注助手），**不引 `Microsoft.Toolkit.Uwp.Notifications`、不注册 AUMID** —— 车间机台零额外依赖 |
| 线程模型 | `Init()` 在 UI 线程调用（`MainForm` 构造尾部），记住 `SynchronizationContext`；`NotifyFail()` 可从 `FileSystemWatcher` 的后台线程调用，只入队 |
| 节流 | UI 线程上 1s 心跳 `Flush()`：距上次弹出不足 `notify_min_interval_sec`（默认 15s）就攒着；多条合并成 `⚠ FCT 不良 ×N` + 前两条摘要 |
| 文本上限 | BalloonTip 超过约 255 字符 Windows 会**直接不显示**，故统一截到 220 字符 |
| 点击行为 | `BalloonTipClicked` / 双击托盘 → `Activated` 事件 → `MainForm` 还原窗口 + `ShowPage(2)` 直达待办看板 |
| 退出清理 | `OnFormClosing` 里 `Shutdown()` 显式 `Visible=false` + `Dispose()`，否则托盘图标会残留到鼠标滑过 |
| 触发条件 | 与飞书推送一致：仅实时监控路径、历史扫描完成后、机台号非 UNKNOWN |
| 开关 | `config.json` 的 `desktop_notify`（缺省 true，仅显式 `false` 关闭）；托盘右键与调试页可临时切换；调试页【桌面提示测试】可验证系统未屏蔽 |

---

### 8.5 GUI 布局约定（v2.8.0）

`MainForm` 外壳 = 侧栏(Left) + 顶栏(Top) + 进度条(Top) + 状态栏(Bottom) + 内容区(Fill)，
页面标题只在顶栏出现（子面板内不再重复大标题），视觉规范全部取自 `Theme`。

⚠ **WinForms 停靠顺序是反的**：`Dock` 布局按 **z-order 从后往前**处理，
也就是**最后 `Add` 的最先停靠**。所以：

| 想要的效果 | 必须的 Add 位置 |
|---|---|
| `Dock=Fill` 吃剩余空间 | **最先 Add** |
| 侧栏占满整列高（最外层） | **最后 Add** |
| 侧栏里「总览」在最上面 | 在同组 `Dock=Top` 里**最后 Add** |

写反了**不会报错**，只是内容区会占满整个客户区、其它栏盖在它上面（侧栏高度会短一截）。
v2.8.0 第一版就踩了这个坑，靠自检的几何断言（量 `Left/Top/Width/Height`）才抓出来 ——
所以 `SelfTest` 里保留了一组「GUI 布局」断言，改布局后必须跑。

辅助工具：`tools/snap_gui.ps1` —— 解压完整包、灌真实库、启动后 `Ctrl+1..5` 逐页截图到 `.snap\`。

### 8.9 重复功能合并（v2.9.3）

| 原重复 | 现状 |
|---|---|
| 4 份手写 xlsx 写出器 | `FctShared.Xlsx`（`Xlsx.cs`）一份**外壳**：zip/部件/sheet XML/转义/列宽/冻结/合并；各工具只留自己的 `S_*` 与 `Styles()`，`Cell`/`Sheet` 用 `using` 别名 → 调用点几乎不动，报表长相不变 |
| `StationDetector` ×2 | 删掉数据分发工具里那份（IP 表逐条核对一致），统一调 `FctAggregator.StationDetector` |
| `XmlViewerForm` ×2 | 删掉主程序 112 行简版，统一用 `FctFailRanker.XmlViewerForm`（功能超集） |

**为什么不统一 `styles.xml`**：四家样式表分别是 7/4/17/2 种样式、配色不同，
统一等于改几百处样式号且报表外观会变。共享写出器因此把 `stylesXml` 作为参数收进来 ——
**外壳一份，长相各自**。

刻意保留的同名类（不同职责，别合）：`FailItem`（多 `StepType`、扫描口径不同）、
`Exporter`（xlsx 汇总 vs csv/json 波形）、`XmlParser`/`XmlScanner`/`XmlTimeScanner`/`Scanner`
（解析单文件 / 扫描排名 / 只读时间戳 / 定位文件）。

⚠ 共享写出器里两个必须保留的细节：XML 非法控制字符要丢弃（否则 Excel 报"文件已损坏"）；
工作表名的 `\ / ? * [ ] :` 换下划线，但 `<` `>` **合法**、只做 XML 转义。

#### 长相回归怎么验（2026-07-31 补）

"报表长相不变"这句话不能靠嘴说。工具链：

| 工具 | 干什么 |
|---|---|
| `tools\xlsxdump\` | 同一份 fixture，四个导出口各导一次真实 xlsx。**只调高层入口**，不碰 `Cell`/`Sheet`/`Xlsx` —— 所以同一份源码在 v2.9.2（去重前）代码上也能编译 |
| `tools\xlsx_check.ps1 -Compare` | `git worktree` 检出 `a06d9b7`(v2.9.2) 用**同一份 fixture** 再导一次，然后对比 |
| `tools\xlsx_cmp.py` | 比 `styles.xml` 逐字节 / 列宽 / 冻结行 / 合并单元格 / **每个单元格的(值, 样式号)** |
| `tools\xlsx_style_report.py` | 把 `styles.xml` 翻成人话（逐样式号列字体/底色/边框/对齐/数字格式） |

实测结论：分发台账、FAIL 排行、取数清单 **完全一致**；
维修记录**多了「冻结首行」**（`FreezeRows = 1` 是 v2.9.3 顺手加的，v2.9.2 无冻结）——
判定为改进，保留，并把措辞改成实话（原注释"输出格式不变"与原断言"**仍**冻结表头"都不准确）。

⚠ 维修记录的长相现在**钉在自检里**：12 个列宽值逐个比对 + 表头底色 `#4472C4` +
白粗字 + 微软雅黑。改这几样会直接让自检红。

⚠ fixture 注意：真实库 `maintenance_records` 是 0 条，harness 会在**库副本**上补 6 条样例
（绝不碰 `dist\data\fct.db`）；FAIL 样本按不同 SN 复制数份，为的是让斑马纹样式显现。

### 8.8 风格统一：递归主题器（v2.9.2）

`Theme.Apply(Control root)` 逐层走控件树按类型上色，用于把四个内嵌工具
（WinForms 默认灰）与主程序页面刷成同一套外观。

**铁律：只改外观，不碰布局。** 四个工具是绝对坐标大表单，改尺寸/字号必错位。因此：
- 不动 `Bounds` / `Dock` / `Anchor` / `Padding` / `Margin`；
- `Font` 只设在页面根上，子控件靠**环境继承**，显式设过字体的保持原样；
- 只覆盖"还是系统默认色"的属性（`IsDefaultish()` 判定），工具刻意涂过色的主按钮 /
  状态灯 / 警告文字都保留；
- 自检有硬断言：**刷前刷后逐控件比 `Bounds`，一个都不许变**（138 个控件）。

**跳过且不递归**（`IsSkipped()`）：`NavButton` / `KpiCard` / `ChipBar` / `SectionPanel` /
`ToolHost` / `BoardCardBase` / `MaintenanceBoard` / `RichTextBox` / `ProgressBar` /
`WaveformPanel`。任何控件挂 `Tag = Theme.SkipTag` 也可单独豁免。

调用点：`ToolHost.Ensure()`（内嵌工具时）+ `MainForm.BuildUi()`（主程序五页）。

⚠ 限制：`ListView` 列头在 WinForms 无法直接改色（需 owner-draw 整个控件），
FAIL 记录 / 维修列表 / 待办列表的列头仍是系统样式。

### 8.7 工具内嵌为页面（v2.9.1）

四个工具不再弹独立窗口，而是内嵌进主窗口内容区，成为 PageIndex 5~8 的页面。

| 关注点 | 做法 |
|---|---|
| 把 `Form` 当子控件 | `TopLevel = false` + `FormBorderStyle = None` + `Dock = Fill`，再 `Show()`；封装在 `ToolHost : Panel` 里 |
| 启动速度 | **惰性构造**：`ShowPage` 第一次切到该页才 `Ensure()`；自检断言"启动时 `Embedded == null`" |
| 工具比窗口大 | 工具自带 `MinimumSize`（最大 1040×790）→ 宿主 `AutoScroll = true`；主窗口默认 1360×920、最小 1240×860 |
| 工具自己 `Close()` 掉 | `Ensure()` 检测 `IsDisposed` 后重建 |
| 构造失败 | 就地显示红色错误 Label + 记日志，不弹框、不崩主程序 |

⚠ **`Control.FindForm()` 在 `Form` 自身上返回 `this`**（实现是"从自己往上找第一个 Form"）。
所以内嵌后工具里 `ShowDialog(this)` / `FindForm()` 都拿不到真正的顶层窗，
必须用 **`TopLevelControl`**：

```csharp
dlg.ShowDialog((IWin32Window)(TopLevelControl ?? this));
```
现有 2 处受影响（分发工具的修时间戳范围框、FAIL 排行的 XML 查看器），已改。
自检里有 4 条 `TopLevelControl == 主窗口` 断言盯着这条。

`Program.RunToolGui`（CLI 子命令 `distribute` / `rank` / `tdms`）走的仍是**顶层窗口**模式，
和内嵌互不影响 —— 需要把工具摆到另一个显示器时用它。

### 8.6 单 exe 合并（v2.9.0）

五个原独立工程合进 `FctAggregator.csproj` 一个程序集。三条关键约定：

| 问题 | 做法 |
|---|---|
| 10 组同名类（v2.9.0 时 `MainForm` ×5、`Program` ×4、`XlsxWriter` ×2…；v3.0.0 拆出分发器后为 `MainForm` ×4、`Program` ×3） | **不改名**，靠各自 `namespace`（`FctAggregator` / `FctFailRanker` / `FctFetcher` / `FctTdmsViewer`；分发器独立成 `FctDistributor` 程序集）隔离 |
| 五个 `Main` → `CS0017 多个入口点` | csproj `<StartupObject>FctAggregator.Program</StartupObject>`；其余 `Main` 留着（含 CLI），由主入口按子命令转发 |
| WinExe 下 CLI 没输出 | `AttachConsole(-1)` **且** `Console.SetOut(new StreamWriter(Console.OpenStandardOutput()))`；只 Attach 不 SetOut 会被 `TextWriter.Null` 吞掉 |

其它落地细节：
- 嵌入资源名随目录变 → `TemplateGenerator.EmbeddedPrefix` 必须同步改成
  `FctAggregator.modules.Distributor.tpl.`（否则模板名显示成一串命名空间）。
  ⚠ v3.0.0 分发器拆出后：前缀固定为 `FctDistributor.tpl.`，由两个工程的 csproj
  用 `LogicalName` **写死**，不再随目录/命名空间漂移（详见 ../fct-distributor/README.md）。
- 工具窗口用 `Form.Show(this)` 非模态 + `Dictionary<string,Form>` 单例去重；
  `FormClosed` 里移除，避免第二次点开时拿到已 Dispose 的窗口。
- 单实例互斥锁**只在 GUI 分支**加，否则主程序开着就跑不了 CLI 子命令。
- 自检工程用 `<Compile Include="..\modules\**\*.cs" />` 把工具源码一起编，
  因此它也需要 `<StartupObject>`。

⚠ 打包脚本的坑：无 RID 的 `dotnet publish` 不把原生库放根目录，
`e_sqlite3.dll` 只在 `runtimes\win-x64\native\`。`make_package.ps1` 现用
`Resolve-DistFile` 递归查找（优先 win-x64）；`启动.bat`/`config.json` 从仓库根取。

### 8.10 应用图标（v2.9.4）

`app_icon.ico` 是**多分辨率 ICO**（16/32/48/64/128/256，PNG 压缩条目），
源 PNG 在 `assets/icon/`，用 `python tools/make_icon.py` 重建
（脚本直接写 ICO 容器，不让 PIL 二次缩放，保证与设计稿逐像素一致）。

取用**只走 `AppIcon`**：`Load()` / `Load(size)` / `Apply(form)`，
顺序是**嵌入资源 → 同目录文件 → 系统图标**，带缓存、永不抛。

⚠ 踩过的坑：v2.9.4 之前有四处直接 `new Icon(BaseDir\app_icon.ico)`，
而 `make_package.ps1` 的清单里**从来没有这个文件** —— 于是现场窗口退回 exe 内嵌图标、
托盘退回 `SystemIcons.Application`（灰色通用图标）。现在两头都堵：
打包带上该文件，加载又以嵌入资源为首选。自检里有断言确认"拿到的不是系统灰图"。

## 9. 构建与部署

### 9.1 构建
```bash
cd fct-aggregator-cs
dotnet build -c Release          # 输出 bin/Release/net8.0-windows/
dotnet publish -c Release -o dist --self-contained false

# 自检（拿 dist\data\fct.db 的**副本**在临时目录里跑，不碰真实数据）
dotnet run --project selftest\SelfTest.csproj -c Release
```

> 自检覆盖 **669 项断言**（v3.8.0 实测计数）：`closed` 迁移与备份、迁移幂等、五状态计数与全表对得上、
> `updated_at` 取得到与排序、全字段更新、拖动只改状态、往期日期不顶前、
> xlsx/csv 各 12 列且无漏译英文 key、**id 回收四种场景**、**卡片左/右键行为**、
> **故障项去重（含拿真实库验 62 条 FAIL → 7 个项）**、**批量建单**、
> **维修人员名单（增/删/改名/同步与不同步/建表幂等）**、
> **多人（拆合/成员级包含与改名/候选不出组合项/多选框）**。
> 导出产物另用 pandas/openpyxl 反向读过。

### 9.2 部署
1. 机台安装 **.NET 8 Desktop Runtime**（一次性）
2. 解压发布包，检查 `config.json`（机台号 / 结果目录 / ini 路径 / webhook）
3. 运行 `Argus.exe`，或用 `启动.bat`（`dotnet Argus.dll`，
   机台上无 exe 可被静态查杀）
4. 需要开机自启：Debug 页 → 「开机自启」按钮（写启动文件夹快捷方式）
5. 升级：退出程序 → 覆盖程序文件 → **保留 `config.json` 与 `data/`**

### 9.3 杀毒（EDR）兼容要点
现场 SentinelOne 曾反复查杀 Python 打包版，C# 版针对性规避：
1. 不用自包含单文件（避免运行时向临时目录释放 native dll，该行为被判 dropper）
2. 不写注册表 `Run` 键，改启动文件夹快捷方式
3. 不在启动时自动写自启项（去掉“自我持久化”特征）
4. 可只分发 DLL + `启动.bat`，不落 exe

---

## 10. 与 Python 版的差异

| 维度 | Python v1.0.2 | C# v2.2.0 |
|------|---------------|-----------|
| UI | PyQt6 | WinForms |
| 打包 | PyInstaller / Nuitka，129MB，常被查杀 | 框架依赖，1.2MB |
| XML 根节点 | 找 `<root>`（**实际数据是 BATCH，解析不出**） | 正确解析 BATCH |
| FCT.ini | 独占打开，被占用时失败 | 共享读取 + 多路径回退 |
| 当日统计 | 依赖 XML 时间戳，常为 0 | 用目录日期 `test_date` |
| 监控 | fts_monitor 递归全盘 + watcher per-model **重叠 → FAIL 重复推送** | 仅 per-model watcher，单条流水线 |
| HTTP API | FastAPI（`/health` `/stats` 等） | 已下线（单机不需要，可恢复） |
| 日报 | 曾有 xlsx 日报（后从源码消失） | 未实现 |
| 多机聚合 | central_aggregator（v1.0.2） | **C# 版 v3.5.0 起为 P2P Mesh**（每机一个节点 + Web 看板，见 §3.1）；v3.0.0~v3.4.x 曾有共享目录/HTTP 中心聚合（已 `[Obsolete]`） |
| TUI | Textual 终端界面 | 无 |

---

## 11. 已知限制与后续规划

### 11.1 当前限制
- 跨机台聚合已是 P2P Mesh（v3.5.0+）：副本库只存 FAIL/heartbeat 摘要行，**XML 报告内容不入库**，
  看板按需从源机 HTTP 拉取 —— 源机离线时报告 404（有 `xml_available` 标记可提示）
- Web 看板良率页（`/api/stats` 的良率日统计 `yld_daily`）**尚在建设中**（v3.8.1 起的 P1 工作）；
  维修 / 设备 / 设置页同样是占位
- 无 Git 版本管理，变更靠 `更新日志.md` + 本文档手工维护
- 端口语义注意：headless/机台节点监听 **`mesh_port`**；设置页改的也是 `mesh_port`
  （v3.8.0 已对齐；旧 `agg_http_port` 只服务遗留中心模式）
- 维修记录看板（v2.2.0）：
  - 每列最多加载 120 张卡（列头徐章仍是真实总数）；超过的靠列表视图看
  - 拖拽只能改**状态**，不能在列内手动排序（无优先级字段，固定按最后更新时间倒序）
  - 无撤销：拖错了靠再拖回去（会多一次 `updated_at` 刷新）
  - CSV 导出带三行标题块（标题/导出时间/空行）后才是表头 —— Excel 直开没问题，
    但 pandas 之类工具 `read_csv` 需 `skiprows=3`（沿用 v2.1.0 格式，未改动）

### 11.2 候选后续工作（按现场价值排序）
1. **Web 看板良率页**：`yld_daily` 日统计落库 + 良率趋势图（P1，v3.8.1 起）
2. **机台排名 / 失败项 Pareto**：定位最差机台与头部失败项，支撑“测量系统能力”整治
   （离线分析工具已并入本套件「FAIL 排行」页 `modules/FailRanker/`，可直接复用其聚合逻辑）
3. **误测率统计**：同 SN 重测后 PASS → 疑似误测，统计误测率（假 fail 不扣良率但吃产能）
4. **FAIL 聚类告警**：时间窗内同失败项 N 次 → 合并成一条飞书告警
5. **定时日报**：良率 / FAIL TOP / 机台排名，文本卡片直推飞书（避免 xlsx 占用问题）
6. 维修记录看板：卡片内手动排序（需加 `sort_order` 列）、状态变更历史留痕

### 11.3 相关项目
| 项目 | 说明 |
|------|------|
| `../fct-distributor` | FCT 数据分发器 v1.9.0（v3.0.0 拆出独立）：模板直生 / 源目录分发 / 修时间戳，只发工程人员、不进产线包；本工程与它有 `Xlsx.cs` / `StationDetector.cs` / `AppIcon.cs` 三份副本（自检盯一致性） |
| `../legacy/fct-fetcher-python` | 取数工具的 Python 版存档（主程序 Python 版源码无任何存档，仅本文第 10 章差异描述可考） |
