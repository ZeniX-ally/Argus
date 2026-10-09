# 正常态自学习底座设计规格（Normal-State Learning Foundation）

> 日期：2026-09-07
> 状态：已评审（用户确认设计五段 + 本文档）
> 版本目标：v3.30.0（P1~P4 分段交付，可独立发布/回滚）
> 范围：**仅机台侧**（Argus.exe / FctAggregator）；聚合端零改动

---

## 1. 背景与动机

现有 `Intelligence/` 智能层（27 个模块）存在四项核心差距（用户确认，全部成立）：

| 差距 | 现状根因 |
|---|---|
| 自学习名不副实 | `SelfBaseline` 仅学习「型号 × 时段」的良率均值/σ，测量明细、TDMS 特征、设备行为均未建模 |
| 告警质量不行 | 阈值静态写死 config，与"这台机器平时什么样"无关，误报/漏报依赖人工调参 |
| 分析不闭环 | spec06 的归因/漂移/cpk/TDMS 产出停留在数据与图表，未回流为判断证据 |
| 预测能力太弱 | `AlertPredictor` 线性趋势过浅，缺乏正常态参照系，无法度量"劣化了多少" |

**终态愿景**（用户确认）：完整智能闭环——自学习基线 → 智能判断 → 根因处置 → 预测预防。
**首个里程碑**：自学习底座（后续判断/预测/根因层的共同参照系）。

## 2. 需求画像（已确认决策）

| 维度 | 决策 |
|---|---|
| 终态目标 | 完整智能闭环（四层） |
| 部署边界 | 机台侧为主；聚合端不参与本设计（现网聚合端未实际使用） |
| 技术栈 | 纯 C# 轻量算法，零第三方依赖（延续现有约束） |
| 数据源 | `test_measurements`（XML 测量明细，含 lolim/hilim）+ `tdms_features`（spec06 已提取特征）+ `device_samples_local`（设备采样）+ 过程/状态数据 |
| 验收 | 历史回放验证 + 模型覆盖度完整 + 上线实战验证 + 自学习状态 UI 面板 |
| 告警链路 | 飞书推送由机台直推（已确认），本设计不改推送链路 |

## 3. 硬约束（必须遵守）

1. **只学正常态，不学判断阈值**：正常态模型仅提供偏离证据；判断依据仍以产品规格（lolim/hilim）为唯一来源。
2. **只学 PASS 数据**：FAIL 记录的测量值是异常证据，不得混入正常态模型。
3. **零第三方依赖**：Welford 在线统计 + P² 流式分位数，纯 C# 手写。
4. **不阻塞主链路**：学习环节吞异常只计数，绝不阻塞测试数据入库（沿用 `TdmsFeatureCollector` 原则）。
5. **默认关闭**：config 开关缺省关闭，关闭时行为与现状 100% 一致，热升级零影响。
6. **架构兼容**：不改现有公开 API、不重写现有 Intelligence 类职责，新能力以新增模块接入。
7. **数据/日志安全**：DB 迁移走机台 `CREATE TABLE IF NOT EXISTS` 惯例，不触碰 data/logs。

## 4. 架构总览

```
┌─ 摄取链路（现有，不动）────────────────────────────────┐
│ Engine.ProcessRealtime → 解析 XML → test_records        │
│                        → test_measurements 落表          │
│                        → TdmsFeatureCollector → tdms_features 落表 │
│ DeviceSampleRecorder → device_samples_local 落表         │
└──────────────┬───────────────────────────────────────┘
               │ 落表后同步钩子（O(1) 增量更新，吞异常）
┌─ 正常态学习层（新增）──────────────────────────────────┐
│ MeasurementLearner   ← test_measurements（仅 PASS）     │
│ WaveformLearner      ← tdms_features（仅 PASS）          │
│ DeviceBehaviorLearner← device_samples_local（按时段）    │
│          ↓ 写入                                          │
│ normal_models 表（source, model, signal_key 三元组一行）  │
└──────────────┬───────────────────────────────────────┘
┌─ 偏离评分层（新增）────────────────────────────────────┐
│ DeviationScorer：单信号偏离分（现算现用）                 │
│ 多信号联合投票 → deviation_events 落库（低频）            │
└──────────────┬───────────────────────────────────────┘
   ┌───────────┼───────────────┐
   ▼           ▼               ▼
FailAttributor  DeviceHealth   WinForms
(归因证据)      Scorer(输入)    「自学习」Tab(UI)
```

## 5. 数据模型

### 5.1 新表 `normal_models`（机台 Database，CREATE IF NOT EXISTS 惯例）

