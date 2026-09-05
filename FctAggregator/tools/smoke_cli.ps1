#requires -Version 7
param([string]$Zip = "$PSScriptRoot\..\bin\Release\net8.0-windows\Argus-v3.2.2.zip")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$tmp = Join-Path $env:TEMP "fct_cli_$(Get-Random -Maximum 999999)"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Expand-Archive $Zip -DestinationPath $tmp
$exe = Join-Path $tmp 'Argus.exe'
$fail = 0
function Assert($cond, $msg) {
    if ($cond) { Write-Host "[OK]   $msg" } else { Write-Host "[FAIL] $msg" -ForegroundColor Red; $script:fail++ }
}

$help = & $exe --help 2>&1 | Out-String
Assert ($help -match 'FCT 工具套件') '--help 有输出（AttachConsole + SetOut 生效，stdout 没被吞）'
foreach ($sub in 'rank', 'tdms', 'fetch') {
    Assert ($help -match [regex]::Escape($sub)) "--help 列出子命令 $sub"
}
Assert ($help -notmatch 'distribute') '--help 里已无 distribute（数据分发已拆成独立程序）'

$fetchHelp = & $exe fetch --help 2>&1 | Out-String
Assert ($fetchHelp.Trim().Length -gt 20) "fetch --help 有输出（$($fetchHelp.Trim().Length) 字符）"
$tdmsHelp = & $exe tdms --help 2>&1 | Out-String
Assert ($tdmsHelp.Trim().Length -gt 20) "tdms --help 有输出（$($tdmsHelp.Trim().Length) 字符）"

$bad = & $exe nosuchcmd 2>&1 | Out-String
Assert ($LASTEXITCODE -ne 0) "未知子命令返回非 0 退出码（实得 $LASTEXITCODE）"
Assert ($bad -match '未知子命令') '未知子命令有明确提示'

foreach ($t in @(@{c = 'rank'; k = '排名|Rank|排行' }, @{c = 'tdms'; k = 'TDMS' })) {
    $p = Start-Process -FilePath $exe -ArgumentList $t.c -PassThru
    $title = $null
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { break }
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { $title = $p.MainWindowTitle; break }
    }
    if ($p.HasExited) {
        Assert $false "子命令 $($t.c) 窗口没起来（进程已退出 ExitCode=$($p.ExitCode)）"
    }
    else {
        Assert ($title -match $t.k) "子命令 $($t.c) 窗口标题 = `"$title`""
        $p | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 300
}

$gui = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
$helpWhileRunning = & $exe --help 2>&1 | Out-String
Assert ($helpWhileRunning -match 'FCT 工具套件') '主程序运行中 CLI 仍可用（单实例锁没误伤子命令）'

$distOut = & $exe distribute 2>&1 | Out-String
Assert ($LASTEXITCODE -ne 0) "distribute 已不是子命令，返回非 0（实得 $LASTEXITCODE）"
Assert ($distOut -match '未知子命令') 'distribute 会提示“未知子命令”（分发器已完全移出本程序）'
if (-not $gui.HasExited) { $gui | Stop-Process -Force }

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($fail -eq 0) { Write-Host '==== 子命令冒烟测试通过 ====' -ForegroundColor Green; exit 0 }
else { Write-Host "==== 子命令冒烟测试失败 ($fail 项) ====" -ForegroundColor Red; exit 1 }
