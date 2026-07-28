# FCT-Aggregator 技术文档（C# 版）

**项目**：FCT 测试结果聚合系统（工控机端）
**当前版本**：v2.1.0
**技术栈**：C# / .NET 8 / WinForms / SQLite
**文档更新**：2026-07-27
**前身**：Python v1.0.2（PyQt6 + FastAPI + PyInstaller），已于 2026-07-27 删除，
源码备份保留在 `F:\Programe\G4_9-QA\fct-aggregator-python-v1.0.2-源码备份.tar.gz`

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
附带设备状态查看（读 FCT.ini）、维修记录管理（含 Excel/CSV 导出）、
FAIL 记录报告查看、调试工具页。

### 1.1 核心功能

| 功能 | 说明 |
|------|------|
| 实时监控 | 每个 `{category}/{model}` 目录挂 `FileSystemWatcher`，新 XML 落盘即处理 |
| 历史扫描 | 启动后台两阶段扫描（收集文件 → 分批解析入库），带进度条，不限天数 |
| XML 解析 | BATCH 格式；PASS 走轻量路径、F_/O_ 走深度路径 |
| 分类统计 | PASS / FAIL / INTERRUPTED / INVALID，累计 + 当日 + 良率 |
| 飞书告警 | FAIL 记录推交互卡片，含失败步骤名称 / 实测值 / 上下限 |
| 设备状态 | 解析 `FCT.ini`：型号列表、固件版本、COM/USB 设备在线灯、A2L 文件 |
| 维修记录 | 增 / 查 / 改状态 / 删 + 导出 xlsx / csv（零第三方依赖 OOXML） |
| FAIL 记录 | 本机全部 FAIL 列表，双击弹出报告式查看器（完整测试流程，失败项标红） |
| 调试工具 | 推送测试 / 数据库状态 / 机台检测 / 型号发现 / 查询 / 自启开关 / 清库 |
| 运行保障 | 单实例 Mutex、开机自启（启动文件夹快捷方式，手动开关） |

### 1.2 运行环境

- Windows 10（工控机）
- **.NET 8 Desktop Runtime**（框架依赖发布，机台需一次性安装）
- 发布体积约 1.2 MB（Python 版 129 MB）
- 无需管理员权限；`data/` `logs/` 与程序同级

---

## 2. 目录结构

```
fct-aggregator-cs/
├── Program.cs                # 入口：单实例 Mutex → 读配置 → 启动 Engine → Application.Run
├── Config.cs                 # AppConfig：读 config.json（单例 + BaseDir 解析）
├── Engine.cs                 # 后台引擎：型号发现 / 实时监控 / 历史扫描 / FAIL 推送
├── Processor.cs              # 单文件处理：路径解析 + 分类 + 组装 TestRecord
├── XmlParser.cs              # BATCH XML 解析（深度 / 轻量 / 报告三种模式）
├── Classifier.cs             # 前缀分类 + debug 判定
├── StationDetector.cs        # 机台号 IP 映射 / 型号校验 / AutoStart（.lnk 自启）
├── Database.cs               # SQLite：建表、批量入库去重、统计、维修记录、FAIL 查询
├── TestRecord.cs             # TestRecord / FailedTest 数据模型
├── AppState.cs               # 全局共享状态（统计 / 扫描进度），线程安全
├── Logger.cs                 # 文件日志 logs/app.log + GUI 内存队列（上限 5000 行）
├── FeishuNotifier.cs         # 飞书群机器人卡片推送
├── FctIni.cs                 # FCT.ini 解析 + COM 口在线检测（共享读取模式）
├── MainForm.cs               # 主窗口：进度条 / 页面切换 / 统计面板 / 日志窗
├── DeviceStatusPanel.cs      # 设备状态页
├── MaintenancePanel.cs       # 维修记录页 + 录入表单 MaintenanceForm
├── MaintenanceExporter.cs    # 维修记录导出 xlsx / csv（零依赖 OOXML）
├── DebugPanel.cs             # 调试工具页
├── FailListPanel.cs          # FAIL 记录页 + XmlViewerForm 报告查看器
├── FctAggregator.csproj      # net8.0-windows / WinForms / SelfContained=false
├── config.json               # 运行配置
├── app_icon.ico
├── data/                     # SQLite 数据库（{station_id}.db）
├── logs/                     # app.log
└── dist/                     # 发布产物（含 启动.bat）
```

### 2.1 依赖包

