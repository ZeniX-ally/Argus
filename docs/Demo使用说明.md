# Argus 测试工程师工作台 — MVP 第一版使用说明

> 版本：v1（2026-08-27）｜定位：核心 MVP——解决测试工程师**最基础、最直接**的日常需求
> 覆盖：总览（机台/FAIL 汇总）、FAIL 明细（搜索/导出/在线查看报告）、设备状态、机台筛选

---

## 1. 这是什么

在现有 Argus 聚合服务（`Argus.exe agg --web`）基础上新增的线上 Dashboard。打开浏览器即可查看全部机台或指定机台的测试数据，点击 FAIL 行可**在线查看 XML 测试报告**（渲染效果与本地查看器一致），并支持 CSV 导出。

## 2. 运行方式

### 2.1 构建（0 警告 0 错误要求）

```powershell
cd FctAggregator
dotnet build FctAggregator.csproj -c Release
```

### 2.2 自检（回归保障，当前 647 项断言）

```powershell
dotnet run --project selftest\SelfTest.csproj -c Release
# 末尾应输出：==== 全部通过 ====
```

### 2.3 启动聚合服务

```powershell
cd FctAggregator\bin\Release\net8.0-windows
.\Argus.exe agg --web          # 无窗体常驻（或 agg --install 一键部署）
```

### 2.4 访问

```
浏览器打开 http://<本机IP>:8081/public/demo/
本机验证： http://127.0.0.1:8081/public/demo/
```

> 若 `config.json` 配了 `agg_token`，首次访问用 `?token=xxx` 打开即可（HttpOnly Cookie 自动记住），后续翻页/API 无需再带。

## 3. 页面功能

| 页面 | 功能 |
|---|---|
| **总览** | 今日/累计 KPI（在线/离线机台、FAIL 总数、最近 FAIL）+ 机台状态卡（点击机台卡 = 只看该机台）+ 各机台 FAIL 柱状图 |
| **FAIL 明细** | 全部 FAIL 记录表格，空格分词即时搜索（失败项/SN/型号）；每行「报告」（在线查看）与「XML」（下载原始文件）；「导出 CSV」（UTF-8 BOM + 公式注入防护） |
| **设备状态** | 机台在线状态列表（系统信息依赖 P6 设备监控上报，当前为占位） |
| **良率** | FAIL 分布预览（正式良率口径 PASS/(PASS+FAIL) 依赖 P1 yld_daily，后续替换） |
| **设置** | 服务健康 / 运行时长 / 已接收请求 |

## 4. 机台筛选（核心交互）

- 顶栏右侧**机台选择器**：`全部机台（汇总）` 或指定某一台
- 选择后**全局生效**：总览 KPI/机台卡/柱状图、FAIL 明细、设备状态、良率全部按所选机台过滤（FAIL 明细与计数走服务端 `?machine=` 参数，其余前端过滤）
- 点击总览里的机台卡 = 快速切到该机台；再次从选择器选「全部机台」回到汇总
- 选择记忆在浏览器（localStorage），刷新后保持

## 5. 在线 XML 报告查看

- FAIL 明细行点「**报告**」→ 新标签打开 `/api/xmlview?id=<id>`
- 服务端按 id 取报告内容（本机白名单直读，或跨机 HTTP 从源机拉取）→ 复用主程序解析（`XmlParser.ParseReportText`）→ 渲染等效本地查看器的 HTML：
  - SN 标题 + 状态徽章（PASS 墨黑 / FAIL 主红）
  - KPI 四卡：测试项总数 / 失败（计入不良）/ 排除项 / 通过项
  - 信息网格：机台 TESTER / 操作模式 / 测试时间（PANEL 优先）/ 整体状态 / 文件名 / SN
  - 测试项明细表：# / 测试项 / 测量值 / 下限 / 上限 / 单位 / 状态（失败项标红、`Get Unit Information` 与 `UUT Status Err` 灰色标注"不计入不良"）
  - 底部：查看原始 XML / 返回列表
- 解析口径与本地 `XmlViewerForm` **完全一致**（同一套 C# 逻辑），并有自检断言锁定

## 6. 代码结构与新增项

```
FctAggregator/
├─ XmlParser.cs          （改）ReportData 增加 PanelTimestamp；新增 ParseReportText(文本重载)
├─ XmlReportHtml.cs      （新）在线报告 HTML 渲染器（等效本地查看器）
├─ WebAggServer.cs       （改）public/ 静态资源服务 + GET /api/xmlview 路由
├─ public/demo/index.html（新）Dashboard 前端（零依赖、黑白红、3s 轮询、响应式、机台筛选）
└─ selftest/SelfTest.cs  （改）+18 项「在线 XML 查看」断言
```

## 7. 本版假设（待确认）

1. **机台筛选范围**：FAIL 明细/计数走服务端过滤（聚合库只存 FAIL 摘要），PASS 数据尚未纳入（依赖 P4 源机检索 `/api/mesh/query`）。
2. **在线报告仅覆盖 FAIL**（聚合库 `agg_records` 有记录的）；PASS 报告的在线查看留待 P4 源机检索。
3. **跨机 XML 拉取**：走现有 `FetchXmlForFail`（源机 `/api/file?id=`），源机聚合库有该记录时可用；源机离线时报告页提示"不可用"。
4. **设备状态/良率页为占位**：系统信息（P6 设备监控上报）与正式良率（P1 yld_daily）落地后自动填充。
5. **字段命名**：后端序列化为 PascalCase，前端已做大小写兼容读取（此前 demo 曾因直接用小写取字段导致机台卡显示异常，本版已修复）。
6. **演示数据**：当前运行库中的 `FCT3` 示例记录与 `data/agg_xml/FCT-TEST/` 示例 XML 为验证用，正式环境由机台推送产生。

## 8. 下一步（按《统一实施方案_总纲》）

P1 剩余（yld_daily 心跳日统计、users 三角色+审计、/api/status）→ P3 只读核心同步 → P4 在线 XML 检索（PASS 报告）→ P5 维修/待办看板。
