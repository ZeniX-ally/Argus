# 测试工程师 Dashboard — 功能规划（制造执行与数据监控）

> 版本：v1（2026-08-27）｜视角：测试工程师｜承接：《设备监控面板功能规划.md》（设备状态模块并入本规划）
> 定位：面向测试工程师的一站式工作台，覆盖**日常拉取机台数据 → 查看测试报告 → 追踪程序调整日志 → 监控良率 → 查询机台明细**的完整闭环。

---

## 1. 需求概述

在现有 Argus 聚合看板（FAIL 聚合）基础上，升级为**制造执行与数据监控 Dashboard**，六个功能模块：

| # | 模块 | 一句话目标 |
|---|---|---|
| 1 | 数据拉取 | 按机台/产线/时间段灵活筛取测试数据，批量导出 + 图表可视化 |
| 2 | 报告中心 | 检索历史报告，在线预览 + 关键指标摘要 + 版本对比 + 下载归档 |
| 3 | 程序调整日志 | 记录程序版本变更，时间轴/机台维度回溯 + 参数前后对比 |
| 4 | 良率监控 | 实时/历史良率看板，异常高亮告警，联动报告与日志 |
| 5 | 数据明细 | 全机台测试数据明细表格，排序/筛选/分页/自定义列/一键导出 |
| 6 | 整体交互 | 模块联动下钻、全局搜索、收藏、权限控制、个性化布局 |

---

## 2. 现状能力盘点（复用 vs 缺口）

| 模块 | 现有可复用资产（已代码核实） | 缺口 |
|---|---|---|
| 数据拉取 | `modules/Fetcher`：按日期区间捞 FAIL XML+CSV+TDMS、打包 zip + 3-Sheet 清单 xlsx，GUI+CLI | 无产线/班次/周/月筛选；无图表可视化；无在线联动（现为独立桌面工具，非服务端接口） |
| 报告中心 | `XmlViewerForm`（深色仪表盘报告查看器：状态徽章/KPI/测试项表，失败项标红）；`/api/file?id=` 在线下载 XML | 无按批次/型号检索；无指标摘要聚合；无版本对比；无归档管理 |
| 程序调整日志 | 无（全新） | 需建表、录入界面、时间轴/机台回溯、参数 diff |
| 良率监控 | `Database.FetchGlobalStats/FetchDailyStats`（PASS/FAIL/中断/SN 去重）；看板机台卡片 | 聚合端**只有 FAIL 摘要**（mesh 只推 FAIL），无 PASS 计数 → 良率无底座；无维度统计/趋势/告警 |
| 数据明细 | 看板 `/api/fails`（排序/筛选/分页）+ `/api/export.csv`（UTF-8 BOM + 公式注入防护） | 仅 FAIL、无 PASS；无自定义列；导出粒度粗 |
| 设备状态 | 上轮规划（心跳扩展 + device_info/samples） | 未开发（并入本规划 M2） |
| 交互层 | 单文件内嵌 HTML/JS + 3s 轮询 + 黑白红主题 + agg_token 鉴权 | 无联动下钻/全局搜索/收藏/角色权限/布局定制 |

**结论**：Fetcher 与 FailRanker 的**统计口径与解析逻辑**是现成资产，但当前是桌面工具形态；Dashboard 需要把它们的能力**服务端化**（变 API），并在聚合端补齐**良率数据底座**（最大缺口）。

---

## 3. 关键架构决策

### D1 聚合数据范围 —— 推荐：心跳携带日统计（不推全量 PASS）

- **现状**：mesh 只推 FAIL（`/api/mesh/fail`），`mesh_agg.db` 只有 `agg_records`；PASS 数据只存在各机台本地库
- **需求缺口**：良率监控需要 PASS 计数；报告中心需按型号/批次检索
- **方案 A（推荐）**：机台心跳（30s）携带**当日增量统计** `{date, pass, fail, interrupted}`；聚合端按 `(machine, date)` 累计到 `yld_daily` 表
  - 优势：每心跳 ~200B，30 台 × 30s = 1 req/s 无压力；聚合端单写者模型不变
  - 明细下钻：跨机报告/明细仍走**按需拉取**（`/api/mesh/fetch` + `/api/file?id=` 已有）
- **方案 B**：全量推送所有记录（含 PASS）——数据最全但推送量 ×5~10，与"轻量采集"原则冲突
- **结论**：A。报告中心检索依赖 XML 索引（`agg_records.xml_path` 已有），配合 P1-B 的 XML 缓存落盘即可覆盖"源机离线"场景

### D2 看板前端形态 —— 推荐：静态资源目录拆分（仍单 exe）

