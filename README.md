# Argus — FCT 机台测试数据采集与分析工具（纯单机版）

Argus 是一套面向工厂 FCT（Functional Test，功能测试）工位的机台端测试数据采集与分析工具。单个 WinForms exe 集成采集引擎、FAIL/维修待办闭环、正常态自学习、工具箱，零第三方服务依赖。

> **v3.31.0 起为纯单机工具**：已删除"聚合/互联/Web 看板"三块能力（Agg / Mesh / public / 聚合库），不再有 P2P 组网、HTTP 服务与浏览器看板；本机采集、FAIL 列表、维修/待办登记、正常态自学习等本机能力完整保留（**分析页已于 v3.50.0 移除**）。

## 功能特性

- **测试数据采集**：扫描 FCT 结果 XML / INI，解析 PASS/FAIL/INTERRUPTED 记录、治具（fixture）归属、不良项明细、测量值（PASS 也采）
- **FAIL 列表与维修待办闭环**：FAIL 双视图分组、待办登记（只扫近一个月、同类合并、永久保留）、维修看板 4 状态机、人员名单/改名同步、12 列导出（CSV 注入防护）、**FAIL 页测项级明细（项目/值/限值）与日/周/月/季度 CSV 导出**
- **飞书推送**（共 10 张卡，schema 2.0 交互卡片，可选 `webhook_url` 配置）：FAIL 告警（批量不良自动合并成一张，不刷屏）/ 待办状态变更 / 采集异常与恢复（漏采主动告知）/ 每日运行摘要 / 章节群挂 / 正常态偏离 / 存储健康 / 待办超期
- **测量值与失败项入库**（PASS 也采）：供 FAIL 测项级明细与正常态自学习使用；**分析页已于 v3.50.0 移除**——趋势图 / 漂移 / CPK / 时段归因 / tester 效应量 / TDMS 快照不再运行，`analyze_*` 键保留（采集与保留期仍生效）
- **机台端自学习**（规格 05 + 正常态 P1）：G49 产品字典驱动故障归并、自基线良率预警、Welford/P² 正常态建模（`learn_normal_enabled` 默认开）
- **工具箱**：FCT 取数打包（fetch）、TDMS 波形查看（tdms）、FAIL 排行导出（rank）
- **无缝热升级**：检测更新包自动暂存，分离进程提交，失败自动回滚，不碰现场数据
- **自检体系**：1273 项断言全链路自检，临时目录隔离运行（不碰真实库）

## 环境要求

- Windows 10/11 或 Windows Server（WinForms）
- .NET 8 SDK（构建）；目标框架 `net8.0-windows`
- 产线机台无需安装任何运行时之外的东西

## 构建

```powershell
git clone <仓库地址>
cd Argus-main
dotnet build Argus.sln -c Release   # 0 警告 0 错误为提交门槛
```

运行自检（1273 项断言，使用临时目录隔离，不碰真实数据）：

```powershell
cd FctAggregator\selftest\bin\Release\net8.0-windows
.\FctAggregator.SelfTest.exe > selftest_run.log 2>&1
Get-Content selftest_run.log -Tail 3      # 期望尾部：==== 全部通过 ====
```

一键发布（构建 + 打包 + 纯净性校验 + SHA256 清单）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make_release.ps1
```

产出两包（均无 `data\`/`logs\`/`*.db`/`*.pdb`，config 为 webhook 置空模板）：

| 包 | 用途 |
|----|------|
| `Argus-v{ver}.zip` | 机台主程序完整包 |
| `Argus-v{ver}-update.zip` | 机台增量更新包（热升级用） |

## 快速开始

1. 解压 `Argus-v{ver}.zip` 到本机目录
2. 编辑 `config.json`：`results_root`（FCT 结果目录）、`fct_ini_path`、可选 `station_id`/`webhook_url`
3. 双击 `Argus.exe` 启动：程序自动扫描入库，主页看当日/当月良率与 FAIL 流水；左侧导航切「维修/待办/工具箱」页

### CLI 子命令

```
Argus.exe                    主程序 GUI
Argus.exe fetch --help       取数打包工具
Argus.exe tdms [文件.tdms]   TDMS 波形查看
Argus.exe rank               FAIL 排行导出
Argus.exe upgrade            图形化升级向导
Argus.exe backfill [--days N]  历史 PASS 测量值/失败明细/TDMS 回填（规格06）
```

## 配置要点（config.json）

| 键 | 说明 |
|----|------|
| `station_id` | 机台号（本机标识，可不配自动识别） |
| `results_root` | FCT 结果 XML 根目录 |
| `fct_ini_path` | FCT.ini 路径（自动识别兜底） |
| `webhook_url` | 飞书群机器人 webhook（留空则无推送） |
| `auto_update` | 无感热升级开关（默认开） |
| `todo_scan_days` | 待办扫描窗口（默认 30 天） |
| `analyze_*` | 测量值/失败项采集与保留期参数（分析页已于 v3.50.0 移除；漂移/CPK/归因/TDMS 相关键保留但不再运行） |
| `learn_*` | 机台端自学习：`learn_normal_enabled` 默认开，规格 05 其它键默认关 |
| `feishu_*` | 飞书卡片：FAIL 合并窗口、采集异常阈值与节流、每日摘要、群挂、偏离分下限、存储健康分、待办超期天数（详见 `config.example.json`） |

完整键位见 `FctAggregator/config.example.json`。

## 升级

- **自动**：把 `Argus-v{ver}-update.zip` 放进机台 `data/updates`，程序 5 分钟周期检测后自动暂存 → 托盘提示 → 分离进程重启提交，失败自动回滚
- **手动**：`Argus.exe upgrade` 图形化向导，或 `deploy_update.ps1 -Execute`
- 升级不触碰 `data\` / `logs\`，老机台库由程序启动时幂等补表，向下兼容

## 目录结构

```
Argus-main/
├── FctAggregator/          # 主程序（纯单机，单 exe 工具）
│   ├── Core/               # 采集引擎 / 解析 / 分类 / FCT.ini
│   ├── Db/                 # Database（本机库，幂等 Init）+ DbMaintenance
│   ├── Intelligence/       # 正常态自学习（规格05）；漂移/CPK/归因/TDMS 类保留但生产链路无调用（v3.50.0）
│   ├── Ui/                 # WinForms 界面（MainForm/各 Panel）
│   ├── Infra/              # Config/Logger/TimeUtil/Xlsx/CsvUtil/FeishuNotifier
│   ├── Parsing/            # 插件式 XML 解析器
│   ├── modules/            # fetch / tdms / rank 工具箱
│   ├── selftest/           # 1273 项断言自检工程
│   └── tools/              # 打包 / 冒烟 / 诊断脚本
├── scripts/                # make_release.ps1 一键发布 / make_github_export.ps1 GitHub 白名单导出
├── docs/                   # 设计文档（含历史 mesh/聚合协议文档备查）
└── Releases/               # 发布归档（gitignore）
```

## 安全说明

- 飞书 webhook 通过 `config.json` 配置，不要提交到版本库
- 更新包部署前有纯净性强制校验，防止测试数据污染产线

## 许可证

[MIT](LICENSE)

## 致谢与依赖

核心依赖仅 4 个 NuGet 包：`Microsoft.Data.Sqlite`、`SQLitePCLRaw.bundle_e_sqlite3`、`System.IO.Ports`、`TDMSReader`。其余全部为手写实现（OOXML 导出、自绘 UI、分析算法、飞书卡片等）。
