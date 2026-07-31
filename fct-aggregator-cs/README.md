# FCT 工具套件（FCT Suite）

车间 FCT 测试线的一站式工具集。**v2.9.1 起五个原独立程序合并为一个 exe，四个工具内嵌为主窗口页面**。

| 模块 | 干什么 | 入口（内嵌页 / 命令行）|
|---|---|---|
| **数据聚合**（主程序） | 监控测试机 XML 落盘 → 解析分类入库 → 统计良率 / 待办维修 / FAIL 告警（飞书 + Windows 桌面提示） | 直接启动 |
| 数据分发 v1.9.0 | XML 分发到目标目录、批量改时间戳、按模板造数、SN 导出 xlsx | Ctrl+6 / `distribute` |
| FAIL 排行 v1.5.0 | 扫 FAIL 记录按不良项排名，导出 xlsx / csv，内置 XML 报告查看 | Ctrl+7 / `rank` |
| 取数打包 v1.0.0 | 按日期区间捞 Results + TDMS，打包成 zip / 汇总 xlsx（带 CLI） | Ctrl+8 / `fetch` |
| TDMS 波形 v1.1.0 | 打开 TDMS 记录看波形、导出 csv/json（带 CLI） | Ctrl+9 / `tdms` |

## 快速开始

```powershell
# 机台部署：解压完整包，改好 config.json，双击 FCT-Aggregator.exe
FCT-Aggregator.exe                    # 主程序（左侧导航切页，工具箱在下方）
FCT-Aggregator.exe --help             # 全部子命令
FCT-Aggregator.exe distribute         # 只开分发工具
FCT-Aggregator.exe tdms x.tdms        # 直接看某个波形文件
FCT-Aggregator.exe fetch --help       # 取数工具命令行
```
快捷键：`Ctrl+1~5` 主程序页、`Ctrl+6~9` 四个工具页、`Ctrl+R` 刷新。
四个工具是**内嵌页面**（`TopLevel=false` 的 Form）；命令行方式则以独立窗口打开。

## 运行环境

- Windows 10/11 + **.NET 8 Desktop Runtime**（框架依赖发布，包体最小）
- 无需数据库服务：本地 SQLite（`data\{机台号}.db`）

## 目录结构

```
fct-aggregator-cs/
├── FctAggregator.csproj      唯一工程（WinExe，StartupObject = FctAggregator.Program）
├── *.cs                      主程序：采集引擎 / 待办看板 / GUI 外壳(Theme+UiWidgets)
├── Xlsx.cs                   FctShared.Xlsx：共享 xlsx 写出器（四处导出共用，样式各自保留）
├── AppIcon.cs                图标唯一取用口（嵌入资源优先，带缓存）
├── assets/icon/              图标源 PNG；python tools/make_icon.py 重建 app_icon.ico
├── modules/                  合入的四个工具，各自保留 namespace
│   ├── Distributor/ (tpl/)   FctDistributor
│   ├── FailRanker/           FctFailRanker
│   ├── Fetcher/              FctFetcher
│   └── TdmsViewer/           FctTdmsViewer
├── selftest/                 主自检（含 GUI 几何断言、待办闭环、工具构造）
├── selftest-distributor/     分发工具原自检
├── tools/                    打包与冒烟脚本（含 xlsx 长相新旧对比 xlsx_check/xlsx_cmp/xlsxdump）
├── 更新日志.md               面向技术（改了什么、为什么、踩了什么坑）
├── 版本更新说明.md           面向使用（怎么点、看什么）
├── 覆盖说明.md               升级/覆盖步骤（随更新包发出）
└── TECHNICAL_DOCUMENTATION.md 架构、数据库、设计约定
```

## 开发

```powershell
dotnet build FctAggregator.csproj -c Release          # 编译（要求 0 警告 0 错误）
dotnet run --project selftest\SelfTest.csproj -c Release   # 全量自检（必须全绿）

dotnet publish FctAggregator.csproj -c Release -o dist
.\tools\make_package.ps1                             # 生成完整包 + 覆盖包
.\tools\smoke_gui.ps1                                # 起窗口、看标题、查日志 ERROR
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
- **五个 `Main` 靠 `<StartupObject>` 消歧**，别删各工具的 `Main`（里面有 CLI）。
- **内嵌的 Form 里别用 `FindForm()`**：它在 Form 自身上返回 this，拿顶层窗要用 `TopLevelControl`。
- **要图标就用 `AppIcon.Load()/Apply()`**：别再 `new Icon(BaseDir\\app_icon.ico)`，发布包不一定有那个文件。
- **要导 Excel 就用 `FctShared.Xlsx`**：外壳共享，自己只提供 `styles.xml` 与样式号，别再手写 zip/部件。
  改到导出长相（配色/列宽/冻结/合并）后跑 `.\tools\xlsx_check.ps1 -Compare` —— 它会拿旧版本
  用同一份 fixture 再导一次，逐单元格比「值 + 样式号」。
- **新面板别自己写颜色字号**：取 `Theme` 里的令牌；改完让 `Theme.Apply` 走一遍即可统一。
  主题器**不许改布局**（自检会逐控件比对 `Bounds`）；自绘控件请加进 `Theme.IsSkipped` 或挂 `Tag = Theme.SkipTag`。
- **WinExe 里 CLI 要输出**：`AttachConsole(-1)` 之后必须 `Console.SetOut(...)`。
- **待办来自真实不良**：不可忽略、不可删除，只能确认 → 处理 → 拖到「已完成」。
- 提交前确保 `_releases/`、`package_v*/`、`dist/`、`.snap/`、`data/`、`logs/` 不入库。

## 历史

- 前身是 Python 版 v1.0.2（PyQt6 + FastAPI），因杀毒软件误杀于 2026-07 用 C#/.NET 8 重写。
- 老的 Python 取数脚本存档在 `../legacy/fct-fetcher-python/`。
- 历史发布包移出仓库，放在 `../_releases/`。
