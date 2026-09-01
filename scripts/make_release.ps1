# ============================================================================
# Argus 统一发布脚本（2026-08-27）
#
# 一键完成：构建两个工程 -> 跑三个打包器 -> 统一归档纯净 zip 到 Releases\v{ver}\
#  -> 生成 RELEASE.txt 发布清单（版本/日期/SHA256/内容说明/纯净校验）。
#
# 三个打包器各司其职（均已是纯净包，webhook 置空、不含 data/logs/db/pdb）：
#   FctAggregator/tools/make_package.ps1        -> Argus-v{ver}.zip + Argus-v{ver}-update.zip
#   FctAggregator/tools/make_package_agg.ps1    -> Argus-Agg-v{ver}.zip
#   FctDistributor/tools/make_package.ps1       -> FCT-Distributor-v{ver}.zip + ...-update.zip
#
# 产物来源目录：
#   主程序  bin\Release\net8.0-windows\   （dotnet build 标准输出）
#   聚合包  FctAggregator\dist-agg\
#   分发器  FctDistributor\dist\
#
# 用法：
#   .\make_release.ps1                 # 一键构建 + 打包 + 归档（幂等，不删历史版本）
#   .\make_release.ps1 -SkipBuild      # 跳过构建（已 build 过，只打包归档）
#   .\make_release.ps1 -Version 3.5.3  # 强制归档目录版本（默认自动读 FctAggregator.csproj）
# ============================================================================
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
# PS 5.1 兼容：不用 ?? / ?: 等 7.x 语法
# SSH/CI 无控制台时静默进度条
$ProgressPreference = 'SilentlyContinue'

$repo = (Resolve-Path "$PSScriptRoot\..").Path

