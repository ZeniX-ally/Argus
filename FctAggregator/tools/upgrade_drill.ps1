#requires -Version 7
param(
    [string]$OldZip = "$PSScriptRoot\..\..\Releases\aggregator\FCT-Aggregator-v2.4.0.zip",
    [string]$NewUpdateZip = "$PSScriptRoot\..\bin\Release\net8.0-windows\Argus-v3.2.2-update.zip",
    [switch]$FullScan,
    [switch]$KeepTemp
)
$ErrorActionPreference = 'Stop'
$fail = 0
function Assert($cond, $msg) {
    if ($cond) { Write-Host "[OK]   $msg" } else { Write-Host "[FAIL] $msg" -ForegroundColor Red; $script:fail++ }
}
function Start-AndWait([string]$exe, [int]$sec) {
    $p = Start-Process -FilePath $exe -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if ($p.HasExited) { break }
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { break }
    }
    if ($p.HasExited) { throw "进程启动即退出 ExitCode=$($p.ExitCode)" }
    Start-Sleep -Seconds $sec
    $p.Refresh()
    if (-not $p.HasExited) { $p | Stop-Process -Force }
    Start-Sleep -Milliseconds 1200
    return $p
}

$tmp = Join-Path $env:TEMP "fct_drill_$(Get-Random -Maximum 999999)"
$station = Join-Path $tmp 'station'
New-Item -ItemType Directory -Path $station -Force | Out-Null

Write-Host "===== 1. 铺「机台现状」：$([IO.Path]::GetFileName($OldZip)) =====" -ForegroundColor Cyan
Expand-Archive $OldZip -DestinationPath $station
$results = Join-Path $tmp 'FakeResults'
$model = 'E3002781'
$day = Get-Date -Format 'yyyyMMdd'
$dutDir = Join-Path $results "Offline\$model\$day"
New-Item -ItemType Directory -Path $dutDir -Force | Out-Null

$cfgPath = Join-Path $station 'config.json'
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.results_root = $results
$cfg.webhook_url = ''
$cfg.station_id = 'FCT9'
$cfg.skip_historical_scan = (-not $FullScan)
$cfg | Add-Member -NotePropertyName todo_scan_days -NotePropertyValue 45 -Force
$cfg | Add-Member -NotePropertyName '_现场备注' -NotePropertyValue '这行是现场加的，升级后必须还在' -Force
$cfg | ConvertTo-Json -Depth 6 | Set-Content $cfgPath -Encoding utf8
$cfgHashBefore = (Get-FileHash $cfgPath -Algorithm SHA256).Hash
Write-Host "  config.json SHA256(升级前) = $($cfgHashBefore.Substring(0,16))..."

$oldExe = Join-Path $station 'Argus.exe'
if (-not (Test-Path $oldExe)) {
    $oldExe = Join-Path $station 'FCT-Aggregator.exe'
    if (-not (Test-Path $oldExe)) {
        throw "「机台现状」里找不到可执行文件（既无 Argus.exe 也无 FCT-Aggregator.exe）: $station"
    }
}
$newExe = Join-Path $station 'Argus.exe'
$renamed = ($oldExe -ne $newExe)
$verBefore = (Get-Item $oldExe).VersionInfo.FileVersion
Write-Host "  机台现有版本 = $verBefore ($(Split-Path $oldExe -Leaf))"
if ($renamed) { Write-Host "  ⚠ 老版可执行文件名与新版不同，升级后会共存（本演练会校验这一点）" -ForegroundColor Yellow }
Write-Host "===== 2. 启动旧版并丢 2 条 FAIL 入库 =====" -ForegroundColor Cyan
$p = Start-Process -FilePath $oldExe -PassThru
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; if ($p.HasExited) { break }; $p.Refresh(); if ($p.MainWindowHandle -ne 0) { break } }
if ($p.HasExited) { throw "旧版启动即退出 ExitCode=$($p.ExitCode)" }
foreach ($n in 1, 2) {
    $sn = "${model}DRILL000$n"
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<BATCH TIMESTAMP="$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ss')">
  <FACTORY USER="OP01" TESTER="FCT9-PC" />
  <PANEL STATUS="Failed">
    <DUT ID="$sn">
      <TEST NAME="6.1.1.$n 5V_Rail" STATUS="Failed" VALUE="9.$n" LOLIM="10" HILIM="12" UNIT="V" RULE="GELE" />
    </DUT>
  </PANEL>
</BATCH>
"@
    Set-Content -Path (Join-Path $dutDir "F_Fts_PEU_G49_FCT9_${sn}_${day}_10101$n.xml") -Value $xml -Encoding utf8
    Start-Sleep -Seconds 3
}
Start-Sleep -Seconds 5
$p.Refresh(); if (-not $p.HasExited) { $p | Stop-Process -Force }
Start-Sleep -Milliseconds 1500

