#requires -Version 7
<#
  GUI 冒烟测试：解压完整包到临时目录 -> 启动 -> 断言【主窗口真的存在且标题正确】-> 关掉
  只看"进程还活着"是不够的：v2.9.4 曾出现进程秒退(0xC0000005)，也可能出现进程活着但窗口没建起来。
#>
param(
    # 路径自己推（仓库改名 G4_9-QA -> Argus 后，写死绝对路径会静静失效）
    [string]$Zip = "$PSScriptRoot\..\bin\Release\net8.0-windows\Argus-v3.0.1.zip",
    [string]$ExpectTitle = 'Argus'
)
$ErrorActionPreference = 'Stop'

$tmp = Join-Path $env:TEMP "fct_gui_$(Get-Random -Maximum 999999)"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Expand-Archive $Zip -DestinationPath $tmp

# 指向空的假 results，避免扫真实 D:\Results；关掉飞书
$cfgPath = Join-Path $tmp 'config.json'
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
$cfg.results_root = Join-Path $tmp 'FakeResults'
$cfg.webhook_url = ''
$cfg | ConvertTo-Json -Depth 6 | Set-Content $cfgPath
New-Item -ItemType Directory -Path $cfg.results_root -Force | Out-Null

$exe = Join-Path $tmp 'Argus.exe'
$proc = Start-Process -FilePath $exe -PassThru
$fail = 0

# 等窗口出现（最多 20s）
$title = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { break }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne 0) { $title = $proc.MainWindowTitle; break }
}

if ($proc.HasExited) {
    Write-Host "[FAIL] 进程已退出，ExitCode=$($proc.ExitCode)" -ForegroundColor Red
    if ($proc.ExitCode -eq -1073741819) { Write-Host "       (0xC0000005 访问冲突 —— 通常是构造期空引用)" }
    $fail++
} elseif ($null -eq $title -or $proc.MainWindowHandle -eq 0) {
    Write-Host "[FAIL] 进程活着但主窗口没建起来（MainWindowHandle=0）" -ForegroundColor Red
    $fail++
} else {
    Write-Host "[OK]   主窗口已出现: `"$title`""
    if ($title -like "*$ExpectTitle*") {
        Write-Host "[OK]   标题含预期版本号 ($ExpectTitle)"
    } else {
        Write-Host "[FAIL] 标题不含预期版本号 ($ExpectTitle)" -ForegroundColor Red
        $fail++
    }
    Start-Sleep -Seconds 3          # 多停一会，让定时器/后台线程跑起来，暴露延迟崩溃
    $proc.Refresh()
    if ($proc.HasExited) {
        Write-Host "[FAIL] 窗口出现后又崩了，ExitCode=$($proc.ExitCode)" -ForegroundColor Red
        $fail++
    } else {
        Write-Host "[OK]   运行 3 秒后仍存活（无延迟崩溃）"
    }
}

if (-not $proc.HasExited) { $proc | Stop-Process -Force; Start-Sleep -Milliseconds 800 }

# 检查日志里有没有 ERROR
$log = Join-Path $tmp 'logs\app.log'
if (Test-Path $log) {
    $errs = Select-String -Path $log -Pattern '\| ERROR' -ErrorAction SilentlyContinue
    if ($errs) {
        Write-Host "[FAIL] 日志里有 ERROR：" -ForegroundColor Red
        $errs | ForEach-Object { "       $($_.Line)" }
        $fail++
    } else {
        Write-Host "[OK]   日志无 ERROR"
    }
    if (Test-Path (Join-Path $tmp 'data')) {
        $db = Get-ChildItem (Join-Path $tmp 'data') -Filter *.db -ErrorAction SilentlyContinue
        if ($db) { Write-Host "[OK]   数据库已创建: $($db.Name) ($($db.Length) 字节)" }
    }
} else {
    Write-Host "[FAIL] 没有生成日志" -ForegroundColor Red
    $fail++
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
if ($fail -eq 0) { Write-Host "==== GUI 冒烟测试通过 ====" -ForegroundColor Green; exit 0 }
else { Write-Host "==== GUI 冒烟测试失败 ($fail 项) ====" -ForegroundColor Red; exit 1 }
