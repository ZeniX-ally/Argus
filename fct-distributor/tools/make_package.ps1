# 打包 FCT 数据分发器 v1.9.0（v3.0.0 起从 FCT 工具套件里独立出来的单独程序）
#
# ⚠ 为什么独立：它能改文件时间戳、按模板造数、批量投放 XML。
#   这些操作放在产线机台的主程序里，误点一次后果很重 —— 所以只发给工程/测试人员。
#
# 生成 package_v1.9.0（完整包）与 package_v1.9.0-update（覆盖包）
$ErrorActionPreference = 'Stop'
# 路径自己推（仓库目录改过名，写死绝对路径会静静失效）
$root = (Resolve-Path "$PSScriptRoot\..").Path
$ver  = '1.9.0'
$dist = Join-Path $root 'dist'
$full = Join-Path $root "package_v$ver"
$upd  = Join-Path $root "package_v$ver-update"

$progFiles = @(
    'FCT-Distributor.exe',
    'FCT-Distributor.dll',
    'FCT-Distributor.deps.json',
    'FCT-Distributor.runtimeconfig.json'
)
$rootFiles = @('app_icon.ico')
$docs = @('README.md')

<#
  ⚠ 与主程序打包脚本同一个坑：无 RID 的 dotnet publish 不把原生库放到根目录。
     分发器目前不依赖 SQLite，所以清单里没有原生库；真加了依赖记得照抄 Resolve-DistFile。
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

foreach ($d in @($full, $upd)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $full }
foreach ($f in $rootFiles) { Copy-Item (Join-Path $root $f) $full }
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $full }

foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $upd }
foreach ($f in $rootFiles) { Copy-Item (Join-Path $root $f) $upd }
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $upd }

Write-Host "包目录已建好"
Write-Host "  完整包: $((Get-ChildItem $full -Recurse -File).Count) 个文件"
Write-Host "  更新包: $((Get-ChildItem $upd  -Recurse -File).Count) 个文件"

# --- 断言：别把主程序的东西混进来 ---
$forbidden = @('*.pdb', 'Argus.*', 'config.json', '*.db')
foreach ($pat in $forbidden) {
    foreach ($d in @($full, $upd)) {
        $hit = Get-ChildItem $d -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
        if ($hit) { throw "包里出现了不该有的文件: $($hit.Name -join ', ')" }
    }
}
Write-Host "[OK] 包里不含 *.pdb / Argus.* / config.json / *.db"

# --- 断言：内置模板真的编进了 exe（LoadEmbedded 靠它） ---
$dll = Join-Path $full 'FCT-Distributor.dll'
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$xmls = @($asm.GetManifestResourceNames() | Where-Object { $_ -like '*.xml' })
if ($xmls.Count -lt 2) { throw "内置模板没进 exe（只找到 $($xmls.Count) 个 xml 资源）" }
$bad = @($xmls | Where-Object { -not $_.StartsWith('FctDistributor.tpl.') })
if ($bad) { throw "资源名前缀不对（应为 FctDistributor.tpl.）: $($bad -join ', ')" }
Write-Host "[OK] 内置模板 $($xmls.Count) 个，资源名前缀正确（FctDistributor.tpl.）"
$ico = @($asm.GetManifestResourceNames() | Where-Object { $_ -like '*app_icon.ico' })
if (-not $ico) { throw "图标没作为嵌入资源编进去（AppIcon 会退回系统灰图）" }
Write-Host "[OK] 图标已嵌入（$($ico -join ', ')）"