```sql
CREATE TABLE IF NOT EXISTS normal_models (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source     TEXT NOT NULL,   -- 'measurement' | 'waveform' | 'device'
    model      TEXT NOT NULL,   -- 产品型号；device 类固定 ''
    signal_key TEXT NOT NULL,   -- measurement=test_name; waveform=group_name+'/'+channel_name; device=指标名+'@'+slot
    n          INTEGER NOT NULL,        -- 已学样本数
    mean REAL, sigma REAL,              -- Welford 在线均值/标准差
    p01 REAL, p50 REAL, p99 REAL,       -- P² 流式分位数
    min_v REAL, max_v REAL,
    last_ts  TEXT,                      -- 最近更新时间 ISO8601
    status   TEXT NOT NULL,             -- learning | ready | stale
    UNIQUE(source, model, signal_key)
);
CREATE INDEX IF NOT EXISTS idx_nm_status ON normal_models(status);
```

### 5.2 新表 `deviation_events`（偏离事件，低频写入）

```sql
CREATE TABLE IF NOT EXISTS deviation_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    model TEXT NOT NULL,
    source TEXT NOT NULL,
    signal_count INTEGER NOT NULL,      -- 参与投票的信号数
    top_signal TEXT NOT NULL,           -- 偏离分最高的信号
    top_score REAL NOT NULL,
    detail_json TEXT NOT NULL,          -- 全部参与信号 {signal_key, score, value, mean, sigma}
    seen INTEGER NOT NULL DEFAULT 0     -- UI 已读标记
);
CREATE INDEX IF NOT EXISTS idx_de_ts ON deviation_events(ts);
```

### 5.3 状态机

```
新建 → learning ──(n ≥ min_samples)──→ ready ──(超 stale_days 未更新)──→ stale
                                      ↑                                  │
                                      └────────(重新收到更新)────────────┘
```

- `learning`：只积累，不参与偏离评分（防小样本误报）
- `ready`：正常参与评分
- `stale`：暂停评分，收到新数据即恢复（同时重新计数连续性）

### 5.4 容量与换型/换程序

- 单机典型数千行（每信号一行），SQLite 无压力
- 换型/换程序产生新 `test_name`/新 `model` → 天然生成新模型条目，旧条目自然 stale，**无需显式清理**
- 同名参数语义变更场景：UI 提供单模型"重置"操作（清零重新学习）

## 6. 学习器设计

### 6.1 MeasurementLearner（测量学习器）

- **挂接点**：`test_measurements` 落表后同步钩子（`Engine.ProcessRealtime` 内）
- **输入**：该记录的全部数值型测量行（`value` 非 NULL）；仅 `Result=PASS`
- **模型键**：`(measurement, model, test_name)`
- **更新**：Welford 均值/σ + P² 分位数，O(1)/行

### 6.2 WaveformLearner（波形学习器）

- **挂接点**：`InsertTdmsFeaturesFor` 落表后钩子（复用 spec06 phase-3 产出，**不重新解析 TDMS 文件**）
- **输入**：该记录的 `tdms_features` 行；仅 PASS
- **模型键**：`(waveform, model, group_name/channel_name)`，每通道取稳定统计特征 vmean/vstd/vmax 分别建子键（如 `CH1/vmax`；vmin/vfirst/vlast 抗噪性差，不建模）

### 6.3 DeviceBehaviorLearner（设备行为学习器）

- **挂接点**：`device_samples_local` 采样落表后钩子
- **输入**：cpu/mem/disk 等指标值
- **模型键**：`(device, '', 指标名@slot)`，slot 复用现有 `TimeSlot.SlotOfHour` 4 时段（夜/早/午/晚），避免昼夜负载差异污染基线

### 6.4 历史回填（Backfill）

- 一次性/手动触发：按时间序读取历史 `test_measurements` / `tdms_features` / `device_samples_local` 行喂给学习器（仅 PASS）
- 节流沿用现有 backfill 策略（200 文件睡 100ms 等效限速）
- UI 提供按钮 + 进度显示；CLI：`argus learn --backfill`

### 6.5 心跳与可观测

- `app_meta` 写入每学习器心跳：`learn_normal_heartbeat` = `{learner, last_run, today_updates, error_count}`
- 学习器异常只计数 + `Logger.Warning`，不抛出

## 7. 偏离评分（DeviationScorer）

### 7.1 单信号偏离分

```
z = (x - mean) / max(sigma, sigma_floor)
score = min(100, round(z * 100 / 6))        -- z=3 → 50, z=6 → 100
```

- 仅对 `status=ready` 的模型计算；其余一律返回"不可评"
- `sigma_floor` 为常量下限（防 σ≈0 放大微小波动）

### 7.2 四道防误报闸门