| 包 | 版本 | 用途 |
|----|------|------|
| `Microsoft.Data.Sqlite` | 8.0.10 | SQLite 访问 |
| `System.IO.Ports` | 8.0.0 | `SerialPort.GetPortNames()` 检测 COM 口 |

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
1. `Mutex("Global\FCT_Aggregator_SingleInstance")` → 已运行则弹框退出
2. `AppConfig.Instance` 读 `config.json`（缺失走默认值并告警）；`Logger.SetLevel`
3. `new Engine(cfg)`：解析机台号 → 建 `data/{station}.db` → 打印启动信息
4. `engine.Start()`：校验 results_root → 型号发现 → 挂 watcher → 起历史扫描任务
5. `Application.Run(new MainForm(engine))`；窗口关闭后 `engine.Stop()` 释放 watcher

> 注意：开机自启**不在启动时自动写入**（`AutoStart.EnsureFirstRun()` 已注释），
> 因为“启动即自我持久化”是 EDR（SentinelOne）重点拦截的行为特征，改为 Debug 页手动开关。

---

## 4. 模块详细说明

### 4.1 `Program.cs`
入口。单实例保护 → 配置 → 引擎 → 窗口。退出时 `engine.Stop()`，`GC.KeepAlive` 保住 Mutex。

### 4.2 `Config.cs` — `AppConfig`
- 单例 `AppConfig.Instance`，从 `AppContext.BaseDirectory/config.json` 读取。
- **`BaseDir` 用 `AppContext.BaseDirectory` 而非 `Environment.ProcessPath`**：
  用 `dotnet FCT-Aggregator.dll` 启动时 ProcessPath 是 `dotnet.exe`，会导致 db/log 落错目录。
- 字段：`station_id` / `results_root` / `fct_ini_path` / `webhook_url` /
  `skip_historical_scan` / `log_level`（config.json 里其余字段为历史遗留，程序不读）。

### 4.3 `Engine.cs`
| 方法 | 职责 |
|------|------|
| 构造 | 机台号解析（config 优先 → IP 识别）、建库、写启动日志 |
| `Start()` | results_root 校验（不存在→status=error 并停止采集）、型号发现、挂 watcher、起历史扫描 |
| `DiscoverModels()` | 扫 `Online`/`Offline` 下符合 `^E\d{7}$` 的目录名，去重排序 |
| `HistoricalScan()` | 两阶段：①枚举全部 xml（每 200 个更新总数）②分批 100 解析入库 |
| `ProcessRealtime()` | 等文件稳定 → 解析 → 入库 → 刷新统计 → FAIL 推送 |
| `WaitForStable()` | 文件大小连续 2 次不变（500ms 间隔，10s 超时）视为写完 |
| `Stop()` | 取消 token、关闭并释放所有 watcher |

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
- `GetExistingPaths()`：分批 500 的 `IN` 查询做去重预筛
- `BatchInsert()`：单事务 + `INSERT OR IGNORE`（`xml_path UNIQUE` 兜底去重）
- `FetchGlobalStats()` / `FetchDailyStats()`：按 station 过滤，返回 PASS/FAIL/INTERRUPTED/INVALID/产品数
- 维修记录：`CreateMaintenance` / `ListMaintenance(statusFilter)` / `UpdateMaintenanceStatus` / `DeleteMaintenance`
- FAIL 查询：`RecentFails(limit)`（Debug 页）、`AllFails(stationId)`（FAIL 记录页，时间倒序）
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
| `equipment_model` / `equipment_sn` | 设备型号 / SN |
| `fail_item` NOT NULL | 故障项目 |
| `fail_reason` | 故障描述 |
| `severity` | `critical` 严重 / `major` 一般 / `minor` 轻微（默认 major） |
| `status` | `open` 待处理 / `in_progress` 进行中 / `resolved` 已解决 / `closed` 已关闭 |
| `resolver` / `resolution` / `notes` | 维修人 / 措施 / 备注 |
| `created_at` / `updated_at` | 时间戳（创建时间可在表单手动指定） |

索引：`idx_maint_status(status)`

> 表结构自 v2.0.0 起未变，v2.0.0 → v2.1.0 可直接覆盖程序、原地复用 db。

---

## 7. 配置说明

`config.json`（程序目录，UTF-8）：

| 键 | 默认 | 说明 |
|----|------|------|
| `station_id` | `""` | 留空则按 IP 自动识别 |
| `results_root` | `D:\Results` | 测试结果根目录 |
| `fct_ini_path` | `C:\FTS\Apps\XPT\Cfg\FCT.ini` | 设备状态页数据源 |
| `webhook_url` | `""` | 飞书群机器人；空则不推送 |
| `skip_historical_scan` | `false` | true 则只做实时监控 |
| `log_level` | `INFO` | DEBUG / INFO / WARNING / ERROR |

