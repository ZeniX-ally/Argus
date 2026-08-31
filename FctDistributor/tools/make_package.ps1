# 打包 FCT 数据分发器（v3.0.0 起从 FCT 工具套件里独立出来的单独程序）
#
# ⚠ 为什么独立：它能改文件时间戳、按模板造数、批量投放 XML。
#   这些操作放在产线机台的主程序里，误点一次后果很重 —— 所以只发给工程/测试人员。
#
# ⚠ 2026-08-27 发布治理：
#   - 版本号自动读 FctDistributor.csproj 的 <Version>（不再手写，防包名版本滞后于程序）；
#   - 产两个纯净 zip（staging 组装，避免把 zip 打进 zip）：
#       完整包 FCT-Distributor-v{ver}.zip        —— 全新安装用（含文档）
#       更新包 FCT-Distributor-v{ver}-update.zip —— 覆盖升级用（不含 config.json / 文档）
#   - 纯净断言：包内绝不含 *.pdb / *.db / config.json / data\ / logs\ / 主程序(Argus.*)文件；
#     配置模板 config.json 由本脚本生成（results_root 置默认值，不进真实配置）。
$ErrorActionPreference = 'Stop'
# 路径自己推（仓库目录改过名，写死绝对路径会静静失效）
$root = (Resolve-Path "$PSScriptRoot\..").Path
# ⚠ 版本号自动读 csproj 的 <Version>（曾手写 '1.9.0' 导致升版后包名滞后）
$ver  = [regex]::Match((Get-Content (Join-Path $root 'FctDistributor.csproj') -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $ver) { throw 'FctDistributor.csproj 里找不到 <Version>' }
$dist = Join-Path $root 'dist'   # dotnet publish 的输出（程序文件来源）

$progFiles = @(
    'FCT-Distributor.exe',
    'FCT-Distributor.dll',
    'FCT-Distributor.deps.json',
    'FCT-Distributor.runtimeconfig.json'
)
$rootFiles = @('app_icon.ico')
$docs = @('README.md')

<#
  从 dist\ 里找程序文件。
  ⚠ 与主程序打包脚本同一个坑：无 RID 的 dotnet publish 不把原生库放到根目录。
     分发器目前不依赖 SQLite，所以清单里没有原生库；真加了依赖记得照抄主程序的 Resolve-DistFile。
#>
function Resolve-DistFile([string]$name) {
    $direct = Join-Path $dist $name
    if (Test-Path $direct) { return $direct }
    $hit = Get-ChildItem $dist -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
           Sort-Object { if ($_.FullName -match 'win-x64') { 0 } elseif ($_.FullName -match 'win') { 1 } else { 2 } } |
           Select-Object -First 1
    if (-not $hit) { throw "dist 里找不到 $name（先跑 dotnet publish -o dist）" }
    return $hit.FullName
}

# 把公共文件拷进 staging（程序文件 + 随包文件 + 文档）
# ⚠ 分发器 config.json 完全可选（缺省用程序默认 D:\Results，见 Program.cs）。
#   为最大纯净度，两个包都不带 config.json——避免更新包覆盖现场、避免任何现场路径/凭据泄漏。
function Copy-PackageFiles([string]$stage, [bool]$withDocs) {
    foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stage }
    foreach ($f in $rootFiles) { Copy-Item (Join-Path $root $f) $stage }
    if ($withDocs) { foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $stage } }
}

# 纯净断言：包内绝不能含这些（对 staging 目录递归检查）
function Assert-Clean([string]$stage, [string]$kind) {
    $forbidden = @('*.pdb', '*.db', 'Argus.*', 'config.json')
    foreach ($pat in $forbidden) {
        $hit = Get-ChildItem $stage -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
        if ($hit) { throw "$kind 包里出现了不该有的文件: $($hit.Name -join ', ')（匹配 $pat）" }
    }
    foreach ($sub in @('data', 'logs')) {
        if (Test-Path (Join-Path $stage $sub)) { throw "$kind 包里出现了 $sub\ 目录" }
    }
    Write-Host "[OK] $kind 已确认不含 *.pdb / *.db / Argus.* / data\ / logs\"
}

# ==================== 1) 完整包 zip（staging 组装）====================
$stageFull = Join-Path $env:TEMP "fct_dist_full_$PID"
Remove-Item $stageFull -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageFull -Force | Out-Null
Copy-PackageFiles $stageFull $true
Assert-Clean $stageFull '完整包'

$zipFull = Join-Path $dist "FCT-Distributor-v$ver.zip"
if (Test-Path $zipFull) { Remove-Item $zipFull -Force }
Compress-Archive -Path "$stageFull\*" -DestinationPath $zipFull -Force

# ==================== 2) 更新包 zip（覆盖升级用，无 config/文档，防止覆盖现场配置）====================
$stageUpd = Join-Path $env:TEMP "fct_dist_upd_$PID"
Remove-Item $stageUpd -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageUpd -Force | Out-Null
Copy-PackageFiles $stageUpd $false
Assert-Clean $stageUpd '更新包'

$zipUpd = Join-Path $dist "FCT-Distributor-v$ver-update.zip"
if (Test-Path $zipUpd) { Remove-Item $zipUpd -Force }
Compress-Archive -Path "$stageUpd\*" -DestinationPath $zipUpd -Force

# ==================== 3) 断言：内置模板真的编进了 exe（LoadEmbedded 靠它）====================
$dll = Join-Path $stageFull 'FCT-Distributor.dll'
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$xmls = @($asm.GetManifestResourceNames() | Where-Object { $_ -like '*.xml' })
if ($xmls.Count -lt 2) { throw "内置模板没进 exe（只找到 $($xmls.Count) 个 xml 资源）" }
$bad = @($xmls | Where-Object { -not $_.StartsWith('FctDistributor.tpl.') })
if ($bad) { throw "资源名前缀不对（应为 FctDistributor.tpl.）: $($bad -join ', ')" }
Write-Host "[OK] 内置模板 $($xmls.Count) 个，资源名前缀正确（FctDistributor.tpl.）"
$ico = @($asm.GetManifestResourceNames() | Where-Object { $_ -like '*app_icon.ico' })
if (-not $ico) { throw "图标没作为嵌入资源编进去（AppIcon 会退回系统灰图）" }
Write-Host "[OK] 图标已嵌入（$($ico -join ', ')）"

# ==================== 4) 清理 staging ====================
Remove-Item $stageFull, $stageUpd -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "已生成（在 dist\ ）:"
Write-Host "  完整 zip: $(Split-Path $zipFull -Leaf)  $([math]::Round((Get-Item $zipFull).Length/1KB)) KB"
Write-Host "  更新 zip: $(Split-Path $zipUpd -Leaf)  $([math]::Round((Get-Item $zipUpd).Length/1KB)) KB"