1. **ready 门**：非 ready 模型不评分
2. **σ 下限门**：`sigma_floor` 兜底
3. **多信号投票门**：单信号偏离只现算现展示（不落库不告警）；同一评估窗口内 ≥ `vote_signals`（默认 3）个信号 `score ≥ event_score`（默认 75）才形成 `deviation_events` 落库。评估窗口按 source 隔离：measurement/waveform = 单条 PASS 记录内的全部模型条目；device = 单条采样内的全部指标条目。事件不跨 source 合并。
4. **风暴门**：单机每日事件落库不超过 `learn_normal_max_events_per_day`（默认 10），超出只计数不落库

### 7.3 评分消费者（P4 接入）

| 消费者 | 用法 |
|---|---|
| `FailAttributor` | FAIL 归因时附加"该失败信号对本机的偏离分"，修正归因排序 |
| `DeviceHealthScorer` | 波形/设备偏离分作为健康分动态输入 |
| WinForms 自学习 Tab | 偏离事件列表展示 |

### 7.4 明确不做

- 本阶段不新增飞书告警类型（偏离事件仅进 UI 与归因证据；后续按准确率表现再评估推送开关）
- 不替代规格判断（lolim/hilim 判断逻辑原样保留）
- 不引入自动阈值调参（那是规格 02 自反馈的职责，后续闭环对接）

## 8. UI 面板（机台 WinForms「自学习」Tab）

与 `AnalysisPanel` 同级新增 Tab，遵循现有浅色主题规范。四个区域：

1. **学习器运行状态**：三学习器心跳、当日摄取量、错误计数（读 `app_meta`）
2. **模型覆盖率**：按 source 统计 total/ready/learning/stale 进度条；含**回填按钮**（带进度、限速）；可对单模型重置
3. **模型明细**（可搜索/筛选）：signal_key、n、mean±σ、P01/P50/P99、状态、最后更新；支持按 source/model/status 过滤
4. **偏离事件**：最近 `deviation_events` 列表（时间、信号数、Top 信号/分数、详情展开）

数据全部本机直查，零网络依赖。

## 9. 验证方案

### 9.1 selftest 确定性断言（新增分组 `[正常态]`，并入 961+ 体系）

- Welford/P² 与离线全量计算的收敛对拍
- 合成数据：注入阶跃/漂移异常 → 断言检出；正常序列 → 断言沉默
- 状态机迁移全路径（learning→ready→stale→ready）
- FAIL 数据不污染模型（喂 FAIL 记录后模型不变）
- 学习器异常注入 → 主链路不受阻
- 面板数据与表数据对拍

### 9.2 历史回放（ReplayRunner，`argus learn --replay`）

```
4 周历史数据：前 3 周按时间序喂学习器（等效真实摄取）→ 后 1 周逐条评分
对照已知异常时段（良率下跌日、maintenance_records 时间戳）判卷
```

| 指标 | 定义 | 目标 |
|---|---|---|
| 异常检出率 | 已知异常时段内触发偏离事件的比例 | ≥ 80% |
| 误报率 | 正常时段偏离事件数/天 | ≤ 0.2 条/天 |
| 模型覆盖率 | 回放结束 ready 模型占比 | ≥ 90% |

指标不达标 → 调参（min_samples / sigma_floor / vote_signals / event_score）→ 重考。**不达标不上线**。

## 10. 配置项（config.json，全部缺省关闭/保守）

```jsonc
"learn_normal_enabled": false,        // 总开关（默认关）
"learn_normal_min_samples": 30,       // ready 门槛
"learn_normal_stale_days": 14,        // stale 判定天数
"learn_normal_vote_signals": 3,       // 多信号投票门限
"learn_normal_event_score": 75,       // 偏离事件分数门限
"learn_normal_max_events_per_day": 10 // 单机每日事件落库上限（防风暴）
```

## 11. 分段落地计划

| 阶段 | 内容 | 验收 |
|---|---|---|
| P1 模型底座 | normal_models 迁移、三学习器接入、心跳、config 开关（默认关） | `[正常态]` selftest 全绿 |
| P2 回填+评分 | backfill 工具、DeviationScorer、deviation_events、投票门限 | ReplayRunner 三指标达标 |
| P3 UI 面板 | WinForms 自学习 Tab 四区域 | 面板↔表对拍断言 |
| P4 链路接入 | FailAttributor 证据接入 + DeviceHealthScorer 动态输入 | 现有链路 selftest 回归 + 归因对比 |

上线策略：P1~P2 发布后 1~2 台机台试点开启 → ReplayRunner 达标 → 逐台推开。任一阶段问题 → 关开关即回到现状。

## 12. 后续闭环接口（本设计预留，不在本期范围）

- 预测层：偏离分时间序列的斜率 = 劣化趋势度量（AlertPredictor 升级输入）
- 根因层：多维偏离证据 + G49ProductDictionary 信号族 → 处置建议（TodoSuggester 升级）
- 规格对接：偏离事件与规格判定结果的对账回流（对接规格 02 自反馈）
