# 打包 FCT 工具套件 v3.0.0（单 exe：主程序 + 排行/取数/TDMS 三个工具）
# ⚠ v3.0.0：数据分发已独立成 FCT-Distributor.exe，单独打包（见 ../fct-distributor/tools/make_package.ps1）
#
# ⚠ 2026-08-01 交付形态变更：**不再生成 package_v*/ 子目录**。
#   产物直接铺平到 bin\Release\net8.0-windows（build 标准输出）：
#     程序文件 + config.json + 启动.bat + app_icon.ico + 文档 + data\ + logs\ + 两个 zip
#   两个 zip 从临时 staging 生成，完整 zip 与更新 zip 的区别：
#     完整包：含 config.json（webhook 按要求写死）、data\、logs\
#     更新包：无 config.json / *.pdb / data\ / logs\，多一份 覆盖说明.md
#
# ⚠ dotnet clean / Rebuild 会清掉本目录内容，打好的 zip 请及时拷走归档。
$ErrorActionPreference = 'Stop'
# 路径自己推（仓库目录 2026-07-31 从 G4_9-QA 改名成 Argus，写死绝对路径会静静失效）
$root = (Resolve-Path "$PSScriptRoot\..").Path
$ver  = '3.2.1'
$out  = Join-Path $root 'bin\Release\net8.0-windows'

# 程序文件（不含 pdb / config.json）
$progFiles = @(
    'Argus.exe',
    'Argus.dll',
    'Argus.deps.json',
    'Argus.runtimeconfig.json',
    'e_sqlite3.dll',
    'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw.batteries_v2.dll',
    'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll',
    'System.IO.Ports.dll',
    'TDMSReader.dll'                    # v2.9.4 新增：TDMS 波形工具依赖
)
# 仓库根目录里的随包文件（不是构建产物）
# ⚠ app_icon.ico 必须带上：v2.9.4 前它没进包，导致现场托盘图标一直是系统灰图
$rootFiles = @('启动.bat', 'config.json', 'app_icon.ico')
$docs = @('版本更新说明.md', '更新日志.md')

<#
  把程序文件从 $out 里找出来。
  ⚠ 踩过的坑：原生库 e_sqlite3.dll 只在 runtimes\win-x64\native\ 下（build/publish 都不平铺到根）。
     根目录没有时向下递归找，优先 win-x64。
#>
function Resolve-DistFile([string]$name) {
    $direct = Join-Path $out $name
    if (Test-Path $direct) { return $direct }
    $hit = Get-ChildItem $out -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
           Sort-Object { if ($_.FullName -match 'win-x64') { 0 } elseif ($_.FullName -match 'win') { 1 } else { 2 } } |
           Select-Object -First 1
    if (-not $hit) { throw "$out 里找不到 $name（先 dotnet build -c Release）" }
    return $hit.FullName
}

# ==================== 1) 铺平到 $out ====================
# 程序文件已在 build 时生成在 $out；补上随包文件与 data/logs 目录
foreach ($f in $rootFiles) { Copy-Item (Join-Path $root $f) $out }
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $out }
New-Item -ItemType Directory -Path (Join-Path $out 'data'), (Join-Path $out 'logs') -Force | Out-Null

# ==================== 2) 完整 zip（staging 组装）====================
$stage = Join-Path $env:TEMP "fct_pkg_$PID"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stage }
foreach ($f in ($rootFiles + $docs)) { Copy-Item (Join-Path $root $f) $stage }
New-Item -ItemType Directory -Path (Join-Path $stage 'data'), (Join-Path $stage 'logs') -Force | Out-Null

$zipFull = Join-Path $out "Argus-v$ver.zip"
if (Test-Path $zipFull) { Remove-Item $zipFull -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zipFull -Force

# ==================== 3) 更新 zip（无 config/data/logs/pdb，多覆盖说明）====================
$stageUpd = Join-Path $env:TEMP "fct_pkg_upd_$PID"
Remove-Item $stageUpd -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageUpd -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stageUpd }
Copy-Item (Join-Path $root '启动.bat') $stageUpd                        # 启动脚本可覆盖，config.json 不可
Copy-Item (Join-Path $root 'app_icon.ico') $stageUpd                     # 图标也跟着更新
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $stageUpd }
Copy-Item (Join-Path $root '覆盖说明.md') $stageUpd      # 覆盖包专属：升级步骤说明

$zipUpd = Join-Path $out "Argus-v$ver-update.zip"
if (Test-Path $zipUpd) { Remove-Item $zipUpd -Force }
Compress-Archive -Path "$stageUpd\*" -DestinationPath $zipUpd -Force

# ==================== 4) 断言：更新包绝不能含这些 ====================
$forbidden = @('config.json', '*.pdb')
foreach ($pat in $forbidden) {
    $hit = Get-ChildItem $stageUpd -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
    if ($hit) { throw "更新包里出现了不该有的文件: $($hit.Name -join ', ')" }
}
foreach ($sub in @('data', 'logs')) {
    if (Test-Path (Join-Path $stageUpd $sub)) { throw "更新包里出现了 $sub\ 目录" }
}
Write-Host "[OK] 更新包已确认不含 config.json / *.pdb / data\ / logs\"

# ==================== 5) 清理 staging ====================
Remove-Item $stage, $stageUpd -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "已生成（铺平在 $out ）:"
Write-Host "  完整 zip: $(Split-Path $zipFull -Leaf)  $([math]::Round((Get-Item $zipFull).Length/1KB)) KB"
Write-Host "  更新 zip: $(Split-Path $zipUpd -Leaf)  $([math]::Round((Get-Item $zipUpd).Length/1KB)) KB"
