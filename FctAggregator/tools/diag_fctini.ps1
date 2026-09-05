$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

Write-Host "================ FCT.ini 诊断 ================" -ForegroundColor Cyan
Write-Host "机器: $env:COMPUTERNAME   用户: $env:USERNAME   时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host ""

$cfgCandidates = @(
    (Join-Path $PSScriptRoot 'config.json'),
    (Join-Path (Get-Location) 'config.json'),
    'C:\Argus\config.json'
)
$cfgPath = $cfgCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
$configured = ''
if ($cfgPath) {
    Write-Host "[1] config.json = $cfgPath" -ForegroundColor Green
    try {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        $configured = $cfg.fct_ini_path
        Write-Host "    fct_ini_path = `"$configured`""
        Write-Host "    results_root = `"$($cfg.results_root)`""
        Write-Host "    station_id   = `"$($cfg.station_id)`"$(if(-not $cfg.station_id){'  (留空 = 按 IP 自动识别)'})"
    } catch { Write-Host "    [X] config.json 解析失败: $($_.Exception.Message)" -ForegroundColor Red }
} else {
    Write-Host "[1] 没找到 config.json（把本脚本放到程序目录里再跑，或手工确认）" -ForegroundColor Yellow
}
Write-Host ""

Write-Host "[2] 逐个检查候选路径（程序内置顺序）" -ForegroundColor Cyan
$cands = @()
if ($configured) { $cands += $configured }
$cands += @('C:\FTS\Apps\PEU\Cfg\FCT.ini', 'D:\FTS\Apps\PEU\Cfg\FCT.ini', 'C:\FTS\Cfg\FCT.ini')
foreach ($p in $cands) {
    $dir = Split-Path $p -Parent
    if (Test-Path $p) {
        $f = Get-Item $p
        Write-Host "    [OK] $p" -ForegroundColor Green
        Write-Host "         大小 $($f.Length) 字节，改动于 $($f.LastWriteTime)"
    } elseif (Test-Path $dir) {
        Write-Host "    [X ] $p  <-- 文件不在，但目录存在" -ForegroundColor Yellow
        $inis = @(Get-ChildItem $dir -Filter *.ini -ErrorAction SilentlyContinue)
        if ($inis) { Write-Host "         该目录下的 ini: $($inis.Name -join ', ')" -ForegroundColor Yellow }
        else { Write-Host "         该目录下没有任何 .ini" }
    } else {
        Write-Host "    [X ] $p  <-- 目录都不存在: $dir" -ForegroundColor DarkGray
    }
}
Write-Host ""

Write-Host "[3] 全盘搜 FCT.ini（每个盘限时，可能要十几秒）..." -ForegroundColor Cyan
$hits = @()
foreach ($drive in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ne $null })) {
    $root = "$($drive.Name):\"
    try {
        $found = Get-ChildItem -Path $root -Filter 'FCT.ini' -Recurse -File -ErrorAction SilentlyContinue -Force |
                 Select-Object -First 10
        foreach ($h in $found) { $hits += $h; Write-Host "    [找到] $($h.FullName)  ($($h.Length) 字节, $($h.LastWriteTime))" -ForegroundColor Green }
    } catch {}
}
if (-not $hits) {
    Write-Host "    [X] 整机没有任何叫 FCT.ini 的文件" -ForegroundColor Red
    Write-Host "        => 这台机器可能没装测试软件，或文件名不同（下面顺带找找相近的）" -ForegroundColor Yellow
    foreach ($d in 'C:\FTS', 'D:\FTS', 'C:\Program Files\FTS', 'D:\Apps') {
        if (Test-Path $d) {
            Write-Host "        $d 下的 .ini 文件:" -ForegroundColor Yellow
            Get-ChildItem $d -Filter *.ini -Recurse -File -ErrorAction SilentlyContinue |
                Select-Object -First 15 | ForEach-Object { Write-Host "          $($_.FullName)" }
        }
    }
}
Write-Host ""

$target = if ($hits) { $hits[0].FullName } elseif ($configured -and (Test-Path $configured)) { $configured } else { $null }
if ($target) {
    Write-Host "[4] 验证读取: $target" -ForegroundColor Cyan
    try {
        $fs = [System.IO.File]::Open($target, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = New-Object System.IO.StreamReader($fs)
        $text = $sr.ReadToEnd(); $sr.Close(); $fs.Close()
        Write-Host "    [OK] 共享模式读取成功，共 $($text.Split(""`n"").Count) 行" -ForegroundColor Green
        Write-Host "    ---- [Resource Name] 段（程序就靠这段出设备状态）----"
        $inSec = $false; $n = 0
        foreach ($line in $text -split "`r?`n") {
            $t = $line.Trim()
            if ($t -match '^\[(.+)\]$') { $inSec = ($matches[1] -eq 'Resource Name'); continue }
            if ($inSec -and $t -and -not $t.StartsWith(';') -and $n -lt 20) { Write-Host "      $t"; $n++ }
        }
        if ($n -eq 0) { Write-Host "      [X] 没有 [Resource Name] 段 —— 程序会认为没有设备" -ForegroundColor Red }
    } catch {
        Write-Host "    [X] 读取失败: $($_.Exception.GetType().Name) $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "        权限问题的话，试试用管理员身份运行 Argus" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "[5] 把这一行写进 config.json（注意 JSON 里反斜杠要双写）" -ForegroundColor Cyan
    Write-Host "    `"fct_ini_path`": `"$($target -replace '\\','\\')`"," -ForegroundColor Green
} else {
    Write-Host "[4] 跳过读取验证：没有可用的 FCT.ini" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "[6] 顺带看一眼 FTS 测试软件是否在跑（能借它的路径反推 Cfg 位置）" -ForegroundColor Cyan
$procs = Get-Process | Where-Object { $_.ProcessName -match 'XPT|FTS|TestStand|LabVIEW' }
if ($procs) {
    foreach ($p in $procs) {
        $path = try { $p.MainModule.FileName } catch { '(拿不到路径，需管理员)' }
        Write-Host "    $($p.ProcessName) (PID $($p.Id)) -> $path"
    }
} else { Write-Host "    没有发现 XPT/FTS/TestStand/LabVIEW 进程" }
Write-Host ""
Write-Host "================ 诊断结束，把上面全部内容发回来 ================" -ForegroundColor Cyan
