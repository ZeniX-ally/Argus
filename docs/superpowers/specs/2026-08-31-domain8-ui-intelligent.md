# 域 8 主界面与 UI 智能化 Spec — 布局/预警/交互（离线）

> 版本：v1.0（2026-08-31）｜聚焦 A/C/D

## 1. 目标
- A 布局自适应：按角色/频次自动调整
- C 视觉预警智能高亮：异常自动置顶标红
- D 交互辅助：搜索联想、一键跳转

## 2. 设计

**A 布局：**
- LayoutAdvisor：记录页面访问频次，常用页签前置，viewer/engineer 差异化默认布局

**C 预警：**
- HighlightEngine：基于 diagnosis_log/predict_log，异常机台卡置顶 + 红边 + 脉冲动画

**D 交互：**
- SearchAssist：全局搜索联想（基于 FailReason/型号/SN 前缀），一键跳转 #/fails/# /yield

## 3. 存储与接口
- 本地 localStorage 记录频次，无新增表
- 前端 loader.js 扩展

## 4. 发布
- 版本 v3.19.0