$db = Join-Path $station 'data\FCT9.db'
Assert (Test-Path $db) '旧版已建库 data\FCT9.db'

Write-Host "===== 3. 再插 2 条维修记录（模拟现场历史）=====" -ForegroundColor Cyan
$py = @"
import sqlite3
c = sqlite3.connect(r'$db')
c.execute('''CREATE TABLE IF NOT EXISTS maintenance_records (
  id INTEGER PRIMARY KEY AUTOINCREMENT, station_id TEXT, equipment_model TEXT, equipment_sn TEXT,
  fail_item TEXT, fail_reason TEXT, severity TEXT, status TEXT, resolver TEXT, resolution TEXT,
  notes TEXT, created_at TEXT, updated_at TEXT)''')
for i in (1, 2):
    c.execute('''INSERT INTO maintenance_records
      (station_id,equipment_model,equipment_sn,fail_item,fail_reason,severity,status,resolver,resolution,notes,created_at,updated_at)
      VALUES ('FCT9','E3002781','SN-DRILL-%d','演练故障项 %d','升级演练用','major','open','张工','','演练数据',
              '2026-07-01 08:00:00','2026-07-01 08:00:00')''' % (i, i))
c.commit()
"@
$py | Set-Content (Join-Path $tmp 'seed.py') -Encoding utf8
python (Join-Path $tmp 'seed.py')

$before = python (Join-Path $PSScriptRoot 'upgrade_drill_db.py') $db | ConvertFrom-Json
Write-Host "  升级前各表行数: $($before | ConvertTo-Json -Compress)"

Write-Host "===== 4. 用更新包整包覆盖 =====" -ForegroundColor Cyan
$updTmp = Join-Path $tmp 'upd'
Expand-Archive $NewUpdateZip -DestinationPath $updTmp

# v3.2.2 起包内带 config.json 模板：**先**合并现场 config（此时读到的是现场原文件），
# 再覆盖程序文件（覆盖时排除 config.json）——顺序反了会把现场 config 冲掉再合并（= 没合并）
$pkgCfg = Join-Path $updTmp 'config.json'
if (Test-Path $pkgCfg) {
    $site = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $pkg  = Get-Content $pkgCfg -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($prop in $site.PSObject.Properties) {
        if ($null -eq $pkg.PSObject.Properties[$prop.Name]) {
            $pkg | Add-Member -NotePropertyName $prop.Name -NotePropertyValue $prop.Value -Force
        }
    }
    # 机台特有值保留现场：station_id（库名）、results_root（各机台路径不同）、
    # webhook_url / agg_token（凭据：现场已配好则保留现场，现场为空才用模板值——模板值也是空）
    foreach ($keepKey in @('station_id', 'results_root', 'webhook_url', 'agg_token')) {
        if ($site.$keepKey) { $pkg.$keepKey = $site.$keepKey }
    }
    $pkg | ConvertTo-Json -Depth 6 | Set-Content $cfgPath -Encoding UTF8
    Write-Host "  config.json 已按部署规则合并（station_id/results_root/webhook_url 保留现场，模板字段生效）"
}
# 覆盖程序文件（config.json 已合并完毕，跳过它；runtimes 树要带上）
Get-ChildItem $updTmp -File | Where-Object { $_.Name -ne 'config.json' } |
    Copy-Item -Destination $station -Force
if (Test-Path (Join-Path $updTmp 'runtimes')) {
    Copy-Item (Join-Path $updTmp 'runtimes') $station -Recurse -Force
}
$overCnt = (Get-ChildItem $updTmp -File | Where-Object { $_.Name -ne 'config.json' }).Count
Write-Host "  已覆盖 $overCnt 个程序文件 + runtimes 树"

Write-Host "===== 5. 启动新版（跑迁移 + 待办同步）=====" -ForegroundColor Cyan
# 用 $newExe：跨 v3.0.1 升级时 $oldExe 是残留的旧程序，启动它等于根本没升级
$null = Start-AndWait $newExe 10