- **现状**：看板 HTML/JS 硬编码在 `WebAggServer` 的 const 字符串（已近千行，6 模块后不可维护）
- **方案**：`WebAggServer` 增加**静态资源服务**（`public/` 目录，发布包随带），各模块独立 `.html/.js/.css`；`HttpListener` 按路径服务 + 现有鉴权包裹；仍**零第三方依赖、离线可用、单 exe 部署**
- 收益：模块可独立演进、缓存可控（带 ETag/Last-Modified）；旧单文件入口保留为「总览」首页

### D3 权限模型 —— 推荐：轻量用户表 + 三角色（不引 AD/LDAP）

| 角色 | 能力 |
|---|---|
| viewer（只读） | 看板浏览、图表查看 |
| engineer（工程师，默认） | viewer + 导出/下载、程序日志登记、报告归档 |
| admin | engineer + 设置、用户管理、告警配置、删除/清理 |

- 实现：`users` 表（name + 密码哈希 + role）；登录后发 HttpOnly Cookie 会话；**保留旧 `agg_token` 通道（视为 admin）** 以兼容现有机台推送与旧链接
- 按钮按角色显隐 + 后端接口二次校验（防绕过）

### D4 程序调整日志数据来源 —— 推荐：人工登记为主 + 自动版本采集辅助

- **人工登记**（面板表单）：版本号、变更时间、变更人、变更内容、影响机台范围、关联报告
- **自动辅助**：从 XML 提取 SW/Bootloader 版本（G49 文档 8.2 Version check 口径）+ 相邻版本**关键参数快照对比**（自动 diff 高亮）
- 落库：`proc_change_log` 表；参数快照存 JSON（`params_snapshot`），对比在查询时做

### D5 在线 XML 查看的检索与渲染策略 —— 推荐：源机本地检索 + 服务端渲染等效

**需求**：任意聚合节点，在线查看**任意机台**上**某个产品**（SN/型号）的测试 XML，效果与本地查看器（XmlViewerForm）一致。

**现状核实**：
- 本地查看 = `modules/FailRanker/XmlViewerForm.cs`：`Parse()` 是**纯 C# 数据解析**（XmlReader 流式，DTD Prohibit 防 XXE，提取 BATCH/FACTORY/PANEL/DUT/TEST → `ReportData`），与 UI 完全解耦 → 可直接抽出复用
- 跨机拉取已有：`MeshReceiver.FetchXmlForFail(failId)`（本地白名单 / 跨机 HTTP `/api/file?id=`）
- **缺口 1（检索）**：聚合库 `agg_records` **只有 FAIL**，PASS 报告的 xml_path 不在聚合端 → 无法按 SN 查到 PASS 报告
- **缺口 2（渲染）**：`/api/file` 返回原始 XML 文本，浏览器无 XmlViewerForm 式仪表盘渲染

**方案**：
- **检索（源机本地索引）**：PASS/FAIL 全量索引天然在各机台 `test_records`（有 sn/test_date 索引）。聚合端新增 `POST /api/mesh/query`（带 SN/型号/时间/机台过滤）→ 并发向在线机台转发 → 机台本地 `test_records` 查询返回 `{xml_path, result, test_date, file_size}` 列表 → 聚合端汇总（含离线机台降级提示）；结果缓存 TTL 5min
  - 收益：聚合端**零膨胀**（不复制 PASS 索引），口径与本地库一致
  - 备选：聚合端建全量索引（mesh 推送所有记录索引）——查询更快但聚合库年增 ~GB 级，与轻量原则冲突，列为后续演进
- **渲染（服务端渲染等效）**：抽 `XmlReportParser.ParseText(xml)`（= XmlViewerForm.Parse 逻辑，纯 C#）+ 新增 `XmlReportHtml.Render(data)`（HTML 生成：SN 标题 + 状态徽章 + 4 张 KPI 卡 + 信息网格 + 等宽测试项表格，失败标红、排除项标注，黑白红主题）→ 新端点 `GET /api/xmlview` 返回完整 HTML
  - **同一份 C# 解析逻辑 = 本地与在线口径完全一致**；浏览器零重写
  - 安全：沿用 agg_token 鉴权 + LocalReadValidator 白名单（v3.5.1 已加固）+ DTD Prohibit

**交互（用户视角）**：
```
报告中心 → 输入 SN/型号（可加机台/时间）→ 搜索
  → 并发查在线机台 → 结果列表（机台/时间/结果/大小）
  → 点「预览」→ 新标签页 /api/xmlview → 仪表盘 HTML（等效本地查看器）
  → 「下载」→ 原始 XML；「对比」→ 相邻版本参数 diff（D4 关联）
```

