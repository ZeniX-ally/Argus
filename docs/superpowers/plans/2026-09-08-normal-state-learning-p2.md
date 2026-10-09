# 正常态自学习 P2（回填+评分）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 落地正常态 P2：`deviation_events` 表 + `DeviationScorer`（单信号分 + 四道闸门）+ 历史回填 + `ReplayRunner` 合成回放三指标达标 + CLI `Argus.exe learn --backfill|--replay`。

**Architecture:** 纯函数评分器读 `normal_models`（P1 已有，stale 惰性判定），投票通过才低频写入 `deviation_events`。回填按时间序把库内 PASS 测量/TDMS/设备采样喂给现有三学习器。回放在 selftest 内合成 4 周数据，不依赖现场库。

**Tech Stack:** 现有 .NET 8 / SQLite / 零新依赖。

**Spec:** `docs/superpowers/specs/2026-09-07-normal-state-learning-design.md`（§5.2 事件表、§6.4 回填、§7 评分、§9.2 ReplayRunner、§10 配置；P3 UI / P4 归因不在本计划）

## Global Constraints

- 零第三方依赖
- `learn_normal_enabled=false` 时评分/回填/回放全部 no-op
- 只对 PASS 窗口评分（device 采样无 PASS 概念，按单条采样窗口）
- 偏离分用 `|z|`（规格写 `(x-mean)` 未写绝对值；负偏离必须对称，否则分数为负无意义）
- `sigma_floor = 1e-6` 常量，不进 config
- 吞异常只计数，不阻塞采集
- selftest 基线 568，只增不减；功能 minor → v3.32.0
- 不新增飞书告警；不学 lolim/hilim；DeviceHealthScorer 已删，不接

---

### Task 1: DeviationScorer 纯函数 + deviation_events 表

**Files:**
- Create: `FctAggregator/Intelligence/DeviationScorer.cs`
- Modify: `FctAggregator/Db/Database.cs`（Init 建表 + Insert/Count/List）
- Modify: `FctAggregator/Infra/Config.cs` + `config.example.json`（vote/event_score/max_events 三键）
- Test: `FctAggregator/selftest/SelfTest.cs` `RunNormalStateTests`

**Interfaces:**
- Produces: `DeviationScorer.SigmaFloor`；`ScoreOne(row,x,now,staleDays) → double?`；`EvaluateWindow(db,cfg,source,model,ts,samples,now) → DeviationEvent?`；`Database.InsertDeviationEvent` / `CountDeviationEventsOnDay` / `ListDeviationEvents`

- [ ] **Step 1: 写失败测试**（公式 z=3→50、非 ready 不可评、stale 不可评、投票门、风暴门、开关关 no-op）
- [ ] **Step 2: 跑 selftest 确认编译失败或断言失败**
- [ ] **Step 3: 最小实现**
- [ ] **Step 4: 断言通过**

### Task 2: LearnBackfill + ReplayRunner + CLI

**Files:**
- Create: `FctAggregator/Intelligence/LearnBackfill.cs`
- Create: `FctAggregator/Intelligence/ReplayRunner.cs`
- Create: `FctAggregator/Intelligence/LearnCli.cs`
- Modify: `FctAggregator/Program.cs`（`learn` 子命令）
- Modify: `FctAggregator/Db/Database.cs`（按 record 拉 PASS 测量/TDMS、设备采样区间）
- Test: `RunNormalStateTests` 合成 4 周回放三指标

**Interfaces:**
- Consumes: Task1 scorer + P1 learners
- Produces: `LearnBackfill.Run(db,cfg,days,ct)`；`ReplayRunner.Run(db,cfg,trainEnd,scoreFrom,scoreTo,anomalyDates) → ReplayReport`；CLI `learn --backfill [--days N]` / `learn --replay`

- [ ] **Step 1: 写失败测试**（回填幂等、FAIL 不入模、Replay 检出≥80% / 误报≤0.2/天 / 覆盖≥90%）
- [ ] **Step 2: 跑红**
- [ ] **Step 3: 实现回填/回放/CLI**
- [ ] **Step 4: 绿**

### Task 3: 实时钩子 + 发版材料

**Files:**
- Modify: `FctAggregator/Core/Engine.cs`（PASS 入库后 Evaluate，先于或后于 Observe 均可；先 Evaluate 再 Observe 以免当前点污染）
- Modify: `TdmsFeatureCollector.cs` / `DeviceSampleRecorder.cs`
- Modify: csproj 3.32.0、更新日志、CODEBUDDY §0
- Test: 钩子开关关不落事件；异常吞并

- [ ] **Step 1: 测试钩子 no-op / 吞异常**
- [ ] **Step 2: 接线**
- [ ] **Step 3: sln Release 0 警告 + selftest 全绿**
