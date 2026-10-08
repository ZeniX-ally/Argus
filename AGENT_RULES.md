# Argus 主程序仓库 · Agent 强制规章（v1.1，2026-09-11）

> **本文件是硬性规章，不是参考文档。** 任何 AI agent 在本仓库工作前，必须完整阅读本文件
> 并逐条遵守；违反任一条 = 本次任务失败，需回滚后重做。

---

## §0 强制阅读顺序（任何改动之前，无例外）

1. **本文件**（AGENT_RULES.md，规章）
2. **CODEBUDDY.md §0 版本快照**（当前版本/断言基线/结构变更）
3. **`FctAggregator/更新日志.md` 最新 2 条**（了解当前坑与"明确不做"边界）
4. `git status` 确认工作区干净

未读完就动手 = 违规。

---

## §1 工作流分级（按改动量选 Tier）

| Tier | 适用 | 必须完成 |
|---|---|---|
| **Tier 1** | 小修 / bug fix | 改代码 → build → selftest 全绿 → 更新日志 patch 条目 → commit |
| **Tier 2** | 新功能 | Tier 1 + 自检断言随行（同一提交）→ csproj 版本 minor+1（Version/AssemblyVersion/FileVersion 三处）→ 更新日志完整条目（三段式：功能/自检/坑/明确不做） |
| **Tier 3** | 发版 | Tier 2 + make_release.ps1 出包（纯净性全过）→ push → 更新 CODEBUDDY.md §0 快照。**必须先经用户确认** |

**提速规则**：迭代期可只 build 主工程自查，但**提交前必须完整跑 sln build + selftest**；禁止以"改动小"为由跳过 selftest。

---

## §2 提交前硬门槛（六查，逐项过，缺一不可）

1. `dotnet build Argus.sln -c Release` **0 警告 0 错误**
2. selftest **全绿**且断言数 ≥ 基线（**1252**，v3.57.3 起；基线只增不减，**删功能经用户授权可下调**——v3.31.0 删聚合、v3.40.0 删 Seq、v3.48.0 撤回盘口动效、v3.50.0 删分析页）
3. `git status` 只含预期文件（无 bin/obj/data/临时文件混入）
4. 兼容性铁律未被破坏：老机台库直开自动补表（Database 构造幂等 Init）/ config 缺省可运行
5. 更新日志已写（坑必须记录——是给下一个 agent 的）
6. 版本号策略正确：功能 minor / 修复 patch / 纯结构不升版本

---

## §3 红线（禁止事项，违反即失败）

1. **禁止绕过 selftest 提交**；禁止在自检外新增测试框架
2. **禁止引入新第三方依赖**（白名单：Microsoft.Data.Sqlite / SQLitePCLRaw / System.IO.Ports / TDMSReader）
3. 禁止提交 bin/obj、data/、logs/、Releases/、截图、临时文件（勿 force-add）
4. 禁止改动 Releases/、历史发版、更新日志旧条目、.gitignore 兼容性条目
5. **禁止随意写脚本落盘**：一次性验证不创建文件；沉淀脚本只放 `tools/` 或 `scripts/`
6. 禁止拆分巨文件（Database/MainForm 保持整体）；新代码放对应域新文件
7. 禁止绕过 Database.Init 幂等 `CREATE TABLE IF NOT EXISTS` 直接改表（v3.31.0 起已无 DbMigrator）；禁止绕过 Config.cs 三处同步直接加配置键
8. 分发器已独立仓库（e:/FctDistributor）：本仓库不维护其源码；Xlsx.cs/StationDetector.cs/AppIcon.cs 为两侧副本，**改一侧必须同步另一侧**（无自动比对，靠纪律）

---

## §4 关键命令（照抄执行）

```bash
# 构建（提交前必跑）
cd e:/Argus-main && dotnet build Argus.sln -c Release

# selftest —— 禁止管道直连（会挂死），必须文件重定向
cd e:/Argus-main/FctAggregator/selftest/bin/Release/net8.0-windows
./FctAggregator.SelfTest.exe > selftest_run.log 2>&1; tail -3 selftest_run.log
# 验收：尾部 "==== 全部通过 ===="，无 [FAIL] 行；断言数用 grep -ac "\[OK\]" selftest_run.log 统计

# 发版（Tier 3，用户确认后）
cd e:/Argus-main && powershell -ExecutionPolicy Bypass -File scripts/make_release.ps1

# 提交格式
git commit -m "feat:|fix:|chore:|docs:|refactor: <英文单行>"
```

---

## §5 验收与汇报

- 汇报用中文 + 表格，包含：改动文件、build/selftest 结果、版本号、踩坑
- 发版/推送前必须征求用户确认
- 工具输出 GBK 乱码是显示问题不是错误；路径用正斜杠 `e:/Argus-main`

---

*配套文档：CODEBUDDY.md（唯一流程 Spec，本规章的依据）；更新日志.md（每版踩坑记录）。*
