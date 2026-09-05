[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = (Resolve-Path "$PSScriptRoot\..").Path

function Ok([string]$m)  { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Info([string]$m){ Write-Host "  [--]   $m" -ForegroundColor Gray }
function Head([string]$t){ Write-Host ""; Write-Host ("=" * 66) -ForegroundColor Cyan; Write-Host "  $t" -ForegroundColor Cyan; Write-Host ("=" * 66) -ForegroundColor Cyan }

if (-not $Version) {
    $csproj = Join-Path $repo 'FctAggregator\FctAggregator.csproj'
    $Version = [regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
}
if (-not $Version) { throw '无法确定版本号：FctAggregator.csproj 没有 <Version>，请用 -Version 指定' }

$outDir = Join-Path $repo "Releases\v$Version"

Head "Argus 发布 v$Version"
Info "归档目录: $outDir"

if (-not $SkipBuild) {
    Head "1. 构建工程（Release）"
    Push-Location (Join-Path $repo 'FctAggregator')
    try {
        dotnet build FctAggregator.csproj -c Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'FctAggregator 构建失败' }
        Ok 'FctAggregator 构建成功'
    } finally { Pop-Location }
} else {
    Head "1. 构建（已跳过 -SkipBuild）"
}

Head "2. 运行打包器（生成纯净 zip）"
& (Join-Path $repo 'FctAggregator\tools\make_package.ps1') | Out-Host
& (Join-Path $repo 'FctAggregator\tools\make_package_agg.ps1') | Out-Host
Ok '两个打包器均完成'

Head "3. 收集并归档 zip"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$candidates = @()
$aggBuild = Join-Path $repo 'FctAggregator\bin\Release\net8.0-windows'
foreach ($n in @("Argus-v$Version.zip", "Argus-v$Version-update.zip")) {
    if (Test-Path (Join-Path $aggBuild $n)) { $candidates += Join-Path $aggBuild $n }
}
$aggPkg = Join-Path $repo 'FctAggregator\dist-agg'
foreach ($n in @("Argus-Agg-v$Version.zip")) {
    if (Test-Path (Join-Path $aggPkg $n)) { $candidates += Join-Path $aggPkg $n }
}

if ($candidates.Count -eq 0) { throw '没有收集到任何 zip，请检查打包器是否成功' }

$archived = @()
foreach ($zip in $candidates) {
    $name = Split-Path $zip -Leaf
    Copy-Item $zip (Join-Path $outDir $name) -Force
    $archived += Join-Path $outDir $name
    Ok "归档: $name  ($([math]::Round((Get-Item $zip).Length/1KB)) KB)"
}

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

Head "5. 生成 RELEASE.txt"
$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("Argus 发布包  v$Version")
[void]$sb.AppendLine("发布日期：$stamp")
[void]$sb.AppendLine("主程序版本：$Version")
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
[void]$sb.AppendLine("  主程序更新：Argus.exe upgrade（图形化升级向导，v3.22.1 起）选本更新包一键部署；")
[void]$sb.AppendLine("              或命令行 deploy_update.ps1 -Execute（脚本已随包自带，data/logs 不受影响）。")
[void]$sb.AppendLine("  聚合后端：  解压 Argus-Agg-v$Version.zip，运行 一键部署聚合服务.bat。")
[void]$sb.AppendLine("  分发器：    已独立仓库 e:/FctDistributor（FCT-Distributor v1.9.2），用其自带 make_package.ps1 单独发版。")
$manifestPath = Join-Path $outDir 'RELEASE.txt'
$sb.ToString() | Set-Content $manifestPath -Encoding UTF8
Ok "RELEASE.txt 已生成（$manifestPath）"

Head "发布完成"
Info "归档目录: $outDir"
foreach ($zip in $archived) { Write-Host "  $(Split-Path $zip -Leaf)" -ForegroundColor Green }
Write-Host ""
