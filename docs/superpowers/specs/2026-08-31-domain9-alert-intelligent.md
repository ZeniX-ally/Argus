# 域 9 告警通知智能化 Spec — 预测/自愈联动（离线）

> 版本：v1.0（2026-08-31）｜聚焦 C/D

## 1. 目标
- C 自愈联动：告警后自动巡检与处置建议
- D 告警预测：基于趋势提前预警

## 2. 设计

**C 自愈联动：**
- AlertHealer：告警触发后自动调用 DeviceInspector 巡检，生成建议（清理磁盘/检查 COM）并附于飞书卡片备注

**D 告警预测：**
- AlertPredictor：基于 yld_daily 良率下滑趋势 + device_samples 异常趋势，提前 1 小时预测告警，落 alert_predict_log

## 3. 存储与接口
- 新表：alert_predict_log
- API：GET /api/alerts/predict

## 4. 发布
- 版本 v3.19.0