---

## 4. 信息架构（IA）

### 4.1 一级导航（左侧栏 8 项）

```
Dashboard
├─ 1 总览       今日 KPI / 快捷入口 / 实时告警条
├─ 2 数据拉取   筛选 → 图表 → 导出
├─ 3 报告中心   检索 → 预览 → 对比 → 归档
├─ 4 程序日志   时间轴 / 机台维度 / 登记 / 参数对比
├─ 5 良率监控   维度统计 / 趋势 / 排行 / 告警
├─ 6 数据明细   可配置明细表格
├─ 7 设备状态   在线 / 系统信息 / 告警（上轮规划）
└─ 8 设置       用户权限 / 告警规则 / 布局管理
```

### 4.2 每模块内部布局（测试工程师视角的按钮与区块）

#### 5.1 总览（首屏）
```
[今日良率 ▁▁][今日 FAIL ▁▁][在线机台 n/M][待办数 ▁▁]      ← KPI 卡（点击→良率监控/明细）
[收藏的常用筛选：横向快捷卡片（×N）]                       ← 点卡片直达对应模块+预置筛选
[实时告警条：离线机台 / 良率跌破 / 磁盘告警]                ← 点击→对应模块
```

#### 5.2 数据拉取
```
筛选区：机台[多选下拉，按产线分组] | 产线[单选] | 时间[自定义起止 | 班次 | 周 | 月] | 类别[Offline/Online]
操作区：[拉取预览] [导出 Excel] [导出 CSV] [打包 zip] [图表切换]
结果区（页签）：[趋势图] [分布图] [热力图] | 明细预览表（前 200 行）
  趋势图：良率/FAIL 数按日折线（点击某日→下钻当日明细）
  分布图：不良项 TOP-N 柱状（点击某不良项→报告中心检索该失败项）
  热力图：机台 × 日期 FAIL 密度（点击格→当日该机台明细）
```
- 服务端化：复用 Fetcher 核心扫描/打包逻辑 → 新 API `POST /api/fetch/plan`（预算命中的文件数/大小）+ `POST /api/fetch/export`（异步任务 → 轮询进度 → 下载产物），避免阻塞 HttpListener（对齐方案 P2-2 导出异步化）

#### 5.3 报告中心
```
检索区：机台 | 批次(SN) | 型号 | 时间段 | 结果(PASS/FAIL/全部)
列表：报告清单（时间/机台/SN/型号/结果/失败项/文件大小），排序+分页
行操作：[预览] [对比] [下载] [归档]
预览抽屉：服务端渲染等效 HtmlViewer（见 5.3.1），非 iframe 文本
关键指标摘要：通过率 / NG 项分布（TOP-N）/ 测试时长 / 版本
版本对比视图：相邻版本关键参数 before/after 表，差异高亮
归档区：已归档报告（只读，可按时间检索）
```

##### 5.3.1 在线 XML 查看（等效本地，核心首件）——详细设计

| 项 | 设计 |
|---|---|
| 解析 | 抽 `XmlReportParser`（纯 C#，源自 XmlViewerForm.Parse，XmlReader + DTD Prohibit；字段口径与本地完全一致） |
| 渲染 | `XmlReportHtml.Render`：SN 标题栏 + 状态徽章（PASS 墨黑 / FAIL 主红）+ KPI 卡（测试项总数/失败计入不良/排除项/通过项）+ 信息网格（TESTER/操作模式/测试时间/整体状态/文件名/SN）+ 等宽测试项表格（失败标红、排除项标「不计入不良」灰、表头强调色）——黑白红 Web 版，**与本地查看器等效** |
| 检索 | `POST /api/mesh/query`（聚合端→机台，按 SN/型号/时间/机台过滤，机台查本地 test_records 全量含 PASS）+ 结果缓存 5min；离线机台标「不可达」降级 |
| 拉取 | 复用 `FetchXmlForFail`（failId，走 agg_records）与跨机 `/api/file`；PASS 报告走「机台查询 → 源机 /api/file」 |
| 端点 | `GET /api/xmlview?machine=&sn=&time=`（或 `?id=`）→ 完整 HTML 页面（新标签页）；`GET /api/xml`（原始内容，供「查看原始 XML」） |
| 安全 | agg_token 鉴权全路由；LocalReadValidator 白名单（跨机路径校验）；DTD Prohibit 防 XXE |
| 兼容 | 旧版机台无 `/api/mesh/query` → 聚合端跳过并提示「该机台版本过旧」；FAIL 报告仍可用 id 路径查看 |

