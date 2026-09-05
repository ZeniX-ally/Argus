#requires -Version 7
param(
    [string]$Zip = "$PSScriptRoot\..\bin\Release\net8.0-windows\Argus-v3.0.1.zip",
    [string]$SeedDb = "$PSScriptRoot\..\dist\data\fct.db",
    [string]$OutDir = "$PSScriptRoot\..\.snap"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$tmp = Join-Path $env:TEMP "fct_snap_$(Get-Random -Maximum 999999)"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Expand-Archive $Zip -DestinationPath $tmp
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$results = Join-Path $tmp 'FakeResults'
New-Item -ItemType Directory -Path (Join-Path $results 'Offline\E3002781\20260730') -Force | Out-Null

$cfgPath = Join-Path $tmp 'config.json'
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.results_root = $results
$cfg.webhook_url = 'https://example.invalid/hook'   # 只为让「飞书已配置」胶囊亮起来
$cfg.station_id = ''
$cfg.skip_historical_scan = $true
$cfg | ConvertTo-Json -Depth 6 | Set-Content $cfgPath -Encoding utf8

if (Test-Path $SeedDb) {
    New-Item -ItemType Directory -Path (Join-Path $tmp 'data') -Force | Out-Null
    Copy-Item $SeedDb (Join-Path $tmp 'data\fct.db') -Force
    Write-Host "[i] 已灌入种子数据库: $SeedDb"
}

$exe = Join-Path $tmp 'Argus.exe'
$proc = Start-Process -FilePath $exe -PassThru

$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { throw "进程已退出 ExitCode=$($proc.ExitCode)" }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $h = $proc.MainWindowHandle; break }
}
if ($h -eq [IntPtr]::Zero) { $proc | Stop-Process -Force; throw '主窗口没出现' }
Write-Host "[i] 窗口标题: $($proc.MainWindowTitle)"
Start-Sleep -Seconds 3            # 等定时器把统计/待办角标刷出来

function Snap([string]$file) {
    [Win32]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 700
    $r = New-Object Win32+RECT
    [Win32]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "[OK] $file"
}

Snap (Join-Path $OutDir 'page1.png')
# 2~5 = 主程序页，6~8 = 三个内嵌工具页（v3.0.0：数据分发已独立）
foreach ($n in 2, 3, 4, 5, 6, 7, 8) {
    [Win32]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait("^$n")
    Start-Sleep -Milliseconds 900
    Snap (Join-Path $OutDir "page$n.png")
}

$proc | Stop-Process -Force
Start-Sleep -Milliseconds 600
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "`n截图已保存到 $OutDir"
