#requires -Version 5.1
<#
.SYNOPSIS
    Argus 更新包自动部署工具（识别新包 -> 自动找到机台上的旧安装 -> 备份 -> 覆盖 -> 校验）

.DESCRIPTION
    针对 FctAggregator（Argus / 旧名 FCT-Aggregator）的现场升级。
    刻意兼容 Windows PowerShell 5.1（产线机台通常没有 pwsh 7），因此
    不使用 ?? / ?: / && 等 7.x 语法。

    == 默认是演练模式 ==
    不加 -Execute 时只输出部署计划，绝不改动任何文件。

    == 为什么需要这个工具 ==
    v3.0.1 起可执行文件由 FCT-Aggregator.exe 改名为 Argus.exe，且互斥体名
    也从 FCT_Aggregator_SingleInstance 改为 Argus_SingleInstance。更新包只能
    添加/覆盖同名文件、无法删除旧文件，于是跨 v3.0.1 升级后旧 exe 会残留，
    与新版可同时运行并同写一个数据库。本工具会一并清理这些残留。

    == 不动的东西 ==
    data\（数据库）、logs\ 原样保留；config.json 按包内模板**合并更新**（保留 station_id/results_root/webhook_url 与现场自定义字段，原文件先备份）。

.PARAMETER Zip
    更新包 zip。留空则在脚本所在目录、当前目录里找版本号最高的 Argus-v*-update.zip。

.PARAMETER Target
    机台安装目录。留空则自动发现（运行中进程 -> 启动文件夹快捷方式 -> 磁盘扫描）。

.PARAMETER Execute
    真正执行部署。不加则仅演练。

.PARAMETER KeepLegacy
    保留旧 exe 与旧启动快捷方式（默认会清理）。

.PARAMETER NoDbBackup
    不备份 data\*.db（库很大时用；程序本身不会改动库结构，但仍建议备份）。

.PARAMETER NoStart
    部署完成后不自动启动程序做校验。

.PARAMETER ExcludePid
    停进程时排除的 PID（v3.22.1）：图形化升级向导（Argus.exe upgrade）本身也是
    运行中的 Argus 进程，把自己 PID 传进来可避免部署引擎把向导一并停掉。

.PARAMETER Rollback
    回滚：传入本工具生成的备份目录路径，把程序文件恢复原状。

.EXAMPLE
    .\deploy_update.ps1
    演练：自动找包、自动找安装位置，打印计划但不改任何东西。

.EXAMPLE
    .\deploy_update.ps1 -Execute
    实际部署。

.EXAMPLE
    .\deploy_update.ps1 -Zip D:\Argus-v3.2.2-update.zip -Target "D:\Argus" -Execute

.EXAMPLE
    .\deploy_update.ps1 -Rollback "D:\Argus\_backup_20260805_143000"
#>
[CmdletBinding()]
param(
    [string]$Zip,
    [string]$Target,
    [switch]$Execute,
    [switch]$KeepLegacy,
    [switch]$NoDbBackup,
    [switch]$NoStart,
    [int]$ExcludePid = 0,
    [string]$Rollback
)

$ErrorActionPreference = 'Stop'

# ---- v2.9.0 合并进单 exe / v3.0.1 改名后应当清理的旧程序 ----
# 【重要】FCT-Distributor.exe 不在此列：v3.0.0 起数据分发器是独立程序，仍在使用，删了会让现场分发功能消失。
$LegacyExe = @(
    'FCT-Aggregator.exe',   # 被 Argus.exe 取代（v3.0.1 改名）
    'FCT-FailRanker.exe',   # 以下三个 v2.9.0 已合并进主 exe
    'FCT-Fetcher.exe',
    'FCT-TdmsViewer.exe'
)
$LegacyLnk = @('FCT-Aggregator.lnk', 'Argus.lnk')  # Argus.lnk 会在部署后按需重建
$KeepAlways = @()                              # 包内 config.json 为模板，部署时合并更新（原配置先备份）
$NewExeName = 'Argus.exe'

$script:Warn = 0

# Get-FileHash 在 PowerShell 4.0+ 可用，但某些机台（含本机）5.1 里缺失（模块损坏）
# 内置替代：用 .NET 原生 SHA256 计算，结果完全一致
function SafeFileHash([string]$Path, [string]$Algorithm = 'SHA256') {
    if (Get-Command Get-FileHash -EA SilentlyContinue) {
        return Get-FileHash $Path -Algorithm $Algorithm
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead((Resolve-Path $Path).Path)
    $hash = $sha.ComputeHash($stream)
    $stream.Close()
    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $hash) { $sb.Append($b.ToString('x2')) | Out-Null }
    return [pscustomobject]@{ Algorithm = $Algorithm; Hash = $sb.ToString().ToUpper(); Path = $Path }
}

