# 域 7 维修/待办智能化 Spec — 分类/优先级/建单/流转（离线）

> 版本：v1.0（2026-08-31）｜4 项全做，与 B1 衔接

## 1. 目标
- A 语义分类深化：在 B1 SynonymDict 基础上，增加上下文（机台/型号）维度分类
- B 优先级智能评估：基于频次、影响范围、持续时间自动定级
- C 自动建单与去重：FAIL 自动生成待办，同 key 合并
- D 流转预测与建议：基于历史状态机推荐下一步

## 2. 设计

**A 分类深化：**
- KeyOf 2.0 已有同义+单位归一，本域增加机台/型号前缀权重，同机台同 FAIL 优先同卡

**B 优先级：**
- PriorityScorer：fail_count(>20高/5中) + 涉及机台数 + 持续天数 加权，产 priority 高/中/低

**C 建单去重：**
- AutoTodoCreator：SyncTodoItems 时自动建单，group_key 去重，variants 追加

**D 流转：**
- FlowAdvisor：基于 maintenance_records 历史，open→in_progress→resolved 路径概率推荐下一步，UI 提示

## 3. 存储与接口
- 复用 todo_items, maintenance_records，无新增表
- API：GET /api/todos/suggest, GET /api/maintenance/advise

## 4. 发布
- 版本 v3.19.0
