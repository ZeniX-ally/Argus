# FCT-TdmsViewer — TDMS 文件查看器

解析、浏览、绘图、导出 NI TDMS 文件。`net8.0-windows` / WinForms。

## 用法

**图形界面**

```powershell
FCT-TdmsViewer.exe                     # 空界面，可把 .tdms 拖进来
FCT-TdmsViewer.exe <文件.tdms>         # 直接载入
```

**命令行**

```powershell
FCT-TdmsViewer.exe --info <文件.tdms>
FCT-TdmsViewer.exe --info <文件> --group "6.1 Power Test"
FCT-TdmsViewer.exe --info <文件> --group "8.11 Resolver Test" --channel KL30
FCT-TdmsViewer.exe --summary <文件> [--out x.csv]
FCT-TdmsViewer.exe --help
```

| 参数 | 说明 |
|---|---|
| `--info <文件>` | 打印组/通道概览；配 `--group` 列出该组各通道统计 |
| `--group <名>` | 指定组 |
| `--channel <子串>` | 按名字过滤通道（不区分大小写） |
| `--summary <文件>` | 把**全部**通道的统计导成 CSV（组/通道/类型/点数/min/max/mean/std/首/末） |
| `--out <路径>` | 指定输出文件 |

## 界面

```
┌─ 工具栏: 打开 / 导出选中通道 / 导出结构清单 / 清空勾选 / 隐藏通道树 / 收起数据·属性 ┐
├────────────┬──────────────────────────────────────────────┤
│ 搜索框     │            波形图 (GDI+ 自绘)                 │
│ ┌────────┐ │   滚轮缩放 · 拖拽平移 · 双击复位               │
│ │ 组     │ │   鼠标十字线读值                              │
│ │ └通道☑ │ ├──────────────────────────────────────────────┤
│ │ └通道☑ │ │ 统计条: n / min / max / mean / std / 首 / 末  │
│ └────────┘ │ ┌ 数据 ┬ 属性 ┐                              │
│            │ │  表格或属性列表                             │
├────────────┴──────────────────────────────────────────────┤
│ 状态栏: 组数 / 通道数 / 文件大小 / 解析耗时                 │
└───────────────────────────────────────────────────────────┘
```

- **勾选通道**叠加显示波形，最多 8 条（用不同颜色 + 图例）；导出不受此限制
- **隐藏通道树 / 收起数据·属性**：一键折叠左栏或底栏，把整块区域让给波形；再点一次恢复
- **选中组**（而非通道）时，数据页显示该组**全部通道的统计概览表**
- **搜索框**过滤通道名 —— 单组 160 个通道时基本必用
- 非数值通道（字符串/时间）灰色显示、不参与绘图，但表格里能看内容
- 支持**拖放**文件到窗口

## 实现要点

**TDMS 解析用 `TDMSReader` 3.1.0**（NuGet，纯 C#、无依赖、.NETStandard2.0）。
选它而非自己实现二进制解析，是因为 TDMS 有分段、交错/非交错、增量元数据、
DAQmx raw index 等一堆边界情况，自己写容易在真实文件上翻车。

**解析结果已与 Python `npTDMS` 1.10.0 交叉核对一致**：同一文件 17 组、
各组采样点数、`RES_v_ResAng` 的 `n=102 min=0.7857 max=5.4987 mean=4.2043`、
`LVDCM_v_KL30 min=14.0393 max=14.0649` 全部逐位吻合。

**波形图是 GDI+ 自绘**，没用 `System.Windows.Forms.DataVisualization.Charting`
（那是 .NET Framework 专有，.NET 8 用不了），也没为一张折线图引入 ScottPlot。
点数远超像素宽度时按列抽样，避免绘制上万个点。

**内存**：打开时只读元信息（用 `Channel.DataCount`，不载数据），
数据按通道惰性读取并缓存。2.77 MB / 2720 通道的文件，GUI 常驻约 112 MB。

**为什么是控制台程序（`OutputType=Exe`）而非 `WinExe`**：`WinExe` 启动时
.NET 已把 `Console.Out` 绑为 `TextWriter.Null`，CLI 输出在管道/重定向下会全部丢失
（实测 stdout 恒为空）。改用 `Exe` 让 stdout 天然可重定向，GUI 模式再
`ShowWindow(SW_HIDE)` 隐藏控制台窗口。

## 关于 FCT 数据的对应关系

TDMS 的 **Group 就是 FCT 测试项**，通道就是 XCP 变量：

| TDMS Group | 对应 |
|---|---|
| `6.1 Power Test` | XML 里 `6.1.x.x` 那批测项 |
| `8.11 Resolver Test` | `8.11.6.1 RES_v_ResAng(45°)` 等 |
| `8.18 Gate Driver Test` | `8.18.2.1 SiC_G_HU...` 等 |

**XML 只存判定结果（一个值 + 上下限），TDMS 存整个测试过程的波形**（100 Hz 采样）。
所以排查失败项时，用 XML 定位是哪一项失败，再用本工具看该项对应 Group 的信号变化过程。
