# 域 3 配置智能化 Spec — 校验/推荐/审计/热生效（离线）

> 版本：v1.0（2026-08-31）｜4 项全做

## 1. 目标
- A 自动校验与修复：peers/路径/阈值非法自动提示与修正建议
- B 智能推荐：基于历史与机台自动推荐阈值（device_alert/yield_alert）
- C 变更审计与回滚：改前快照、一键回退、审计落库
- D 热生效与健康自检：保存即生效 + 自检反馈

## 2. 设计

**A 校验：**
- ConfigValidator：peers URL 格式、results_root/fct_ini_path 存在性、阈值范围（磁盘 1-100GB/CPU 0-100）校验，错误置 config_error

**B 推荐：**
- ConfigAdvisor：基于近 30 天 yld_daily 良率分布推荐 yield_alert 阈值；基于 device_samples 推荐 device_alert 磁盘/CPU 阈值，仅建议不自动改

**C 审计回滚：**
- 保存前快照 config_backup_yyyyMMdd_HHmmss.json，audit_log 记录变更 diff，支持一键回滚

**D 热生效：**
- Save 后触发 SettingsChanged 热重建 Mesh/Watcher/Alert，3s 内自检并提示结果

## 3. 接口
- GET /api/config/validate, GET /api/config/recommend, POST /api/config/rollback

## 4. 发布
- 版本 v3.19.0
