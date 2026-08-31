# 智能化 B1 设计 — 智能日志诊断 + 代办智能分类（离线零模型）

> 版本：v1.0（2026-08-31）｜基于 brainstorming 收敛：B1 纯本地规则+统计，车间离线、机台零开销、聚合端集中计算；PDF 能力延后

## 1. 目标与非目标

**目标：**
- T1 机台测试 log 智能分析 → 自动化诊断机台状态（输出 health_score + 故障域标签，不依赖外网/LLM）
- T2 代办面板分类智能化 → 理解测试步骤真实含义，达到更精准的 `group_key` 合并（同义步骤归一）

**非目标：**
- 不引入 Ollama/ONNX/向量服务（B1 约束）
- 不做 PDF 解析（延后）
- 不改变现有 mesh 协议与 DB 单写者铁律

**成功标准：**
- 同义 FAIL（如 `5V Rail` vs `5V Power` vs `5V_DP`）自动归一到同一 todo 卡，人工抽检准确率 >90%
- 机台异常（探针/电源/通讯）能在 5min 内产出标签，准确召回 >80%（以历史 FAIL 标注为基准）

## 2. 现状与约束

- 解析链：`Engine → Processor → ParserRegistry(ParserRuleSet) → XmlParser/DefaultResultParser → TestRecord → Database`（`E:/4.9/FctAggregator/Parsing/*`）
- 分类：`Classifier.ClassifyByPrefix` 仅文件名前缀；`TodoGrouping.KeyOf` 正则剥步骤号 + 丢索引 token 留单位，无语义
- 诊断：`IgnoredFailSteps` 过滤伪不良，无偏离度/基线；告警仅 `AggAlertService` 阈值
- 约束：车间离线（192.168.1.232 不可用）、500ms 敏感可后台异步、工控机 i5/8GB、零第三方铁律

## 3. 架构

```
机台 Argus.exe (轻)
  Engine/FileWatcher → Processor(规则) → test_records
         │ 仅 FAIL 的 NAME/VALUE/LIMIT/UNIT 异步推送
         └─→ 聚合端 WebAggServer (重)
              ├─ FaultDomainDict + DeviationScorer → health_score/label
              ├─ SynonymDict + UnitNormalize → KeyOf 2.0 → todo_items.group_key
              └─ 5min 定时汇总 + 飞书告警复用
```

- 机台端：保持实时 <20ms，仅增加故障域词典标注（内存 <1MB）
- 聚合端：集中计算，`SyncTodoItems` 与 `DeviceAlertLoop` 复用定时器

## 4. 模块设计

### 4.1 智能日志诊断（Log Intelligent Diagnosis）

**输入：** `TestRecord.FailedTests[]` 每项 `NAME/VALUE/HILIM/LOLIM/UNIT` + `TestRecord.StationId/Model`

**组件：**
- `FaultDomainDict`（`FctAggregator/Diagnosis/FaultDomainDict.cs`）：四域词典 `power_rail`（5V/3V3/12V/电流）、`probe_contact`（contact/resistance/open）、`comm`（UART/I2C/SPI/timeout）、`timing`（delay/ms/sequence）。命中规则：`NAME.ToLower` 含关键词 → 域标签。多域命中取首个，置信度 `1/命中数`
- `DeviationScorer`：`dev = |VALUE - clamp(VALUE,HILIM,LOLIM)| / max(|HILIM|,|LOLIM|,1)`，`0-0.2 轻 0.2-0.5 中 >0.5 重`
- `HealthScorer`：`health = 100 - Σ(dev*weight[domain])`，`weight: power 1.2/probe 1.0/comm 0.8/timing 0.9`，按机台 5min 窗口聚合 `avgHealth`，`<60` 标记 `machine_anomaly`
- `DiagnosisStore`：`AggDatabase.Diagnosis.cs` 新增表 `diagnosis_log(id, machine, ts, domain, dev, health, label)`，保留 30 天

**输出：** `diagnosis_log` + 内存 `DeviceHealthCache` 供 `/api/status` 与 `DeviceStatusPanel` 展示

### 4.2 代办智能分类 2.0（Todo Grouping 2.0）

**输入：** `TestRecord.FailReason` 即 `TEST NAME`

**改动：** `TodoGrouping.cs` 升级 `KeyOf 2.0`，保持旧 `group_key` 兼容（同 key 不拆卡）
- `SynonymDict`（`FctAggregator/Diagnosis/SynonymDict.json`）：手工维护 30-50 条同义映射，例 `5v_rail: [5v power, 5v_dp, 5v_rail_voltage]`，加载后 `Normalize` 阶段先替换再走现有 `StripLeadingStepNo/ParenRe/IsIndexToken`
- `UnitNormalize`：`5V`/`5.0V`/`5000mV` 统一转 `5v`（`mV→V`），`100ms`/`0.1s` 统一
- 兼容：旧 `todo_items.group_key` 已存在的不重算，新 FAIL 按 2.0 算；提供一次性脚本 `scripts/migrate_todo_keys.py` 可选重算（需确认）

**输出：** 更精准的 `todo_items.group_key`，`TodoGrouping.TitleOf` 复用

## 5. 数据流与接口

- 机台 `Engine.ProcessRealtime` → `Processor` 产 `TestRecord` → `Database.BatchInsert` → 触发 `DiagnosisAnnotator.Annotate(record)`（内存标注，不落库）→ 异步 `MeshPusher` 推 FAIL 到聚合端（已有）
- 聚合端 `WebAggServer` 启动 `DiagnosisAggregator` 定时 5min：`SELECT FailedTests FROM test_records WHERE ts>now-5min` → `FaultDomainDict+DeviationScorer` → `Upsert diagnosis_log` + 更新 `DeviceHealthCache`
- 聚合端 `Database.SyncTodoItems` 调用 `KeyOf 2.0` 替代旧 `KeyOf`

**API：**
- `GET /api/diagnosis?machine=&hours=24` 返回 `diagnosis_log`
- `GET /api/status` 扩展 `deviceHealth` 字段

## 6. 错误处理与回退

- 词典缺失/解析异常 → 退化为旧 `KeyOf` / 无域标签，不阻塞主流程
- `dev` 计算分母为 0 → 记 0
- 兼容：旧 `group_key` 保留，`SynonymDict.json` 缺失则跳过同义替换

## 7. 测试

- 单元：`FaultDomainDict` 命中率、 `DeviationScorer` 边界、`SynonymDict` 归一（各 10 用例）
- 集成：`SyncTodoItems` 前后 `group_key` 对比，`diagnosis_log` 写入
- 端到端：导入 100 条历史 FAIL（含 5V 同义变体），验证归一率与 health 告警

## 8. 部署与发布

- 零新增依赖，`SynonymDict.json` 随 `public/` 打包，随 `make_release.ps1` 发布
- 版本：`FctAggregator v3.19.0`（Y 新功能），`selftest` +15 项

## 9. 后续（PDF 延后）

- `PdfParser` 另起 spec，复用本设计的 `FaultDomainDict` 词典
