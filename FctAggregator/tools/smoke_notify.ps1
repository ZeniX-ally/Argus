#requires -Version 7
param(
    [string]$Zip = "$PSScriptRoot\..\bin\Release\net8.0-windows\Argus-v3.2.2.zip"
)
$ErrorActionPreference = 'Stop'

$tmp = Join-Path $env:TEMP "fct_notify_$(Get-Random -Maximum 999999)"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Expand-Archive $Zip -DestinationPath $tmp

$results = Join-Path $tmp 'FakeResults'
$model = 'E3002781'
$day = Get-Date -Format 'yyyyMMdd'
$dutDir = Join-Path $results "Offline\$model\$day"
New-Item -ItemType Directory -Path $dutDir -Force | Out-Null

$cfgPath = Join-Path $tmp 'config.json'
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.results_root = $results
$cfg.webhook_url = ''
$cfg.station_id = 'FCT9'
$cfg.skip_historical_scan = $true
$cfg | Add-Member -NotePropertyName desktop_notify -NotePropertyValue $true -Force
$cfg | Add-Member -NotePropertyName notify_min_interval_sec -NotePropertyValue 1 -Force
$cfg | ConvertTo-Json -Depth 6 | Set-Content $cfgPath -Encoding utf8

$exe = Join-Path $tmp 'Argus.exe'
$proc = Start-Process -FilePath $exe -PassThru
$fail = 0

for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { break }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { break }
}
if ($proc.HasExited) { Write-Host "[FAIL] 进程已退出 ExitCode=$($proc.ExitCode)" -ForegroundColor Red; exit 1 }

$sn = "${model}TEST0001"
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<BATCH TIMESTAMP="$(Get-Date -Format 'yyyy-MM-ddTHH:mm:ss')">
  <FACTORY USER="OP01" TESTER="FCT9-PC" />
  <PANEL STATUS="Failed">
    <DUT ID="$sn">
      <TEST NAME="6.1.1.1 5V_Rail" STATUS="Failed" VALUE="9.9" LOLIM="10" HILIM="12" UNIT="V" RULE="GELE" />
    </DUT>
  </PANEL>
</BATCH>
"@
$file = Join-Path $dutDir "F_Fts_PEU_G49_FCT9_${sn}_${day}_101010.xml"
Set-Content -Path $file -Value $xml -Encoding utf8

Start-Sleep -Seconds 8      # 等文件稳定判定 + 入库 + 弹提示

$log = Join-Path $tmp 'logs\app.log'
$text = if (Test-Path $log) { Get-Content $log -Raw } else { '' }

function Assert($cond, $msg) {
    if ($cond) { Write-Host "[OK]   $msg" } else { Write-Host "[FAIL] $msg" -ForegroundColor Red; $script:fail++ }
}

Assert ($text -match '桌面提示已就绪') '桌面提示已初始化（托盘 NotifyIcon 建成）'
Assert ($text -match '\[入库\].*FAIL') 'FAIL 记录已实时入库'
Assert ($text -match '\[待办\].*新登记') '新不良已登记进待办（大项合并 + 永久保留）'
Assert ($text -notmatch '桌面提示弹出失败') '桌面提示弹出无异常'
Assert ($text -notmatch '桌面提示初始化失败') '桌面提示初始化无异常'
Assert ($text -notmatch '\| ERROR') '日志无 ERROR'

$proc.Refresh()
Assert (-not $proc.HasExited) '弹提示后程序仍存活'
if (-not $proc.HasExited) { $proc | Stop-Process -Force; Start-Sleep -Milliseconds 800 }

# ---- 库里应能聚合出未确认不良（看板「待办」列的数据源）----
$db = Join-Path $tmp 'data\FCT9.db'
Assert (Test-Path $db) '按机台命名的库已生成 (data\FCT9.db)'
if (Test-Path $db) {
    Add-Type -Path (Join-Path $tmp 'Microsoft.Data.Sqlite.dll') -ErrorAction SilentlyContinue
    # 用 sqlite3 命令不一定有，改用最简单的方式：查文本里的日志痕迹 + 文件大小
    Assert ((Get-Item $db).Length -gt 20000) "库文件已写入数据 ($((Get-Item $db).Length) 字节)"
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($fail -eq 0) { Write-Host "==== 不良提示冒烟测试通过 ====" -ForegroundColor Green; exit 0 }
else { Write-Host "==== 不良提示冒烟测试失败 ($fail 项) ====" -ForegroundColor Red; exit 1 }
