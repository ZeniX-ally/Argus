#requires -Version 7
<#
  升级演练：证明「更新包覆盖上去不会动现场的配置与历史数据」。

  流程（全在临时目录，绝不碰真实机台/仓库数据）：
    1. 解压一个**旧版本完整包**当作「机台现状」，改好 config.json（写入可识别的自定义值）
    2. 启动旧版 -> 丢 2 条 FAIL 让它入库 -> 关掉
    3. 再往库里插 2 条维修记录（模拟现场攒的历史数据）
    4. 记录 config.json 的 SHA256 与各表行数
    5. 用**更新包**整包覆盖
    6. 启动新版（会跑数据库迁移与待办同步）-> 关掉
    7. 校验：config.json 一字节没动、旧数据一行没少、新表已建好

  用法：
    .\tools\upgrade_drill.ps1                       # 默认从 v2.8.0 升到当前更新包
    .\tools\upgrade_drill.ps1 -OldZip <旧完整包.zip>
#>
param(
    # 默认拿 v2.4.0 作基线：它还没有 todo_items / app_meta 两张表，
    # 能真正考到「升级时自动建新表且不动旧数据」这条路径。
    [string]$OldZip = "$PSScriptRoot\..\..\_releases\aggregator\FCT-Aggregator-v2.4.0.zip",
    [string]$NewUpdateZip = "$PSScriptRoot\..\bin\Release\net8.0-windows\Argus-v3.0.1-update.zip",
    # 默认 skip_historical_scan=true（演练跑得快）。
    # ⚠ 加上 -FullScan 则模拟现场默认配置（false）：只有这条路径会在启动时
    #   把历史不良并入待办登记表（SyncTodos 挂在「历史扇描后」）。
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
    Start-Sleep -Milliseconds 1200      # 等文件句柄释放，否则覆盖会失败
    return $p
}

$tmp = Join-Path $env:TEMP "fct_drill_$(Get-Random -Maximum 999999)"
$station = Join-Path $tmp 'station'          # 模拟机台程序目录
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
# 写入两个「现场特有」的值，升级后必须原样保留
$cfg | Add-Member -NotePropertyName todo_scan_days -NotePropertyValue 45 -Force
$cfg | Add-Member -NotePropertyName '_现场备注' -NotePropertyValue '这行是现场加的，升级后必须还在' -Force
$cfg | ConvertTo-Json -Depth 6 | Set-Content $cfgPath -Encoding utf8
$cfgHashBefore = (Get-FileHash $cfgPath -Algorithm SHA256).Hash
Write-Host "  config.json SHA256(升级前) = $($cfgHashBefore.Substring(0,16))..."

$exe = Join-Path $station 'Argus.exe'
$verBefore = (Get-Item $exe).VersionInfo.FileVersion
Write-Host "  机台现有版本 = $verBefore"
Write-Host "===== 2. 启动旧版并丢 2 条 FAIL 入库 =====" -ForegroundColor Cyan
$p = Start-Process -FilePath $exe -PassThru
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
Copy-Item "$updTmp\*" $station -Recurse -Force
Write-Host "  已覆盖 $((Get-ChildItem $updTmp -File).Count) 个文件"

Write-Host "===== 5. 启动新版（跑迁移 + 待办同步）=====" -ForegroundColor Cyan
$null = Start-AndWait $exe 10

Write-Host "===== 6. 校验 =====" -ForegroundColor Cyan
$cfgHashAfter = (Get-FileHash $cfgPath -Algorithm SHA256).Hash
Assert ($cfgHashBefore -eq $cfgHashAfter) 'config.json 一字节未变（现场配置没被覆盖）'
$cfgNow = Get-Content $cfgPath -Raw | ConvertFrom-Json
Assert ($cfgNow.'_现场备注' -eq '这行是现场加的，升级后必须还在') 'config.json 里的现场自定义字段仍在'
Assert ($cfgNow.todo_scan_days -eq 45) 'todo_scan_days=45 未被改回默认 30'

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
$verAfter = (Get-Item $exe).VersionInfo.FileVersion
Assert ($verAfter -eq '3.0.0.0' -and $verAfter -ne $verBefore) "程序已真的被更新（$verBefore -> $verAfter）"
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