function Ok([string]$m)  { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Info([string]$m){ Write-Host "  [--]   $m" -ForegroundColor Gray }
function Warn2([string]$m){ Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Head([string]$t){ Write-Host ""; Write-Host ("=" * 66) -ForegroundColor Cyan; Write-Host "  $t" -ForegroundColor Cyan; Write-Host ("=" * 66) -ForegroundColor Cyan }

# ---------- 版本号：自动读主程序 csproj，可 -Version 覆盖 ----------
if (-not $Version) {
    $csproj = Join-Path $repo 'FctAggregator\FctAggregator.csproj'
    $Version = [regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
}
if (-not $Version) { throw '无法确定版本号：FctAggregator.csproj 没有 <Version>，请用 -Version 指定' }

$distVer   = [regex]::Match((Get-Content (Join-Path $repo 'FctDistributor\FctDistributor.csproj') -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
$aggProj   = Join-Path $repo 'FctAggregator\FctAggregator.csproj'
$distProj  = Join-Path $repo 'FctDistributor\FctDistributor.csproj'
$outDir    = Join-Path $repo "Releases\v$Version"

Head "Argus 统一发布 v$Version（分发器 v$distVer）"
Info "归档目录: $outDir"

# ---------- 1) 构建 ----------
if (-not $SkipBuild) {
    Head "1. 构建工程（Release）"
    Push-Location (Join-Path $repo 'FctAggregator')
    try {
        dotnet build FctAggregator.csproj -c Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'FctAggregator 构建失败' }
        Ok 'FctAggregator 构建成功'
    } finally { Pop-Location }

    Push-Location (Join-Path $repo 'FctDistributor')
    try {
        dotnet publish FctDistributor.csproj -c Release -o dist | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'FctDistributor 发布失败' }
        Ok 'FctDistributor 发布成功'
    } finally { Pop-Location }
} else {
    Head "1. 构建（已跳过 -SkipBuild）"
}

# ---------- 2) 三个打包器 ----------
Head "2. 运行打包器（生成纯净 zip）"
# ⚠ 三个打包器均用 $ErrorActionPreference='Stop' + throw 表达失败：
#   `&` 调用脚本失败会抛异常 -> 被本脚本外层 Stop 捕获中止，不依赖 $LASTEXITCODE。
& (Join-Path $repo 'FctAggregator\tools\make_package.ps1') | Out-Host
& (Join-Path $repo 'FctAggregator\tools\make_package_agg.ps1') | Out-Host
& (Join-Path $repo 'FctDistributor\tools\make_package.ps1') | Out-Host
Ok '三个打包器均完成'

# ---------- 3) 收集 zip ----------
Head "3. 收集并归档 zip"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$candidates = @()
# 主程序（bin\Release\net8.0-windows\）
$aggBuild = Join-Path $repo 'FctAggregator\bin\Release\net8.0-windows'
foreach ($n in @("Argus-v$Version.zip", "Argus-v$Version-update.zip")) {
    if (Test-Path (Join-Path $aggBuild $n)) { $candidates += Join-Path $aggBuild $n }
}
# 聚合包（dist-agg\）
$aggPkg = Join-Path $repo 'FctAggregator\dist-agg'
foreach ($n in @("Argus-Agg-v$Version.zip")) {
    if (Test-Path (Join-Path $aggPkg $n)) { $candidates += Join-Path $aggPkg $n }
}
# 分发器（dist\）
$distPkg = Join-Path $repo 'FctDistributor\dist'
foreach ($n in @("FCT-Distributor-v$distVer.zip", "FCT-Distributor-v$distVer-update.zip")) {
    if (Test-Path (Join-Path $distPkg $n)) { $candidates += Join-Path $distPkg $n }
}

if ($candidates.Count -eq 0) { throw '没有收集到任何 zip，请检查打包器是否成功' }

$archived = @()
foreach ($zip in $candidates) {
    $name = Split-Path $zip -Leaf
    Copy-Item $zip (Join-Path $outDir $name) -Force
    $archived += Join-Path $outDir $name
    Ok "归档: $name  ($([math]::Round((Get-Item $zip).Length/1KB)) KB)"
}

# ---------- 4) 纯净性最终校验（对归档 zip 逐一） ----------
Head "4. 纯净性校验（不污染现有部署数据）"
foreach ($zip in $archived) {
    $stage = Join-Path $env:TEMP ("rel_check_" + [guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force
        $name = Split-Path $zip -Leaf
        $bad = @()
        foreach ($sub in @('data','logs')) { if (Test-Path (Join-Path $stage $sub)) { $bad += "$sub\" } }
        foreach ($f in (Get-ChildItem $stage -Recurse -File -ErrorAction SilentlyContinue)) {
            if ($f.Extension -in @('.db','.sqlite','.sqlite3','.pdb')) { $bad += $f.Name }
        }
        if ($bad.Count -gt 0) { throw "$name 纯净性不通过：含 $($bad -join ',')" }
        Ok "$name 纯净（无 data/logs/*.db/*.pdb）"
    } finally {
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------- 5) RELEASE.txt 发布清单 ----------
Head "5. 生成 RELEASE.txt"
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("Argus 统一发布包  v$Version")
[void]$sb.AppendLine("发布日期：$stamp")
[void]$sb.AppendLine("主程序版本：$Version      分发器版本：$distVer")
[void]$sb.AppendLine("=" * 66)
[void]$sb.AppendLine("")
[void]$sb.AppendLine("包含的 zip：")
[void]$sb.AppendLine("")
foreach ($zip in $archived) {
    $h = (Get-FileHash $zip -Algorithm SHA256).Hash
    $sz = [math]::Round((Get-Item $zip).Length/1KB)
    [void]$sb.AppendLine("  $('{0,-48}' -f (Split-Path $zip -Leaf))  $sz KB")
    [void]$sb.AppendLine("      SHA256: $h")
    [void]$sb.AppendLine("")
}
[void]$sb.AppendLine("纯净性声明：本批次所有 zip 均不含 data\ / logs\ / *.db / *.pdb，" )
[void]$sb.AppendLine("          config.json 为模板（webhook 置空），部署时不影响机台现有数据。")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("部署方式：")
[void]$sb.AppendLine("  主程序更新：解压 Argus-v$Version-update.zip 到机台，用 tools\deploy_update.ps1 或手动覆盖（data/logs 不受影响）。")
[void]$sb.AppendLine("  聚合后端：  解压 Argus-Agg-v$Version.zip，运行 一键部署聚合服务.bat。")
[void]$sb.AppendLine("  分发器：    解压 FCT-Distributor-v$distVer.zip（工程/测试人员使用）。")
$manifestPath = Join-Path $outDir 'RELEASE.txt'
$sb.ToString() | Set-Content $manifestPath -Encoding UTF8
Ok "RELEASE.txt 已生成（$manifestPath）"

Head "发布完成"
Info "归档目录: $outDir"
foreach ($zip in $archived) { Write-Host "  $(Split-Path $zip -Leaf)" -ForegroundColor Green }
Write-Host ""