历史遗留但**当前程序不读**的键：`auto_start`、`fts.*`、`api_key`、`cleanup_admin_key`、
`api_host`、`api_port`（HTTP API 已下线）。

---

## 8. 界面结构

主窗口 `1200×720`（居中启动），顶部进度条（仅扫描时显示）+ 中部页面区 + 底部按钮栏。

| 页 | 内容 |
|----|------|
| **主页** | 左：统计面板（累计 / 今日 / 良率 / 型号数 / 状态）；右：实时日志窗（可拖分隔条） |
| **设备状态** | FCT.ini 解析结果：型号、固件版本、COM/USB 设备（在线小灯）、A2L 文件；一屏无滚动、刷新不闪烁 |
| **维修记录** | 列表 + 新增表单 + 状态筛选 + 删除 + 导出（xlsx / csv，导当前筛选） |
| **FAIL记录** | 本机全部 FAIL（SN / 首个失败项 / 型号 / 时间），双击 → `XmlViewerForm` 报告（完整测试流程，失败项标红） |
| **调试** | 推送测试、数据库状态、机台检测、型号发现、设备状态、最近 10 条 FAIL、今日统计、最大 SN、查看配置、开机自启开关、清空数据库 |

底部另有「清空日志」「退出」。窗口标题显示版本号，便于现场确认机台上的版本。

---

## 9. 构建与部署

### 9.1 构建
```bash
cd fct-aggregator-cs
dotnet build -c Release          # 输出 bin/Release/net8.0-windows/
dotnet publish -c Release -o dist --self-contained false
```

### 9.2 部署
1. 机台安装 **.NET 8 Desktop Runtime**（一次性）
2. 解压发布包，检查 `config.json`（机台号 / 结果目录 / ini 路径 / webhook）
3. 运行 `FCT-Aggregator.exe`，或用 `启动.bat`（`dotnet FCT-Aggregator.dll`，
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

| 维度 | Python v1.0.2 | C# v2.1.0 |
|------|---------------|-----------|
| UI | PyQt6 | WinForms |
| 打包 | PyInstaller / Nuitka，129MB，常被查杀 | 框架依赖，1.2MB |
| XML 根节点 | 找 `<root>`（**实际数据是 BATCH，解析不出**） | 正确解析 BATCH |
| FCT.ini | 独占打开，被占用时失败 | 共享读取 + 多路径回退 |
| 当日统计 | 依赖 XML 时间戳，常为 0 | 用目录日期 `test_date` |
| 监控 | fts_monitor 递归全盘 + watcher per-model **重叠 → FAIL 重复推送** | 仅 per-model watcher，单条流水线 |
| HTTP API | FastAPI（`/health` `/stats` 等） | 已下线（单机不需要，可恢复） |
| 日报 | 曾有 xlsx 日报（后从源码消失） | 未实现 |
| 多机聚合 | central_aggregator | 已下线 |
| TUI | Textual 终端界面 | 无 |

---

## 11. 已知限制与后续规划

### 11.1 当前限制
- 单机运行，无跨机台汇总（多机汇总需另做）
- 无 HTTP API，外部系统无法拉数据
- 无日报 / 定时汇总
- FAIL 告警逐条推送，同一失败项密集出现时会刷屏（无聚合）
- 无 Git 版本管理，变更靠 `更新日志.md` + 本文档手工维护

### 11.2 候选后续工作（按现场价值排序）
1. **机台排名 / 失败项 Pareto**：定位最差机台与头部失败项，支撑“测量系统能力”整治
   （离线分析已有独立工具 `../fct-fail-ranker`，可考虑并入或复用其聚合逻辑）
2. **误测率统计**：同 SN 重测后 PASS → 疑似误测，统计误测率（假 fail 不扣良率但吃产能）
3. **FAIL 聚类告警**：时间窗内同失败项 N 次 → 合并成一条飞书告警
4. **定时日报**：良率 / FAIL TOP / 机台排名，文本卡片直推飞书（避免 xlsx 占用问题）
5. 工程化：建 Git 仓库、加单元测试（解析 / 分类 / 良率口径）

### 11.3 相关项目
| 项目 | 说明 |
|------|------|
| `../fct-fail-ranker` | FCT 不良项排名导出工具 v1.5.0，离线按时间段汇总 + 排名 + Excel/CSV 导出，业务规则与本项目一致（另多排除 `UUT Status Err`） |
