# 域 4 设备环境智能化 Spec — 异常预测 / FCT.ini 变更 / 巡检自愈（离线）

> 版本：v1.0（2026-08-31）｜聚焦 A/B/D

## 1. 目标
- A 设备异常预测：CPU/磁盘/离线提前预警（基于趋势，非固定阈值）
- B FCT.ini 智能解析与变更检测：版本/设备清单自动比对，变更告警
- D 自动巡检与自愈建议：离线重连、磁盘清理建议

## 2. 设计

**A 异常预测：**
- DevicePredictor：基于 device_samples 近 7 天滑动窗口，CPU 线性趋势 >90% 预警，磁盘按日消耗速率预测 N 天耗尽，离线按心跳间隔抖动预测

**B FCT.ini 智能：**
- FctIniWatcher：FCT.ini 解析后哈希比对，型号/FW/设备增删变更落 fct_change_log，变更即时飞书 note

**D 巡检自愈：**
- DeviceInspector：每日巡检磁盘 <10GB 提示清理 data/archive、COM 离线提示重插，生成建议列表，不自动执行

## 3. 存储与接口
- 新表：fct_change_log, device_predict_log
- API：GET /api/devices/predict, GET /api/fct/changes

## 4. 发布
- 版本 v3.19.0
