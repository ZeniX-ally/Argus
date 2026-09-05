# FCT 工具套件（FCT Suite）

车间 FCT 测试线的一站式工具集。**v2.9.1 起原独立程序合并为一个 exe，工具内嵌为主窗口页面**。

> ⚠ **v3.0.0：数据分发器已拆出去，成为独立程序** `FCT-Distributor.exe`（v1.9.2，**2026-09-02 起独立仓库** `e:/FctDistributor`）。
> 它能改文件时间戳、按模板造数、批量投放 XML —— 这些操作不该出现在产线机台的主程序里，
> 误点一次后果很重。现在只发给工程/测试人员，**不进产线包**。

| 模块 | 干什么 | 入口（内嵌页 / 命令行）|
|---|---|---|
| **数据聚合**（主程序） | 监控测试机 XML 落盘 → 解析分类入库 → 统计良率 / 待办维修 / FAIL 告警（飞书 + Windows 桌面提示） | 直接启动 |
| FAIL 排行 v1.5.0 | 扫 FAIL 记录按不良项排名，导出 xlsx / csv，内置 XML 报告查看 | Ctrl+6 / `rank` |
| 取数打包 v1.0.0 | 按日期区间捞 Results + TDMS，打包成 zip / 汇总 xlsx（带 CLI） | Ctrl+7 / `fetch` |
| TDMS 波形 v1.1.0 | 打开 TDMS 记录看波形、导出 csv/json（带 CLI） | Ctrl+8 / `tdms` |
| 聚合看板（P2P Mesh，v3.5.0+） | 多机台 FAIL 聚合：每机一个节点互相推送，任意机器 `agg --web` 起 Web 看板（机台卡片 + FAIL 明细 + 在线 XML 报告，3s 自动刷新） | `agg --web` / `agg --install` |

## 快速开始

```powershell
# 机台部署：解压完整包，改好 config.json，双击 Argus.exe
Argus.exe                    # 主程序（左侧导航切页，工具箱在下方）
Argus.exe --help             # 全部子命令
Argus.exe tdms x.tdms        # 直接看某个波形文件
Argus.exe fetch --help       # 取数工具命令行
# 聚合看板：任选一台机器（或大屏机）当看板宿主
Argus.exe agg --install      # 一键部署：生成 config + 防火墙放行 8081 + 开机自启 + 启动服务
Argus.exe agg --web          # 手动常驻看板宿主（前台），浏览器开 http://本机IP:8081/
# 机台接入：在机台 config.json 的 peers 填看板宿主地址（如 ["http://10.0.0.1:8081/"]）
```
快捷键：`Ctrl+1~5` 主程序页、`Ctrl+6~8` 三个工具页、`Ctrl+9` 聚合看板、`Ctrl+R` 刷新。
三个工具是**内嵌页面**（`TopLevel=false` 的 Form）；命令行方式则以独立窗口打开。

## 运行环境

- Windows 10/11 + **.NET 8 Desktop Runtime**（框架依赖发布，包体最小）
- 无需数据库服务：本地 SQLite（`data\{机台号}.db`）

## 目录结构

```
FctAggregator/
├── FctAggregator.csproj      唯一工程（WinExe，StartupObject = FctAggregator.Program）
├── Program.cs                入口（子命令分发：GUI / agg / agg --web / --service）
├── Core/                     采集引擎与数据模型（Engine/Processor/Classifier/XmlParser/StationDetector/...
│                             遗留 AgentPusher/AggWatcher 也在此，已标 Obsolete）
├── Db/                       两个 SQLite 库 + 迁移维护（Database / AggDatabase.*.cs / DbMigrator / DbMaintenance）
├── Mesh/                     P2P 去中心化（MeshNode/Pusher/Receiver/Gossiper/QueryService/TodoSync）
├── Agg/                      聚合服务端（WebAggServer/RoutePipeline/HttpIngest/AggAlertService/FetcherService/...）
├── Intelligence/             智能化域（v3.19/3.20：预测/巡检/优先级/布局/高亮/自愈/对账器/...）
├── Ui/                       WinForms 全家（MainForm/AggCenterForm/各 Panel/Theme/UiWidgets/...）
├── Infra/                    横切基础设施（Config/Logger/TimeUtil/CsvUtil/Xlsx/FeishuNotifier/UpdateChecker）
├── Parsing/                  插件式 XML 解析器（IResultParser / ParserRegistry）
├── public/                   Web 看板静态前端（SPA 模块化，构建时复制到输出目录）
├── assets/icon/              图标源 PNG；python tools/make_icon.py 重建 app_icon.ico
├── modules/                  合入的三个工具，各自保留 namespace
│   ├── FailRanker/           FctFailRanker
│   ├── Fetcher/              FctFetcher
│   └── TdmsViewer/           FctTdmsViewer
├── selftest/                 主自检（含 GUI 几何断言、待办闭环、工具构造、Mesh/Web 服务）
├── tools/                    打包与冒烟脚本（含 xlsx 长相新旧对比 xlsx_check/xlsx_cmp/xlsxdump）
├── 更新日志.md               面向技术（改了什么、为什么、踩了什么坑）
├── 版本更新说明.md           面向使用（怎么点、看什么）
└── TECHNICAL_DOCUMENTATION.md 架构、数据库、设计约定（含 P2P Mesh §3.1）
```

