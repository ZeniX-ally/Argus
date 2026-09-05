# Argus — FCT 产线测试数据采集聚合套件

Argus 是一套面向工厂 FCT（Functional Test，功能测试）工位的机台端测试数据采集、聚合与看板系统。单个 WinForms exe 集成采集引擎、Mesh 组网聚合、Web 看板与工具箱，零第三方服务依赖，支持产线内网离线部署与无缝热升级。

## 功能特性

- **测试数据采集**：扫描 FCT 结果 XML / INI，解析 PASS/FAIL 记录、治具（fixture）归属、不良项明细
- **Mesh 组网聚合**：机台间 P2P 互联（token 鉴权），FAIL 记录断线补偿重推，任一机台即可充当聚合节点
- **Web 看板**：内置 HTTP 服务，浏览器访问机台 IP 即可查看全产线良率、Fail 排行、设备健康、维修待办
- **飞书推送**：FAIL 告警 / 离线 / 恢复 / 队列溢出，schema 2.0 交互卡片，多列布局 + 深度信息
- **智能化九域**：良率归因、季节性分解、预测准确率自反馈、设备健康综合分、告警自愈预测、机台端自学习等（全部可配置开关）
- **待办维修闭环**：故障归并（产品信号族字典驱动）、维修记录看板化、优先级权重自学习
- **工具箱**：FCT 取数打包（fetch）、TDMS 波形查看（tdms）、FAIL 排行导出（rank）
- **无缝热升级**：检测更新包自动暂存，分离进程提交，失败自动回滚，不碰现场数据
- **自检体系**：900+ 断言全链路自检，真实库副本隔离运行

## 环境要求

- Windows 10/11 或 Windows Server（WinForms + HTTP 监听）
- .NET 8 SDK（构建）；目标框架 `net8.0-windows`
- 产线机台无需安装任何运行时之外的东西

## 构建

```powershell
git clone <仓库地址>
cd Argus-main
dotnet build -c Release            # 0 警告 0 错误为提交门槛
```

运行自检（900+ 断言，使用临时目录隔离，不碰真实数据）：

```powershell
cd FctAggregator\selftest\bin\Release\net8.0-windows
.\FctAggregator.SelfTest.exe > selftest_run.log 2>&1
Get-Content selftest_run.log -Tail 3      # 期望尾部：==== 全部通过 ====
```

一键发布（构建 + 打包 + 纯净性校验 + SHA256 清单）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make_release.ps1
```

产出三包（均无 `data\`/`logs\`/`*.db`/`*.pdb`，config 为 webhook 置空模板）：

| 包 | 用途 |
|----|------|
| `Argus-v{ver}.zip` | 机台主程序完整包 |
| `Argus-v{ver}-update.zip` | 机台增量更新包（热升级用） |
| `Argus-Agg-v{ver}.zip` | 聚合后端包 |

## 快速开始

### 机台端（工位电脑）

1. 解压 `Argus-v{ver}.zip` 到本机目录
2. 编辑 `config.json`：`station_id`（机台号）、`results_root`（FCT 结果目录）、`fct_ini_path`
3. 双击 `Argus.exe` 启动，左侧工具箱可打开全部子工具

### 聚合端（产线内任一机台兼任）

```powershell
Argus.exe agg --install     # 一键部署：config 生成 / 防火墙放行 / 开机自启 / 启动
```

浏览器访问 `http://<机台IP>:8081/?token=<agg_token>` 查看全产线看板。
邻居机台在 `config.json` 的 `peers` 里互填地址即完成 Mesh 组网。

### CLI 子命令

```
Argus.exe                    主程序 GUI
Argus.exe agg --web|--install|--service   聚合服务
Argus.exe fetch --help       取数打包工具
Argus.exe tdms [文件.tdms]   TDMS 波形查看
Argus.exe rank               FAIL 排行导出
Argus.exe upgrade            图形化升级向导
```

## 配置要点（config.json）

| 键 | 说明 |
|----|------|
| `station_id` | 机台号（Mesh 节点标识） |
| `results_root` | FCT 结果 XML 根目录 |
| `webhook_url` | 飞书群机器人 webhook（留空则无推送） |
| `agg_token` | Mesh 互联鉴权 token（留空自动生成，邻居需一致） |
| `peers` / `mesh_port` | 邻居机台地址列表 / 监听端口 |
| `auto_update` | 无感热升级开关（默认开） |
| `update_dir` | 更新包检测目录（默认 `data/updates`，支持 UNC） |
| `learn_*` | 机台端自学习系列开关（默认关） |

完整键位见 `FctAggregator/config.example.json`。

## 升级

- **自动**：把 `Argus-v{ver}-update.zip` 放进机台 `update_dir`（默认 `data/updates`），程序 5 分钟周期检测后自动暂存 → 托盘提示 → 分离进程重启提交，失败自动回滚
- **手动**：`Argus.exe upgrade` 图形化向导（演练 → 执行 → 回滚），或 `deploy_update.ps1 -Execute`
- 升级不触碰 `data\` / `logs\`，数据库 schema 由内置 `DbMigrator` 自动迁移并向下兼容老库

## 目录结构

```
Argus-main/
├── FctAggregator/          # 主程序（单 exe 工具套件）
│   ├── Core/               # 采集引擎 / 解析 / 分类
│   ├── Agg/                # 聚合 Web 服务 / Mesh 查询
│   ├── Mesh/               # P2P 互联 / 推送 / 同步
│   ├── Intelligence/       # 智能化九域（归因/预测/自学习/字典）
│   ├── Ui/                 # WinForms 界面
│   ├── Db/                 # SQLite 持久层 / 迁移
│   ├── modules/            # fetch / tdms / rank / upgrader 工具
│   ├── selftest/           # 900+ 断言自检工程
│   └── tools/              # 打包 / 部署 / 诊断脚本
├── scripts/                # make_release.ps1 一键发布
├── docs/                   # 设计文档
└── Releases/               # 发布归档（gitignore）
```

## 安全说明

- Mesh 互联与 Web 看板均要求 token；token 比较使用固定时间比较（防时序侧信道）
- 飞书 webhook 建议通过 `config.json` 配置，不要提交到版本库
- 防火墙规则仅放行 RFC1918 内网网段
- 更新包部署前有纯净性强制校验，防止测试数据污染产线

## 许可证

[MIT](LICENSE)

## 致谢与依赖

核心依赖仅 4 个 NuGet 包：`Microsoft.Data.Sqlite`、`SQLitePCLRaw.bundle_e_sqlite3`、`System.IO.Ports`、`TDMSReader`。其余全部为手写实现（OOXML 导出、HTTP 服务、Mesh 协议、飞书卡片等）。