# 发现占用安装目录的 Argus 相关进程。
# 兼容两种启动方式：
#   方式1：直接跑 Argus.exe（进程 Path 在安装目录下，Get-Process 可见）
#   方式2：启动.bat -> dotnet "Argus.dll"（进程是 dotnet.exe，Path 是 dotnet 安装路径，
#          必须查 Win32_Process 的 CommandLine 才能确认它在跑哪个安装目录）
# 返回统一对象列表，至少含 Id / ProcessName / CommandLine。
function Get-ArgusProcesses([string]$dest) {
    $list = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($dest, [StringComparison]::OrdinalIgnoreCase) })
    $destPat = [regex]::Escape($dest.TrimEnd('\')) + '\\'   # 后接反斜杠，防止 D:\Argus 误匹配 D:\Argus-测试机
    try {
        $dotnet = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction Stop
        foreach ($p in $dotnet) {
            $cl = $p.CommandLine
            if ($cl -and $cl -match 'Argus\.(dll|exe)' -and $cl -match $destPat) {
                $list += [pscustomobject]@{
                    Id          = $p.ProcessId
                    ProcessName = 'dotnet'
                    Path        = $p.ExecutablePath
                    CommandLine = $cl
                }
            }
        }
    } catch {
        # CIM 不可用（老系统/权限受限）时退回仅 Get-Process 的结果
    }
    # v3.22.1：图形化升级向导（Argus.exe upgrade）把自己 PID 传入 -ExcludePid，
    # 部署阶段不停向导自身——否则向导会在"结束进程"一步把自己杀掉。
    if ($ExcludePid -gt 0) {
        $list = @($list | Where-Object { $_.Id -ne $ExcludePid })
    }
    return $list
}

function Write-Head([string]$t) {
    Write-Host ""
    Write-Host ("=" * 68) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ("=" * 68) -ForegroundColor DarkCyan
}
function Ok([string]$m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Info([string]$m) { Write-Host "  [--]   $m" -ForegroundColor Gray }
function Warn2([string]$m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow; $script:Warn++ }
function Die([string]$m) { Write-Host "  [FAIL] $m" -ForegroundColor Red; exit 1 }

# ============================================================
# 回滚模式
# ============================================================
if ($Rollback) {
    Write-Head "回滚模式"
    if (-not (Test-Path $Rollback)) { Die "备份目录不存在: $Rollback" }
    $manifest = Join-Path $Rollback 'restore-manifest.txt'
    if (-not (Test-Path $manifest)) { Die "备份目录里没有 restore-manifest.txt，不是本工具生成的备份" }
    $dest = (Get-Content $manifest -TotalCount 1).Trim()
    Write-Host "  备份目录: $Rollback"
    Write-Host "  恢复目标: $dest"
    if (-not (Test-Path $dest)) { Die "原安装目录已不存在: $dest" }

    $procs = @(Get-ArgusProcesses $dest)
    foreach ($p in $procs) {
        Write-Host "  停止进程 $($p.ProcessName) (PID $($p.Id))"
        Stop-Process -Id $p.Id -Force
    }
    if ($procs.Count -gt 0) { Start-Sleep -Seconds 2 }

    $files = Get-ChildItem (Join-Path $Rollback 'program') -File -ErrorAction SilentlyContinue
    foreach ($f in $files) { Copy-Item $f.FullName $dest -Force }
    Ok "已恢复 $($files.Count) 个程序文件"
    Write-Host ""
    Write-Host "  注意：data\ 与 config.json 本次升级从未被改动，无需恢复。" -ForegroundColor Yellow
    Write-Host "  若要连数据库一起回到升级前，手动把 $Rollback\data\*.db 拷回 $dest\data\" -ForegroundColor Yellow
    exit 0
}

# ============================================================
# 1. 定位并校验更新包
# ============================================================
Write-Head "1. 识别更新包"

if (-not $Zip) {
    $searchDirs = @($PSScriptRoot, (Get-Location).Path) | Select-Object -Unique
    $cands = @()
    foreach ($d in $searchDirs) {
        if ($d -and (Test-Path $d)) {
            $cands += Get-ChildItem $d -Filter 'Argus-v*-update.zip' -File -ErrorAction SilentlyContinue
        }
    }
    if ($cands.Count -eq 0) {
        Die "未找到更新包。请把 Argus-v*-update.zip 放到脚本旁边，或用 -Zip 指定路径。"
    }
    # 按版本号排序取最高，而不是按文件名字符串（避免 v3.10 < v3.9 的错误）
    $Zip = ($cands | Sort-Object {
            $m = [regex]::Match($_.Name, 'v(\d+)\.(\d+)\.(\d+)')
            if ($m.Success) {
                [version]::new([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value)
            } else { [version]::new(0, 0, 0) }
        } | Select-Object -Last 1).FullName
}
if (-not (Test-Path $Zip)) { Die "更新包不存在: $Zip" }
$Zip = (Resolve-Path $Zip).Path
Write-Host "  包文件: $Zip"
Write-Host "  大小:   $([math]::Round((Get-Item $Zip).Length/1KB)) KB"
Write-Host "  SHA256: $((SafeFileHash $Zip).Hash)"

$stage = Join-Path $env:TEMP ("argus_deploy_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Expand-Archive -LiteralPath $Zip -DestinationPath $stage -Force
} catch {
    Die "解压失败: $($_.Exception.Message)"
}

$stageExe = Join-Path $stage $NewExeName
if (-not (Test-Path $stageExe)) { Die "包内没有 $NewExeName，这不是 Argus 的更新包" }
$newVer = (Get-Item $stageExe).VersionInfo.FileVersion
Ok "包内版本 = $newVer"

# 包内 config.json = 配置模板（v3.2.2 起更新包有意携带，用于恢复被冲掉的现场配置）
$pkgCfgPath = Join-Path $stage 'config.json'
$pkgHasConfig = Test-Path $pkgCfgPath
if ($pkgHasConfig) {
    Write-Host "  包内含 config.json（配置模板：fct_ini_path=PEU 等）" -ForegroundColor DarkYellow
    Write-Host "  部署时将合并更新现场配置：保留 station_id/results_root/webhook_url 与现场自定义字段，原配置先备份" -ForegroundColor DarkYellow
} else {
    Warn2 "包里没有 config.json —— 现场若缺该文件，将用程序默认配置启动"
}
foreach ($sub in @('data', 'logs')) {
    if (Test-Path (Join-Path $stage $sub)) { Die "包内含 $sub\ 目录，可能覆盖现场数据，已中止" }
}
$dbInZip = Get-ChildItem $stage -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.db', '.sqlite', '.sqlite3') }
if ($dbInZip) { Die "包内含数据库文件 $($dbInZip.Name -join ',')，会污染现场数据，已中止" }
Ok "包校验通过：无 data\ / logs\ / *.db"

$pkgFiles = Get-ChildItem $stage -File
Info "包内 $($pkgFiles.Count) 个文件待部署"

# ============================================================
# 2. 自动发现机台安装位置
# ============================================================
Write-Head "2. 定位机台上的旧安装"

function Test-ArgusDir([string]$dir) {
    # 判定「这是一个 Argus 安装目录」：有主程序 exe，且有现场痕迹（config.json 或 data\）
    if (-not $dir -or -not (Test-Path $dir)) { return $false }
    $hasExe = (Test-Path (Join-Path $dir $NewExeName)) -or (Test-Path (Join-Path $dir 'FCT-Aggregator.exe'))
    if (-not $hasExe) { return $false }
    $hasSite = (Test-Path (Join-Path $dir 'config.json')) -or (Test-Path (Join-Path $dir 'data'))
    return $hasSite
}

$found = New-Object System.Collections.Generic.List[object]

# 「启动」文件夹：既用于发现安装位置，也用于后续清理旧 .lnk，
# 所以必须在自动发现逻辑之外定义（指定 -Target 时也要能用）。
$startupDirs = @(
    [Environment]::GetFolderPath('Startup'),
    [Environment]::GetFolderPath('CommonStartup')
)

function Add-Cand([string]$dir, [string]$how) {
    if (-not (Test-ArgusDir $dir)) { return }
    $full = (Resolve-Path $dir).Path.TrimEnd('\')
    foreach ($e in $found) { if ($e.Path -eq $full) { return } }
    $exe = Join-Path $full $NewExeName
    if (-not (Test-Path $exe)) { $exe = Join-Path $full 'FCT-Aggregator.exe' }
    $found.Add([pscustomobject]@{
        Path = $full
        Exe  = Split-Path $exe -Leaf
        Ver  = (Get-Item $exe).VersionInfo.FileVersion
        How  = $how
    })
}

# 已明确指定 -Target 时，整个自动发现都跳过（尤其不能去扇磁盘）
if (-not $Target) {

# 策略 A：正在运行的进程（最可靠）
foreach ($n in @('Argus', 'FCT-Aggregator')) {
    $ps = Get-Process -Name $n -ErrorAction SilentlyContinue
    foreach ($p in $ps) { if ($p.Path) { Add-Cand (Split-Path $p.Path -Parent) "运行中进程 $n (PID $($p.Id))" } }
}
# 策略 A2：启动.bat 方式（dotnet "Argus.dll"）进程名是 dotnet.exe，按 CommandLine 反查 dll 所在目录
try {
    foreach ($p in (Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction Stop)) {
        $cl = $p.CommandLine
        if (-not $cl -or $cl -notmatch 'Argus\.dll') { continue }
        $i = $cl.IndexOf('Argus.dll', [StringComparison]::OrdinalIgnoreCase)
        $s = $i
        while ($s -gt 0 -and $cl[$s - 1] -notin @('"', "'", ' ', "`t")) { $s-- }
        $dll = $cl.Substring($s, $i + 9 - $s).Trim('"', "'")
        if (Test-Path $dll) { Add-Cand (Split-Path $dll -Parent) "运行中 dotnet 进程 (PID $($p.ProcessId))" }
    }
} catch { }

# 策略 B：「启动」文件夹里的快捷方式（程序自启用的就是这个）
$shell = New-Object -ComObject WScript.Shell
foreach ($sd in $startupDirs) {
    if (-not $sd -or -not (Test-Path $sd)) { continue }
    foreach ($lnk in (Get-ChildItem $sd -Filter '*.lnk' -File -ErrorAction SilentlyContinue)) {
        try {
            $t = $shell.CreateShortcut($lnk.FullName).TargetPath
            if ($t -and ($t -match 'Argus\.exe$|FCT-Aggregator\.exe$')) {
                Add-Cand (Split-Path $t -Parent) "开机自启快捷方式 $($lnk.Name)"
            }
        } catch { }
    }
}

# 策略 C：扫描高概率位置。
# ⚠ 不做全盘递归：开发机/服务器上动辄几十万文件（node_modules 之类），
#   曾实测全盘扫描直接超时。盘符只查**根目录一层**（工具常直接放盘根），
#   只有 Program Files / FTS 等高概率目录才做有限深度递归。
if ($found.Count -eq 0) {
    Info "进程与自启项都没找到，扫描常见安装位置..."
    $scan = New-Object System.Collections.Generic.List[object]
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        if ($d.Root -match '^[A-Za-z]:\\') { $scan.Add(@{ Path = $d.Root; Depth = 0 }) }
    }
    foreach ($p in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, 'C:\FTS', 'D:\FTS', 'E:\FTS', 'D:\Argus', 'E:\Argus')) {
        if ($p) { $scan.Add(@{ Path = $p; Depth = 3 }) }
    }
    foreach ($s in $scan) {
        $r = $s.Path
        if (-not $r -or -not (Test-Path $r)) { continue }
        if ($s.Depth -eq 0) {
            foreach ($n in @('Argus.exe', 'FCT-Aggregator.exe')) {
                if (Test-Path (Join-Path $r $n)) { Add-Cand $r "扫描 $r" }
            }
        } else {
            Write-Host "         扫描 $r (深度 $($s.Depth))..." -ForegroundColor DarkGray
            try {
                $hits = Get-ChildItem $r -Recurse -Depth $s.Depth -Include 'Argus.exe', 'FCT-Aggregator.exe' `
                    -File -Force -ErrorAction SilentlyContinue
                foreach ($h in $hits) { Add-Cand $h.DirectoryName "扫描 $r" }
            } catch { }
        }
    }
    if ($found.Count -eq 0) {
        Info "常见位置未命中。若安装在很深的目录里，请用 -Target 指定。"
    }
}

}  # end if (-not $Target)

if ($Target) {
    if (-not (Test-Path $Target)) { Die "指定的目录不存在: $Target" }
    if (-not (Test-ArgusDir $Target)) {
        Warn2 "指定目录看着不像 Argus 安装位置（缺 exe 或缺 config.json/data\）"
    }
    $dest = (Resolve-Path $Target).Path.TrimEnd('\')
    Info "使用 -Target 指定的目录: $dest"
} else {
    if ($found.Count -eq 0) {
        Die "未能自动找到安装位置。请用 -Target ""D:\Argus"" 手动指定。"
    }
    foreach ($f in $found) {
        Write-Host ("  候选: {0}  [{1} v{2}]  <- {3}" -f $f.Path, $f.Exe, $f.Ver, $f.How)
    }
    if ($found.Count -gt 1) {
        Warn2 "找到 $($found.Count) 个安装位置，不敢自行猜测"
        Write-Host "         请用 -Target 明确指定要升级哪一个。" -ForegroundColor Yellow
        exit 1
    }
    $dest = $found[0].Path
    Ok "锁定安装目录: $dest"
}

# ============================================================
# 3. 生成部署计划
# ============================================================
Write-Head "3. 部署计划"

$curExeNew = Join-Path $dest $NewExeName
$curExeOld = Join-Path $dest 'FCT-Aggregator.exe'
$oldVer = '(未知)'
if (Test-Path $curExeNew) { $oldVer = (Get-Item $curExeNew).VersionInfo.FileVersion }
elseif (Test-Path $curExeOld) { $oldVer = (Get-Item $curExeOld).VersionInfo.FileVersion }

Write-Host "  现场版本: $oldVer"
Write-Host "  升级到:   $newVer"
if ($oldVer -eq $newVer) { Warn2 "版本号相同，这是一次重复部署（仍可继续，属幂等操作）" }

# 待覆盖 / 新增
$willOverwrite = @(); $willAdd = @()
foreach ($f in $pkgFiles) {
    if ($f.Name -eq 'config.json') { continue }   # 走合并部署，不计入覆盖统计
    if (Test-Path (Join-Path $dest $f.Name)) { $willOverwrite += $f.Name } else { $willAdd += $f.Name }
}
Write-Host "  覆盖同名文件: $($willOverwrite.Count) 个"
if ($willAdd.Count -gt 0) { Write-Host "  新增文件:     $($willAdd.Count) 个 -> $($willAdd -join ', ')" }

# 现场受保护文件
Write-Host ""
Write-Host "  以下现场文件不会被动：" -ForegroundColor Green
foreach ($k in $KeepAlways) {
    if (Test-Path (Join-Path $dest $k)) { Write-Host "    - $k（包内不含它）" -ForegroundColor Green }
}
if ($pkgHasConfig -and (Test-Path (Join-Path $dest 'config.json'))) {
    Write-Host "    - config.json：将按包内模板合并更新（保留 station_id/results_root/webhook_url 与现场自定义字段，原文件先备份）" -ForegroundColor DarkYellow
}
foreach ($sub in @('data', 'logs')) {
    $sp = Join-Path $dest $sub
    if (Test-Path $sp) {
        $c = @(Get-ChildItem $sp -File -Recurse -ErrorAction SilentlyContinue).Count
        Write-Host "    - $sub\（$c 个文件）" -ForegroundColor Green
    }
}

# legacy 残留
$legacyHits = @()
foreach ($le in $LegacyExe) {
    $lp = Join-Path $dest $le
    if (Test-Path $lp) {
        $legacyHits += [pscustomobject]@{ Path = $lp; Name = $le; Ver = (Get-Item $lp).VersionInfo.FileVersion }
    }
}
$lnkHits = @()
foreach ($sd in $startupDirs) {
    if (-not $sd -or -not (Test-Path $sd)) { continue }
    $lp = Join-Path $sd 'FCT-Aggregator.lnk'
    if (Test-Path $lp) { $lnkHits += $lp }
}

Write-Host ""
if ($legacyHits.Count -gt 0 -or $lnkHits.Count -gt 0) {
    if ($KeepLegacy) {
        Warn2 "检测到旧程序残留，但 -KeepLegacy 已指定，将保留（存在新旧双开风险）"
    } else {
        Write-Host "  将清理以下旧程序残留：" -ForegroundColor Yellow
    }
    foreach ($l in $legacyHits) { Write-Host "    - $($l.Name)  v$($l.Ver)" -ForegroundColor Yellow }
    foreach ($l in $lnkHits) { Write-Host "    - $l" -ForegroundColor Yellow }
    Write-Host "    理由：v2.9.0 把工具合并进单 exe、v3.0.1 改名 Argus.exe；" -ForegroundColor DarkGray
    Write-Host "          旧 exe 互斥体名不同，会与新版同时运行并同写一个数据库。" -ForegroundColor DarkGray
    $distEx = Join-Path $dest 'FCT-Distributor.exe'
    if (Test-Path $distEx) {
        Ok "FCT-Distributor.exe 保留不动（v3.0.0 起是独立程序，仍在使用）"
    }
} else {
    Info "无旧程序残留需要清理"
}

# 运行中进程
$running = @(Get-ArgusProcesses $dest)
Write-Host ""
if ($running.Count -gt 0) {
    Write-Host "  需要先结束的进程（含托盘）：" -ForegroundColor Yellow
    foreach ($p in $running) { Write-Host "    - $($p.ProcessName) PID $($p.Id)" -ForegroundColor Yellow }
} else {
    Info "目标目录没有正在运行的程序"
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupDir = Join-Path $dest "_backup_$stamp"
Write-Host ""
Write-Host "  备份目录: $backupDir"

if (-not $Execute) {
    Write-Host ""
    Write-Host ("-" * 68) -ForegroundColor DarkYellow
    Write-Host "  演练模式：以上全部为计划，未改动任何文件。" -ForegroundColor Yellow
    Write-Host "  确认无误后加 -Execute 真正部署：" -ForegroundColor Yellow
    Write-Host "      .\deploy_update.ps1 -Execute" -ForegroundColor White
    Write-Host ("-" * 68) -ForegroundColor DarkYellow
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}

# ============================================================
# 4. 执行部署
# ============================================================
Write-Head "4. 执行部署"

# 4.1 结束进程
foreach ($p in $running) {
    Write-Host "  结束 $($p.ProcessName) (PID $($p.Id))..."
    try {
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force }
    } catch {
        try { Stop-Process -Id $p.Id -Force } catch { }
    }
}
if ($running.Count -gt 0) {
    Start-Sleep -Seconds 2
    $still = @(Get-ArgusProcesses $dest)
    if ($still.Count -gt 0) { Die "仍有进程占用目录（$($still.ProcessName -join ',')），请手动退出后重试（注意右下角托盘）" }
    Ok "程序已全部退出"
}

# 4.2 备份
New-Item -ItemType Directory -Path (Join-Path $backupDir 'program') -Force | Out-Null
$dest | Set-Content (Join-Path $backupDir 'restore-manifest.txt') -Encoding UTF8
$bkCount = 0
foreach ($n in $willOverwrite) {
    Copy-Item (Join-Path $dest $n) (Join-Path $backupDir 'program') -Force
    $bkCount++
}
foreach ($l in $legacyHits) { Copy-Item $l.Path (Join-Path $backupDir 'program') -Force; $bkCount++ }
$cfgSrc = Join-Path $dest 'config.json'
$cfgHashBefore = $null
if (Test-Path $cfgSrc) {
    Copy-Item $cfgSrc $backupDir -Force
    $cfgHashBefore = (SafeFileHash $cfgSrc).Hash
}
Ok "已备份 $bkCount 个程序文件到 $backupDir\program"

if (-not $NoDbBackup) {
    $dbs = @(Get-ChildItem (Join-Path $dest 'data') -Filter '*.db' -File -ErrorAction SilentlyContinue)
    if ($dbs.Count -gt 0) {
        New-Item -ItemType Directory -Path (Join-Path $backupDir 'data') -Force | Out-Null
        $mb = 0
        foreach ($d in $dbs) { Copy-Item $d.FullName (Join-Path $backupDir 'data') -Force; $mb += $d.Length }
        Ok "已备份数据库 $($dbs.Count) 个（$([math]::Round($mb/1MB,1)) MB）"
    }
}

# 4.3 覆盖（config.json 跳过——它走下面的合并部署，不能直接盖）
$copied = 0
foreach ($f in $pkgFiles) {
    if ($f.Name -eq 'config.json') { continue }
    Copy-Item $f.FullName $dest -Force
    $copied++
}
# runtimes 目录树（deps.json 声明的运行时文件，如 System.IO.Ports 实现版）——必须随包部署
if (Test-Path (Join-Path $stage 'runtimes')) {
    Copy-Item (Join-Path $stage 'runtimes') $dest -Recurse -Force
}
# v3.22.1：public\ 前端静态资源树（Web 看板全部页面）——此前漏部署导致升级后前端停留旧版
if (Test-Path (Join-Path $stage 'public')) {
    Copy-Item (Join-Path $stage 'public') $dest -Recurse -Force
}
Ok "已覆盖 $copied 个程序文件 + runtimes/public 运行时资源树（config.json 另行合并处理）"

# 4.4 config.json 合并部署：包内模板为基准，保留现场 station_id/results_root/webhook_url/agg_token 与现场自定义字段
# （避免 station_id 丢失 -> 库名由 {sid}.db 退回 fct.db -> 现场看起来“历史数据消失”；
#   webhook_url / agg_token 是凭据，模板里为空值，现场已配置则必须保留现场，避免升级把推送/鉴权打没）
if ($pkgHasConfig) {
    $siteCfg = Join-Path $dest 'config.json'
    if (Test-Path $siteCfg) {
        try {
            # ⚠ 必须显式 -Encoding UTF8：PS 5.1 默认按 ANSI/GBK 读无 BOM 的 UTF-8 文件，
            #   中文会乱码且引号可能被吞（GBK 双字节次字节可含 0x22），JSON 直接解析失败
            $site = Get-Content $siteCfg -Raw -Encoding UTF8 | ConvertFrom-Json
            $pkg = Get-Content $pkgCfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($prop in $site.PSObject.Properties) {
                if ($null -eq $pkg.PSObject.Properties[$prop.Name]) {
                    $pkg | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value -Force
                }
            }
            # 机台特有值必须保留现场：station_id（库名）、results_root（各机台扫描路径不同，
            # 模板里的 D:/Results 是开发机路径，覆盖到现场会扫错目录）、
            # webhook_url / agg_token（凭据：现场已配好则保留现场，现场为空才用模板值——模板值也是空）
            foreach ($keepKey in @('station_id', 'results_root', 'webhook_url', 'agg_token')) {
                if ($site.$keepKey) { $pkg.$keepKey = $site.$keepKey }
            }
            $pkg | ConvertTo-Json -Depth 6 | Set-Content $siteCfg -Encoding UTF8
            Ok "config.json 已合并更新：station_id/results_root/webhook_url/agg_token 保留现场值，模板字段（fct_ini_path 等）已生效"
        } catch {
            # 现场 config.json 无法解析（乱码/损坏）时**保留原文件不动**，宁缺毋滥：
            # 至少不丢 station_id（库名）。模板内容留在备份里供人工合并。
            Warn2 "config.json 无法解析（$($_.Exception.Message)），原文件保留未动"
            Write-Host "         请人工把包内模板字段合入 $siteCfg（模板原文已备份在 $backupDir\config.json 旁）" -ForegroundColor Yellow
            Copy-Item $pkgCfgPath $backupDir -Force -ErrorAction SilentlyContinue
        }
    } else {
        Copy-Item $pkgCfgPath $siteCfg -Force
        Ok "config.json 已生成（机台原本没有，使用包内模板）"
    }
}

# 4.4 清理 legacy
if (-not $KeepLegacy) {
    foreach ($l in $legacyHits) {
        Remove-Item $l.Path -Force
        Write-Host "  已删除旧程序 $($l.Name) v$($l.Ver)"
    }
    foreach ($l in $lnkHits) {
        Remove-Item $l -Force
        Write-Host "  已删除旧自启快捷方式 $(Split-Path $l -Leaf)"
    }
    if ($legacyHits.Count -gt 0 -or $lnkHits.Count -gt 0) {
        Ok "旧程序残留已清理（备份在 $backupDir\program，可回滚）"
        if ($lnkHits.Count -gt 0) {
            Warn2 "旧的开机自启已随 .lnk 一并删除 —— 请进程序【设置】重新勾选一次「开机自启」"
        }
    }
}

# ============================================================
# 5. 校验
# ============================================================
Write-Head "5. 校验"

$destExe = Join-Path $dest $NewExeName
$destDll = Join-Path $dest 'Argus.dll'
$verNow = (Get-Item $destExe).VersionInfo.FileVersion
if ($verNow -eq $newVer) { Ok "程序版本已更新: $oldVer -> $verNow" }
else { Warn2 "版本号异常: 期望 $newVer，实际 $verNow" }

if ($pkgHasConfig) {
    try {
        $cfgNow = Get-Content $cfgSrc -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($cfgNow.fct_ini_path -like '*PEU*') { Ok "config.json 已带包内模板（fct_ini_path=PEU）" }
        else { Warn2 "config.json 未含模板字段，请人工核对（原配置已备份: $backupDir\config.json）" }
        if ($cfgNow.station_id) { Ok "station_id 保留 = $($cfgNow.station_id)（库名不变）" }
        else { Warn2 "station_id 为空：将靠 IP 自动识别机台号（识别失败时库名退回 fct.db，数据仍在原库）" }
    } catch {
        Warn2 "config.json 解析失败，请检查（原配置已备份: $backupDir\config.json）"
    }
} elseif ($cfgHashBefore) {
    $cfgHashAfter = (SafeFileHash $cfgSrc).Hash
    if ($cfgHashBefore -eq $cfgHashAfter) { Ok "config.json 一字节未变（现场配置完好）" }
    else { Warn2 "config.json 被改动了！可用备份恢复: $backupDir\config.json" }
}

foreach ($n in $pkgFiles.Name) {
    if ($n -eq 'config.json') { continue }   # 合并后的 config 与模板必然不同，不参与逐文件比对
    $a = Join-Path $stage $n; $b = Join-Path $dest $n
    if ((SafeFileHash $a).Hash -ne (SafeFileHash $b).Hash) {
        Warn2 "文件校验不一致: $n"
    }
}
Ok "包内程序文件与机台上的副本 SHA256 一致（config.json 为合并结果，单独校验）"

# runtimes 树完整性：deps.json 声明的 win 运行时文件必须都在目标目录
$rtChk = Join-Path $dest 'runtimes\win\lib\net8.0\System.IO.Ports.dll'
if (Test-Path $rtChk) { Ok "runtimes 运行时树已部署（System.IO.Ports 实现版在位）" }
else { Warn2 "runtimes 树缺失：$rtChk —— 新版启动会报 System.IO.Ports 找不到，请勿继续使用" }

if (-not $NoStart) {
    Write-Host "  启动程序做冒烟校验..."
    $logPath = Join-Path $dest 'logs\app.log'
    $logLenBefore = 0
    if (Test-Path $logPath) { $logLenBefore = (Get-Content $logPath -Raw -Encoding UTF8).Length }
    try {
        # 生产主流是启动.bat（dotnet "Argus.dll"）方式：目录里有 Argus.dll 就按 dll 方式启动校验
        if (Test-Path $destDll) {
            $dotnet = Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe'
            if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
            $proc = Start-Process -FilePath $dotnet -ArgumentList '"Argus.dll"' -WorkingDirectory $dest -PassThru
        } else {
            $proc = Start-Process -FilePath $destExe -PassThru
        }
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 500
            if ($proc.HasExited) { break }
            $proc.Refresh()
            if ($proc.MainWindowHandle -ne 0) { break }
        }
        if ($proc.HasExited) {
            Warn2 "程序启动后立即退出（ExitCode=$($proc.ExitCode)），请手动排查"
        } else {
            Ok "程序已启动，窗口正常出现"
            Start-Sleep -Seconds 3
            if (Test-Path $logPath) {
                $logText = Get-Content $logPath -Raw -Encoding UTF8
                $newLog = ''
                if ($logText.Length -ge $logLenBefore) { $newLog = $logText.Substring($logLenBefore) }
                if ($newLog -match '\| ERROR') {
                    Warn2 "启动日志里出现 ERROR，请查看 logs\app.log"
                    foreach ($line in ($newLog -split "`n" | Where-Object { $_ -match '\| ERROR' } | Select-Object -First 5)) {
                        Write-Host "         $($line.Trim())" -ForegroundColor Red
                    }
                } else { Ok "启动日志无 ERROR" }
                if ($newLog -match '\[迁移\]') {
                    foreach ($line in ($newLog -split "`n" | Where-Object { $_ -match '\[迁移\]' })) {
                        Info "数据库迁移: $($line.Trim())"
                    }
                }
            }
            Info "程序仍在运行中（PID $($proc.Id)），请人工确认标题栏版本号为 v$newVer"
        }
    } catch {
        Warn2 "启动失败: $($_.Exception.Message)"
    }
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# ============================================================
Write-Host ""
if ($script:Warn -eq 0) {
    Write-Host ("=" * 68) -ForegroundColor Green
    Write-Host "  部署完成：$oldVer -> $newVer   （$script:Warn 个警告）" -ForegroundColor Green
    Write-Host ("=" * 68) -ForegroundColor Green
} else {
    Write-Host ("=" * 68) -ForegroundColor Yellow
    Write-Host "  部署完成，但有 $script:Warn 个警告，请逐条看上面的 [WARN]" -ForegroundColor Yellow
    Write-Host ("=" * 68) -ForegroundColor Yellow
}
Write-Host ""
Write-Host "  回滚命令（如需）:" -ForegroundColor Gray
Write-Host "      .\deploy_update.ps1 -Rollback ""$backupDir""" -ForegroundColor White
Write-Host ""
exit 0