## 开发

```powershell
dotnet build FctAggregator.csproj -c Release          # 编译（要求 0 警告 0 错误；产物在 bin\Release\net8.0-windows\）
dotnet run --project selftest\SelfTest.csproj -c Release   # 全量自检（必须全绿；当前 332 项断言）

# ⚠ 2026-08-01 起产物统一收敛到 bin\Release\net8.0-windows\（铺平，无 package_v*/ 子目录）：
#   程序文件 + config.json + 启动.bat + 文档 + data\ + logs\ + Argus-v3.8.0.zip（完整）+ Argus-v3.8.0-update.zip（覆盖）
.\tools\make_package.ps1                             # build 后跑一次：铺平随包文件 + 自动生成两个 zip（含更新包断言）
.\tools\smoke_gui.ps1                                # 起窗口、看标题、查日志 ERROR（自动从新路径取 zip）
.\tools\smoke_notify.ps1                             # 丢一条 FAIL → 桌面提示 + 待办登记
.\tools\smoke_cli.ps1                                # 子命令 / CLI stdout / 单实例
.\tools\snap_gui.ps1                                 # 逐页截图到 .snap\（人工核对布局）
.\tools\xlsx_check.ps1 -Compare                      # 四个导出口各导一次 xlsx，并与 v2.9.2 逐单元格对比
python tools\xlsx_style_report.py <目录|xlsx>    # 把 styles.xml 翻成人话（核对配色）
```

**改动后至少跑**：`build` + `SelfTest` + `smoke_gui`。动到 UI 布局再跑 `snap_gui`，
动到子命令/CLI 再跑 `smoke_cli`。

## 约定（踩过的坑，别再踩）

- **WinForms 停靠顺序是反的**：最后 `Add` 的最先停靠。`Dock=Fill` 的要最先 Add，
  侧栏要最后 Add。写反不报错，只是布局悄悄错位 —— 自检里有几何断言盯着。
- **四个 `Main` 靠 `<StartupObject>` 消歧**，别删各工具的 `Main`（里面有 CLI）。
- **数据分发已不在本工程**：2026-09-02 起在独立仓库 `e:/FctDistributor`。那边有 `Xlsx.cs` / `AppIcon.cs` /
  `StationDetector.cs` 的**副本**（彻底分家的代价），改本工程这三个文件时顺手看一眼那边；
  两个仓库已无自动比对（原跨工程一致性自检随拆分自动降级为跳过），副本同步靠人工纪律。
- **内嵌的 Form 里别用 `FindForm()`**：它在 Form 自身上返回 this，拿顶层窗要用 `TopLevelControl`。
- **要图标就用 `AppIcon.Load()/Apply()`**：别再 `new Icon(BaseDir\\app_icon.ico)`，发布包不一定有那个文件。
- **要导 Excel 就用 `FctShared.Xlsx`**：外壳共享，自己只提供 `styles.xml` 与样式号，别再手写 zip/部件。
  改到导出长相（配色/列宽/冻结/合并）后跑 `.\tools\xlsx_check.ps1 -Compare` —— 它会拿旧版本
  用同一份 fixture 再导一次，逐单元格比「值 + 样式号」。
- **新面板别自己写颜色字号**：取 `Theme` 里的令牌；改完让 `Theme.Apply` 走一遍即可统一。
  主题器**不许改布局**（自检会逐控件比对 `Bounds`）；自绘控件请加进 `Theme.IsSkipped` 或挂 `Tag = Theme.SkipTag`。
- **WinExe 里 CLI 要输出**：`AttachConsole(-1)` 之后必须 `Console.SetOut(...)`。
- **待办来自真实不良**：不可忽略、不可删除，只能确认 → 处理 → 拖到「已完成」。
- **端口别搞混**：headless（`agg --web`）与机台节点监听 **`mesh_port`（8081）**；`agg_http_port`（8080）只是
  v3.5.0 前中心化模式的遗留字段（`[Obsolete]` 的 `AgentPusher`/`HttpIngest`/`AggCenterForm` 才读）。
- **改 Mesh/Web 相关代码**：`MeshNode`（编排）/ `MeshPusher`（出站队列）/ `MeshReceiver`（入站组提交）/
  `WebAggServer`（路由）/ `AggDatabase`（副本库），自检里有 Mesh 推送、对账、Web 鉴权/白名单等 ~30 个分组盯着。
- 提交前确保 `_releases/`、`package_v*/`、`dist/`、`.snap/`、`data/`、`logs/` 不入库。

## 历史

- 前身是 Python 版 v1.0.2（PyQt6 + FastAPI），因杀毒软件误杀于 2026-07 用 C#/.NET 8 重写。
- 老的 Python 取数脚本存档在 `../legacy/fct-fetcher-python/`。
- 历史发布包移出仓库，放在 `../_releases/`。