Write-Host "===== 6. 校验 =====" -ForegroundColor Cyan
$cfgNow = Get-Content $cfgPath -Raw | ConvertFrom-Json
# v3.2.2 起更新包带 config.json 模板，部署为「合并」：现场 station_id/自定义字段保留、模板字段生效
Assert ($cfgNow.station_id -eq 'FCT9') 'config.json 合并后 station_id 保留（库名不变）'
Assert ($cfgNow.'_现场备注' -eq '这行是现场加的，升级后必须还在') 'config.json 里的现场自定义字段仍在'
Assert ($cfgNow.todo_scan_days -eq 30) 'todo_scan_days 采用包内模板值 30（模板优先，现场独有字段才保留）'
Assert ($cfgNow.fct_ini_path -eq 'C:\FTS\Apps\PEU\Cfg\FCT.ini') 'fct_ini_path 已按包内模板更新为 PEU'
Assert ($cfgNow.webhook_url -eq '') 'webhook_url 保持空：包内模板不夹带真实凭据（现场为空则保持空，不泄露 token）'

$after = python (Join-Path $PSScriptRoot 'upgrade_drill_db.py') $db | ConvertFrom-Json
Write-Host "  升级后各表行数: $($after | ConvertTo-Json -Compress)"
foreach ($t in 'test_records', 'maintenance_records') {
    $b = $before.$t; $a = $after.$t
    Assert ($a -ge $b -and $b -gt 0) "$t 一行没少（$b -> $a）"
}
foreach ($t in 'todo_items', 'app_meta') {
    $had = $null -ne $before.$t
    $note = if ($had) { '升级前已存在' } else { '升级前**不存在**，由新版自动建好' }
    Assert ($null -ne $after.$t) "表 $t 已就位（$note）"
}
$verAfter = (Get-Item $newExe).VersionInfo.FileVersion
Assert ($verAfter -eq '3.2.2.0' -and $verAfter -ne $verBefore) "程序已真的被更新（$verBefore -> $verAfter）"
# 跨 v3.0.1（exe 改名）升级时的已知残留：更新包只添加/覆盖同名文件，不会删旧 exe。
# 旧 exe 留在原地 + 互斥体名不同（FCT_Aggregator_SingleInstance vs Argus_SingleInstance）
# -> 新旧两个程序可同时运行并同写一个库。现场必须手动清理。
if ($renamed) {
    $stale = Test-Path $oldExe
    Write-Host "[!!]   旧程序残留检查：$(Split-Path $oldExe -Leaf) $(if($stale){'仍在（v'+$verBefore+'）'}else{'已不存在'})" -ForegroundColor $(if ($stale) { 'Yellow' } else { 'Green' })
    if ($stale) {
        Write-Host "       现场升级后必须手动删除它及旧的工具 exe，并清掉「启动」文件夹里的旧 .lnk，" -ForegroundColor Yellow
        Write-Host "       否则可能与新版 Argus.exe 同时运行、同写一个 db（互斥体名不同，拦不住）。" -ForegroundColor Yellow
    }
}
# 待办登记：只有跑了历史扇描才会把**旧**不良并进来（新不良走实时那条路）
if ($FullScan) {
    Assert ($after.todo_items -gt 0) "历史不良已登记进待办（todo_items=$($after.todo_items)）"
} else {
    Write-Host "[--]   todo_items=$($after.todo_items)：skip_historical_scan=true 下不同步历史不良，属预期" -ForegroundColor DarkGray
    Write-Host "       ⚠ 现场若有机台把 skip_historical_scan 设成了 true，升级后要到【调试】页点一次【待办同步】" -ForegroundColor Yellow
}
Assert (Test-Path (Join-Path $station 'app_icon.ico')) 'app_icon.ico 已随包落到机台（否则托盘图标退回系统灰图）'
$log = Get-Content (Join-Path $station 'logs\app.log') -Raw -ErrorAction SilentlyContinue
Assert ($log -notmatch '\| ERROR') '新版启动日志无 ERROR'

if ($KeepTemp) { Write-Host "`n演练目录保留: $tmp" -ForegroundColor Yellow }
else { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
if ($fail -eq 0) { Write-Host "==== 升级演练通过：覆盖不会动配置与历史数据 ====" -ForegroundColor Green; exit 0 }
else { Write-Host "==== 升级演练失败（$fail 项）====" -ForegroundColor Red; exit 1 }
