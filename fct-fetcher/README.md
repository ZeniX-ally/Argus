# fct-fetcher — FCT fail 文件捞取器

按日期区间捞取 **有 fail 的 XML**,提取 SN,并同步捞取对应 SN 的 **CSV** 与 **TDMS**。

## 用法

```powershell
cd F:\Programe\G4_9-QA\fct-fetcher

# 基本:捞 7/22~7/24 的 fail
python fetch.py --start 20260722 --end 20260724

# 日期也接受带分隔符写法
python fetch.py --start 2026-07-22 --end 2026-07-24

# 同时把命中的 XML/CSV/TDMS 复制到 out\files\{SN}\
python fetch.py --start 20260722 --end 20260724 --copy

# 临时覆盖路径
python fetch.py --start 20260722 --end 20260724 --results-root E:\Results --out D:\tmp
```

`--end` **含当天**。

## 输出

`out\fetch_{起}-{止}.xlsx`,三个 sheet:

| Sheet | 内容 |
|---|---|
| 捞取清单 | 每条 fail 记录一行:SN / 结果 / 站点 / 型号 / 日期 / 失败项数 / 失败项详情 / XML 路径 / CSV 路径 / TDMS 路径 |
| 失败项明细 | 一个失败项一行,带测量值与上下限 |
| 失败项排名 | 按次数降序,含影响 SN 数 |

无 pandas 时自动降级输出 CSV。

## 目录结构要求

```
{results_root}\{Online|Offline}\{型号 E+7位}\{yyyyMMdd}\{前缀}_Fts_PEU_G49_{机台}_{SN}_{ts1}_{ts2}.xml
```

不符合此结构的路径会被计入「路径不合规跳过」。目录名大小写不敏感。

## FAIL 判定规则

与 `fct-fail-ranker/XmlScanner.cs` 保持一致,避免两个工具口径不同:

| 前缀 | 处理 |
|---|---|
| `P_` | 直接跳过(不可能是 fail),**不解析 XML**,省时间 |
| `F_` | 恒为 FAIL |
| `O_` | 中断。**仅当含真实失败项时**算 FAIL,否则记为 INTERRUPTED 并跳过 |

- 失败项 = `TEST` 节点且 `STATUS="Failed"`
- 排除 `Get Unit Information`、`UUT Status Err`(非产品真实不良)
- `FACTORY/@USER == "debug"` 的记录跳过
- SN 以 `DUT/@ID` 为准(权威),文件名仅作兜底

实测样本(D:\Results, 306 个 XML)口径守恒:
`204 P_ + 33 INTERRUPTED + 7 debug + 62 FAIL = 306`

## 配置 `config.json`

| 键 | 说明 |
|---|---|
| `results_root` | XML 根目录,默认 `D:/Results` |
| `csv_root` | CSV 根目录。**留空则不捞** |
| `tdms_root` | TDMS 根目录。**留空则不捞** |
| `match_mode` | `contains`(默认) / `prefix` / `exact` —— 按 SN 匹配文件名的方式 |
| `output_dir` | 输出目录 |
| `copy_files` | 是否复制文件,等价于 `--copy` |
| `include_o_fails` | `true`(默认)纳入 O_ 的真实 fail;`false` 则只捞 `F_` |
| `skip_debug` | 跳过 `USER=debug` |
| `categories` | 要扫的类别,默认 `["Online","Offline"]` |

## ⚠️ CSV / TDMS 尚未配置

`csv_root` 与 `tdms_root` 目前为空,因为本机 `D:\Results` 只有 XML,
全盘也未搜到任何 FCT 的 `.csv` / `.tdms`,**无法确认其真实路径与命名规则**。

填好路径后即可生效。匹配逻辑是"文件名包含 SN"(`match_mode=contains`),
对目录层级无要求 —— 会递归索引整个根目录。

若实际命名不含 SN(例如用时间戳关联),需要调整 `find_by_sn()`。

## 性能

- `P_` 文件不解析 XML,只看文件名前缀,占比通常最大(样本中 204/306)
- CSV/TDMS 目录**预先索引一次**,而非每个 SN 全盘搜索
