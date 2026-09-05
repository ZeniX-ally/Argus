# Argus 图形化升级向导（Upgrade Wizard）v3.22.1

产线运维的图形化一键升级入口。**v3.22.1 重写**：不再自己实现部署逻辑，改为
`deploy_update.ps1` 部署引擎的 GUI 前端——演练/停进程/备份/config 合并/SHA256
校验/回滚与命令行升级完全同引擎、同行为。

## 使用

```
Argus.exe upgrade
```

1. **选包**：启动时自动发现程序目录（及上级）版本最高的 `Argus-v*-update.zip`，也可点「浏览」手选；
2. **解包校验**：校验包内 `Argus.exe` 存在、无 `data\`/`logs\`/`*.db`（防污染现场数据），显示包内版本；
3. **演练**：跑 `deploy_update.ps1`（无 `-Execute`），只输出部署计划不改任何文件——
   可看到将覆盖/新增哪些文件、备份到哪、config 合并哪些字段；
4. **执行**：确认后真正部署（自动停 Argus 进程（向导自身除外）→ 备份到 `<安装目录>\_backup_时间戳\`
   → 覆盖程序文件 + `runtimes\` 树 → config.json 合并更新（station_id / results_root /
   webhook_url / agg_token 保留现场值）→ SHA256 逐文件校验）；
5. **完成**：显示回滚命令，可一键退出并启动新版。

## 回滚

```
powershell -ExecutionPolicy Bypass -File deploy_update.ps1 -Rollback "<安装目录>\_backup_时间戳"
```

备份目录路径在向导日志里有完整命令可复制。

## 部署脚本从哪来

查找顺序（`FindDeployScript`）：

1. `<安装目录>\tools\deploy_update.ps1`
2. `<安装目录>\deploy_update.ps1`
3. **升级包内**（官方 v3.22.1 及以后的包自带——make_package.ps1 已把它打进完整包与更新包）

## 实现说明

- 全部 UI + 流程在一个文件 `UpgradeWizard.cs`；纯逻辑（`ValidateStage` /
  `FindDeployScript`）为 public static，selftest 直接覆盖。
- 向导把自身 PID 通过 `-ExcludePid` 传给脚本——向导本身就是运行中的 Argus.exe，
  不排除会被部署引擎在"结束进程"一步停掉。
- 部署统一加 `-NoStart`，由向导在关闭后延迟 2.5s 启动新版（等单实例互斥体释放）。
- 不支持热升级：部署会先停掉正在运行的 Argus。

## 版本历史

| 版本 | 日期 | 变更 |
|-----|------|------|
| 3.22.1 | 2026-09-03 | 重写：改为 deploy_update.ps1 的 GUI 前端；包结构对齐真实发版包；-ExcludePid 防自杀；打包器随包带部署脚本 |
| 3.21.1 | 2026-09-02 | 初版简易向导（包结构/部署路径与实际发版包不匹配，v3.22.1 修复） |
