# Argus 主程序（FctAggregator）— 纯单机 FCT 数据采集分析工具

车间 FCT 测试机台上的一站式本机工具：监控 XML 落盘 → 解析分类入库 → FAIL 列表 / 维修待办 / 正常态自学习 / 飞书告警。

> ⚠ **v3.31.0 起为纯单机工具**：`Agg/`、`Mesh/`、`public/`（Web 看板）、`AggDatabase`/`DbMigrator`、`AggCenterForm` 等已删除；不再提供 `agg` 子命令、P2P 组网、HTTP 服务与浏览器看板。本机采集/FAIL/维修待办/自学习等本机能力完整保留（分析页已于 v3.50.0 移除）。

| 模块 | 干什么 | 入口（内嵌页 / 命令行）|
|---|---|---|
| **本机采集监控** | 监控测试机 XML 落盘 → 解析分类入库 → 统计良率 / 待办维修 / FAIL 告警（飞书 + Windows 桌面提示） | 直接启动 |
| FAIL 排行 v1.5.0 | 扫 FAIL 记录按不良项排名，导出 xlsx / csv，内置 XML 报告查看 | Ctrl+? / `rank` |
| 取数打包 v1.0.0 | 按日期区间捞 Results + TDMS，打包成 zip / 汇总 xlsx（带 CLI） | `fetch` |
| TDMS 波形 v1.1.0 | 打开 TDMS 记录看波形、导出 csv/json（带 CLI） | `tdms` |
| 待办维修闭环 | 故障归并（G49 字典）、维修看板 4 状态机、人员名单/改名同步 | 主页「维修/待办」页 |

> 深度分析（规格 06）页已于 v3.50.0 移除：漂移 / CPK / 时段归因 / tester 效应量 / TDMS 快照不再运行。

## 快速开始

```powershell
# 机台部署：解压完整包，改好 config.json，双击 Argus.exe
Argus.exe                    # 主程序（左侧导航切页）
Argus.exe --help             # 全部子命令
Argus.exe tdms x.tdms        # 直接看某个波形文件
Argus.exe fetch --help       # 取数工具命令行
```
主程序页快捷键与入口见窗口内状态栏提示（`Ctrl+1~6` 切页，`Ctrl+7` TDMS）。

## 运行环境

- Windows 10/11 + **.NET 8 Desktop Runtime**（框架依赖发布，包体最小）
- 无需数据库服务：本地 SQLite（`data\fct.db`）

## 目录结构

```
FctAggregator/
├── FctAggregator.csproj      唯一工程（WinExe，StartupObject = FctAggregator.Program）
├── Program.cs                入口（子命令分发：GUI / fetch / tdms / rank / upgrade / backfill / learn / logs）
├── Core/                     采集引擎与数据模型（Engine/Processor/XmlParser/StationDetector/BackfillTool/FctProgramBackup/...）
├── Db/                       Database（本机库：建表/入库/统计/维修/待办/分析表，幂等 Init，按功能 partial 拆分）+ DbMaintenance（每日维护器）+ StorageOptimizer（存储健康/优化）
├── Intelligence/             正常态自学习（LearningEngine/NormalLearners/DeviationScorer/LearnPipeline/...）；漂移/CPK/归因/TDMS 类保留但生产链路无调用（v3.50.0）
├── Ui/                       WinForms 全家（MainForm/LearningPanel/各 Maintenance/FailList Panel/Theme/UiWidgets/...）
├── Infra/                    横切基础设施（Config/ConfigWatcher/Logger/TimeUtil/CsvUtil/Xlsx/FeishuNotifier/UpdateChecker）
├── Parsing/                  插件式 XML 解析器（IResultParser / ParserRegistry）
├── modules/                  四个工具箱模块，各自保留 namespace
│   ├── FailRanker/           FctFailRanker
│   ├── Fetcher/              FctFetcher
│   ├── TdmsViewer/           FctTdmsViewer
│   └── Upgrader/             upgrade 子命令升级向导
├── selftest/                 主自检（1273 项断言，聚合用例已于 v3.31.0 裁剪）
├── tools/                    打包与冒烟脚本（xlsx 长相新旧对比 xlsx_check/xlsxdump）
├── 更新日志.md               面向技术（改了什么、为什么、踩了什么坑）
├── 版本更新说明.md           面向使用（怎么点、看什么）
└── README.md                 工程说明（架构见 CODEBUDDY.md + docs/）
```

## 开发

```powershell
dotnet build FctAggregator.csproj -c Release          # 编译（要求 0 警告 0 错误；产物在 bin\Release\net8.0-windows\）
cd selftest\bin\Release\net8.0-windows
.\FctAggregator.SelfTest.exe > selftest_run.log 2>&1   # 全量自检（必须全绿；当前 1273 项断言，禁止管道直连）

.\tools\make_package.ps1                             # build 后跑一次：铺平随包文件 + 自动生成两个 zip（完整+更新包）
.\tools\smoke_gui.ps1                                # 起窗口、看标题、查日志 ERROR
.\tools\smoke_notify.ps1                             # 丢一条 FAIL → 桌面提示 + 待办登记
.\tools\smoke_cli.ps1                                # 子命令 / CLI stdout / 单实例
```

**改动后至少跑**：`build` + `SelfTest` + `smoke_gui`。动到 UI 布局再跑截图核对，动到子命令/CLI 再跑 `smoke_cli`。

## 约定（踩过的坑，别再踩）

- **WinForms 停靠顺序是反的**：最后 `Add` 的最先停靠。`Dock=Fill` 的要最先 Add，侧栏要最后 Add。
- **四个 `Main` 靠 `<StartupObject>` 消歧**，别删各工具的 `Main`（里面有 CLI）。
- **数据分发已不在本工程**：2026-09-02 起在独立仓库 `e:/FctDistributor`。那边有 `Xlsx.cs` / `AppIcon.cs` /
  `StationDetector.cs` 的**副本**，改本工程这三个文件时顺手同步另一侧（无自动比对，靠纪律）。
- **内嵌的 Form 里别用 `FindForm()`**：拿顶层窗要用 `TopLevelControl`。
- **要图标就用 `AppIcon.Load()/Apply()`**：别 `new Icon(BaseDir\\app_icon.ico)`，发布包不一定有那个文件。
- **要导 Excel 就用 `FctShared.Xlsx`**：外壳共享，自己只提供 `styles.xml` 与样式号，别再手写 zip/部件。
- **新面板别自己写颜色字号**：取 `Theme` 里的令牌；主题器**不许改布局**（自检会逐控件比对）。
- **WinExe 里 CLI 要输出**：`AttachConsole(-1)` 之后必须 `Console.SetOut(...)`。
- **待办来自真实不良**：不可忽略、不可删除，只能确认 → 处理 → 拖到「已完成」。
- **本机库改表**：走 `Database` 构造的幂等 Init（`CREATE TABLE IF NOT EXISTS`），**没有 DbMigrator**。
- 提交前确保 `_releases/`、`dist/`、`.snap/`、`data/`、`logs/`、selftest 运行日志不入库。

## 历史

- 前身是 Python 版 v1.0.2（PyQt6 + FastAPI），因杀毒软件误杀于 2026-07 用 C#/.NET 8 重写。
- 老的 Python 取数脚本存档在 `../legacy/fct-fetcher-python/`。
- v3.30.1 及更早版本含聚合/互联/Web 看板；v3.31.0 起纯单机（删除明细见 `更新日志.md`）。
