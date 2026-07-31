# FCT-Fetcher — fail 文件捞取器

按日期区间捞取**有 fail 的 XML**，提取 SN，并同步捞取对应 SN 的 **CSV** 与 **TDMS**。

`net8.0-windows` / WinForms / 零第三方依赖（xlsx 手写 OOXML，与 `fct-fail-ranker` 同一套骨架）。

## 用法

双击 `FCT-Fetcher.exe` 打开图形界面；带参数则走命令行：

```powershell
FCT-Fetcher.exe --start 20260722 --end 20260724
FCT-Fetcher.exe --start 2026-07-22 --end 2026-07-24 --no-pack
FCT-Fetcher.exe --start 20260722 --end 20260724 --results-root E:\Results --out D:\tmp
FCT-Fetcher.exe --help
```

| 参数 | 说明 |
|---|---|
| `--start` / `--end` | 日期，`yyyyMMdd` 或 `yyyy-MM-dd`，**含首尾两天**（必填） |
| `--results-root` | 覆盖 Results 根目录 |
| `--tdms-root` | 覆盖 TDMS 根目录 |
| `--out` | 覆盖输出目录 |
| `--pack` | 分类归集并打包（**默认已开**） |
| `--no-pack` | 不打包，只出清单 xlsx |
| `--keep-stage` | 打包后保留未压缩的中间目录 |
| `--only-f` 已移除 | 判定不再看前缀 |
| `--categories <列表>` | 要扇的分类，逗号分隔，默认 `Offline` |
| `--config` | 指定配置文件 |

## GUI

双击 exe 打开。上半部分配置三个路径与日期区间，下半部分两个页签：

- **捞取结果**：表格列出 SN / 站点 / 日期 / 型号 / 失败项数 / CSV / TDMS 数 / 失败项。
  **CSV 或 TDMS 未命中的行标黄**，一眼看出缺件；双击某行在资源管理器里定位该 XML。
- **日志**：完整扯描与统计过程。

顶部摘要条实时回显：`命中 N 条 / SN M 个    CSV x/N    TDMS y/N    已打包 xxx.zip (…)`。
全部命中时绿字，有缺件时橙字。路径与选项改动会自动存回 `config.json`。

## 目录结构

**Results**（XML + CSV）：
```
{results_root}\{Online|Offline}\{型号 E+7位}\{yyyyMMdd}\{前缀}_Fts_PEU_G49_{机台}_{SN}_{ts1}_{ts2}.xml
```

**TDMS**：根目录不同，其下层级与 Results 完全相同：
```
{tdms_root}\{Online|Offline}\{型号}\{yyyyMMdd}\{SN}_{yyyyMMddHHmmss}.tdms
```

目录名大小写不敏感。不合规的路径计入「路径不合规跳过」。

## 三类文件的关联方式

| 类型 | 定位方式 |
|---|---|
| **XML** | 扫描源 |
| **CSV** | 与 XML **同目录同名**，仅扩展名不同（一个 XML 配一个 CSV，同时生成）→ 直接换扩展名，最可靠 |
| **TDMS** | 镜像目录下按 `{SN}_*.tdms` 匹配；找不到时可回退到整个 `tdms_root` 按 SN 查（`tdms_fallback_global`） |

TDMS 的时间戳与 XML **不一定相同**（精度不同、可能有偏差），所以**只按 SN 匹配、不做时间比对**。
同一 SN 若重测会命中多个 TDMS，全部记录。

## 判定规则

1. **只扇 `Offline`** —— 生产环境 `Online` 全是 pass，不扇（可用 `--categories` 或 GUI 勾选框改）。
   实现上是**只枚举 `{results_root}\Offline`** 子目录，而非全盘扇描后再过滤。
2. **只看 XML 内容**：解析后含真实 fail 测试项即判定命中，**不依赖文件名前缀**
   （`P_`/`F_`/`O_` 仅作展示信息保留）。
3. 真实 fail 项 = `TEST` 节点且 `STATUS="Failed"`，默认排除
   `Get Unit Information` 与 `UUT Status Err`（非产品真实不良）。
   可用 `exclude_ignored_steps: false` 关掉这层排除。
4. `FACTORY/@USER == "debug"` 跳过。
5. SN 以 `DUT/@ID` 为准（权威），文件名仅兜底。

> 注：本规则与 `fct-fail-ranker` 不同 —— 后者按文件名前缀判定（`F_` 恒 fail、`O_` 区分中断）。
> 本工具改为纯内容判定，不受命名约定影响。

## 输出

```
{output_dir}\
  20260722-20260724.zip            主产物（单日则 20260722.zip）
  fetch_20260722-20260724.xlsx     清单（包内也有一份）
```

zip 内部**按类型分三个文件夹**：

```
xml\    *.xml
csv\    *.csv
tdms\   *.tdms
fetch_{起}-{止}.xlsx        清单一并带上，便于整包交付
```

打包完成后中间目录自动删除（`--keep-stage` 可保留）。
同名文件自动加 `_2`/`_3` 后缀，不会互相覆盖。

清单 xlsx 含三个 sheet：

| Sheet | 内容 |
|---|---|
| 捞取清单 | 每条 fail 一行：SN / 结果 / 站点 / 型号 / 日期 / 失败项 / CSV 路径 / TDMS 路径 / XML 路径 |
| 失败项明细 | 一个失败项一行，带测量值与上下限 |
| 失败项排名 | 按次数降序，含影响 SN 数 |

## config.json

```json
{
  "results_root": "D:\\Results",
  "tdms_root": "D:\\TDMS Log",
  "output_dir": "",
  "pack_files": true,
  "keep_stage_dir": false,
  "skip_debug": true,
  "exclude_ignored_steps": true,
  "tdms_fallback_global": true,
  "categories": ["Offline"]
}
```

`output_dir` 留空则用 exe 同级的 `out\`。GUI 里改动会自动存回本文件。

## 验证

**真实数据**（测试机 `D:\Results`）只扇 Offline 的 156 个 XML，口径守恒：

```
113 无fail项  +  17 debug  +  26 命中  =  156
```

此结果与独立编写的校验脚本交叉比对一致。
注：测试机的数据分布不代表生产环境（它的 Online 里也有 fail 文件）。

**selftest**（`selftest\setup.ps1`）构造部分命中场景以检验统计准确性：

```powershell
.\selftest\setup.ps1
FCT-Fetcher.exe --start 20260722 --end 20260722 `
  --results-root .\selftest\Results --tdms-root ".\selftest\TDMS Log" `
  --out .\selftest\out
```

3 个 XML → 期望 `CSV 2/3`、`TDMS 2/3`（其中一个 SN 有 2 个 TDMS 模拟重测），
打包得 `20260722.zip` 含 8 个文件（xml 3 / csv 2 / tdms 3 + 清单），实测吻合。

## 实现注记

- **为什么是控制台程序（`OutputType=Exe`）而非 `WinExe`**：`WinExe` 启动时 .NET 已把
  `Console.Out` 绑为 `TextWriter.Null`，`AttachConsole` 在管道/重定向场景下不可靠
  （实测 stdout 恒为空，连重定向到文件都是 0 字节）。改用 `Exe` 让 stdout 天然可重定向，
  GUI 模式再用 `ShowWindow(SW_HIDE)` 把控制台窗口隐藏。
- **TDMS 预先索引一次**（key = 文件名首段即 SN），而非每个 SN 全盘搜，避免 O(n×m)。