#### 5.4 程序日志
```
视图切换：[时间轴] [机台维度]
时间轴：版本变更事件流（时间线），点击展开变更详情
机台维度：选择机台 → 该机台经历的所有版本变更列表
登记表单：[版本号] [变更时间] [变更人] [变更内容] [影响机台范围(多选)] [参数快照(自动采集+可编辑)]
详情面板：变更内容 / 影响机台 / 参数 before-after diff 表 / 关联报告数
联动：点某版本 → 该时段良率趋势 + 关联测试报告
```

#### 5.5 良率监控
```
维度切换：[机台 | 产线 | 产品] × [实时 | 日 | 周 | 月]
主图：良率趋势折线 + 目标线（阈值可配，默认 98%）
排行表：按维度 TOP-N（良率最低的机台/产线/产品置顶标红）
异常高亮：低于阈值 → 卡片/数据点主红 + 飞书告警（复用 AggAlertService，防抖沿用）
联动：点某机台数据点 → 该机台明细 / 报告 / 程序日志
```

#### 5.6 数据明细
```
表格：全部机台测试记录（agg_records FAIL + yld_daily 统计合并视图）
列配置：列显隐拖拽（自定义列），保存到账号
操作：排序（点列头）/ 筛选（列级过滤）/ 分页 / [一键导出 CSV / Excel]
```

#### 5.7 设备状态
```
引用《设备监控面板功能规划.md》：总览卡片（在线/CPU/内存/磁盘）+ 详情 + 趋势 + 告警
```

#### 5.8 设置
```
用户管理（admin）：增删用户 / 改角色 / 重置密码
告警规则：良率阈值 / 磁盘阈值 / CPU 阈值 / 离线时长（写 config.json）
布局管理：恢复默认 / 导出导入个人布局
```

---

## 6. 全局交互设计

| 能力 | 设计 |
|---|---|
| **联动下钻** | 统一"机台 + 日期"双上下文：任何图表/卡片点击 → `#/module?machine=FCT3&date=2026-08-27` URL hash 路由；机台维度贯穿全部模块（点机台 → 该机台报告/良率/日志/明细） |
| **全局搜索** | 顶栏搜索框：输入 SN / 机台 / 型号 / 批次 → 下拉建议 → 跳转最匹配模块（SN→报告中心、机台→设备状态+明细、型号→良率监控） |
| **收藏** | 常用筛选/报表存为快捷卡片（总览页展示）；存储按账号（config.json 或 db），多浏览器同步 |
| **权限** | 按钮级显隐（viewer 无导出/登记/归档按钮）+ 后端接口角色校验；登录会话 Cookie + 旧 agg_token 兼容（admin 级） |
| **个性化布局** | 卡片可拖拽排序/显隐，保存到账号；恢复默认一键 |
| **统一规范** | 3s 轮询刷新（总览/良率/设备状态）、黑白红主题（沿用 Theme 令牌）、快捷键 Ctrl+1~8 切模块 |

---

## 7. 数据模型扩展（聚合端 AggDatabase 新增）

```sql
-- D1：良率日统计（心跳累计）
CREATE TABLE IF NOT EXISTS yld_daily (
  machine TEXT NOT NULL, date TEXT NOT NULL,
  pass INTEGER, fail INTEGER, interrupted INTEGER,
  updated_at TEXT,
  PRIMARY KEY (machine, date)
);
-- 最近 N 天良率查询热路径索引
CREATE INDEX IF NOT EXISTS idx_yld_date ON yld_daily(date);

-- D4：程序调整日志
CREATE TABLE IF NOT EXISTS proc_change_log (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  version TEXT NOT NULL, changed_at TEXT NOT NULL,
  changed_by TEXT, content TEXT,
  scope_machines TEXT,          -- JSON 数组：影响机台
  params_snapshot TEXT,         -- JSON：参数快照（自动采集+人工编辑）
  related_reports TEXT,         -- JSON：关联报告 id 列表
  created_at TEXT
);
CREATE INDEX IF NOT EXISTS idx_proc_log_at ON proc_change_log(changed_at);

-- D3：用户表
CREATE TABLE IF NOT EXISTS users (
  name TEXT PRIMARY KEY COLLATE NOCASE,
  pwd_hash TEXT NOT NULL, role TEXT NOT NULL DEFAULT 'viewer',
  layout TEXT,                  -- JSON：个性化布局
  favorites TEXT,               -- JSON：收藏
  created_at TEXT
);

-- 复用：agg_records（现有 FAIL）/ device_info / device_samples（上轮规划）
-- 索引补强（可选）：agg_records(test_date, machine) 服务报告中心按型号+时间检索
```

---

## 8. 实施计划（分期 + 验收 + 版本线）

| 阶段 | 内容 | 预估 | 验收标准 | 版本 |
|---|---|---|---|---|
| **M1 数据底座** | D1 心跳日统计 + `yld_daily` + `/api/stats/*`；D2 静态资源服务改造；D3 users 表 + 登录/角色 | 1 周 | 30 台日统计入库；看板经 public/ 加载；角色登录/越权 403；自检 +N 全绿 | v3.7.0 |
| **M2 良率 + 总览 + 明细** | 良率看板（维度/趋势/排行/阈值告警→飞书）+ 总览页（KPI/收藏/告警条）+ 明细表格增强（自定义列/导出） | 1 周 | 良率与本地库口径一致；阈值触发告警；明细列配置按账号保存 | v3.8.0 |
| **M3.0 在线 XML 查看（报告中心首件，高优）** | 抽 `XmlReportParser`（源自 XmlViewerForm.Parse）+ `XmlReportHtml` 渲染器 + `GET /api/xmlview` + `POST /api/mesh/query`（源机检索）+ 报告中心检索/预览页 | 1 周 | 任意机台任意产品（含 PASS）XML 在线查看与本地查看器**口径一致**（解析等价断言）；跨机检索 30 台 <2s；离线机台降级提示 | v3.8.x |
| **M3 数据拉取 + 报告中心完整** | Fetcher 服务端化（/api/fetch/plan、异步导出）+ 三图表（canvas 自绘）；报告摘要/归档；版本对比 v1（同报告内参数） | 2 周 | 拉取计划与桌面 Fetcher 同口径；1 万行导出不阻塞；报告预览与 XmlViewerForm 一致 | v3.9.0 |
| **M4 程序日志 + 交互层** | `proc_change_log` 登记/时间轴/机台维度/参数 diff；全局搜索/收藏/布局/联动下钻完善 | 2 周 | 登记-回溯闭环；参数 diff 高亮正确；跨模块联动全通 | v4.0.0 |
| **M5 收尾** | 发布链路（make_release 适配 public/）、文档、冒烟脚本、用户培训材料 | 3~5 天 | update 包覆盖升级无配置破坏；smoke 全绿 | v4.0.0 |

**并行建议**：M1 的静态资源改造可与 M2 前端开发并行；M3 的图表组件与 M4 的日志模块可并行。总计约 6~7 周（单人）。

---

## 9. 风险与权衡

| 风险 | 缓解 |
|---|---|
| 良率口径与本地库不一致 | 心跳统计由机台端 `FetchDailyStats` 同源产出（同一查询），聚合端只做累加 |
| 旧版机台（无日统计上报） | yld_daily 缺失 → 良率视图标「无上报」，不阻塞其他模块；升级机台端即可补齐 |
| 静态资源拆分后鉴权遗漏 | 静态路由统一过 `Handle` 鉴权入口（现有机制），selftest 加"未鉴权访问 403"断言 |
| 前端体积与维护 | 模块化目录 + 每模块独立文件；自绘图表保持零依赖；不引框架（架构一致性） |
| 导出大结果集阻塞 | 异步任务 + 轮询进度（对齐方案 P2-2）；限行数分片 |
| 过度设计 | 不引消息队列/时序库/前端框架；权限不引 AD/LDAP（内网轻量即可，除非产线明确要求） |

---

## 10. 待确认清单（开发前拍板）

1. **聚合数据范围**：心跳日统计（推荐）还是全量 PASS 推送？
2. **角色划分**：viewer / engineer / admin 三角色是否够？是否需要"按产线隔离权限"？
3. **良率阈值**：默认 98%？按产品/机台可配？（告警规则进设置页）
4. **程序日志来源**：人工登记为主（推荐）？是否要从 FCT.ini / 配置文件自动 diff 检测变更？
5. **前端形态**：静态资源目录拆分（推荐）还是维持单文件？
6. **报告归档**：逻辑标记（推荐，不动文件）还是物理移动？
7. **上轮《设备监控面板功能规划》的 5 项决策**（系统归属/设备范围/信息维度/采样保留/告警阈值）同步确认，设备状态模块随 M2 一起落地。
8. **在线 XML 查看的 PASS 索引策略（D5）**：源机本地检索（推荐，聚合零膨胀）还是聚合端全量索引（查询快但库膨胀）？
9. **在线预览的访问形态**：新标签页整页（推荐，等效本地查看器）还是看板内嵌抽屉？「查看原始 XML」是否需要独立端点？
