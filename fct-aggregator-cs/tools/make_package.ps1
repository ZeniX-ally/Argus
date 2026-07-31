# 打包 FCT 工具套件 v2.9.0（单 exe：主程序 + 分发/排行/取数/TDMS 四个工具）
# 生成 package_v2.9.0（完整包，含 config.json）与 package_v2.9.0-update（覆盖包，无 config/data/logs/pdb）
$ErrorActionPreference = 'Stop'
$root = 'F:\Programe\G4_9-QA\fct-aggregator-cs'
$ver  = '2.9.0'
$dist = Join-Path $root 'dist'
$full = Join-Path $root "package_v$ver"
$upd  = Join-Path $root "package_v$ver-update"

# 程序文件（不含 pdb / config.json）
$progFiles = @(
    'FCT-Aggregator.exe',
    'FCT-Aggregator.dll',
    'FCT-Aggregator.deps.json',
    'FCT-Aggregator.runtimeconfig.json',
    'e_sqlite3.dll',
    'Microsoft.Data.Sqlite.dll',
    'SQLitePCLRaw.batteries_v2.dll',
    'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll',
    'System.IO.Ports.dll',
    'TDMSReader.dll'                    # v2.9.0 新增：TDMS 波形工具依赖
)
# 仓库根目录里的随包文件（不是构建产物，不在 dist 里）
$rootFiles = @('启动.bat', 'config.json')
$docs = @('版本更新说明.md', '更新日志.md')

<#
  把程序文件从 dist 里找出来。
  ⚠ 坐过的坑：无 RID 的 `dotnet publish` 不会把原生库拷到根目录，
     `e_sqlite3.dll` 只在 runtimes\win-x64\native\ 下。之前靠 dist 里残留的旧文件蓙着，
     一旦 dist 被清干净就报“找不到路径”。所以根目录没有时向下递归找，优先 win-x64。
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

# --- 完整包 ---
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $full }
foreach ($f in $rootFiles) { Copy-Item (Join-Path $root $f) $full }   # 含 config.json（webhook 按要求写死）
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $full }
New-Item -ItemType Directory -Path (Join-Path $full 'data') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $full 'logs') -Force | Out-Null

# --- 更新包（覆盖用）---
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $upd }
Copy-Item (Join-Path $root '启动.bat') $upd                        # 启动脚本可覆盖，config.json 不可
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $upd }
Copy-Item (Join-Path $root '覆盖说明.md') $upd      # 覆盖包专属：升级步骤说明

Write-Host "包目录已建好"
Write-Host "  完整包: $((Get-ChildItem $full -Recurse -File).Count) 个文件"
Write-Host "  更新包: $((Get-ChildItem $upd  -Recurse -File).Count) 个文件"

# --- 断言：更新包绝不能含这些 ---
$forbidden = @('config.json', '*.pdb')
foreach ($pat in $forbidden) {
    $hit = Get-ChildItem $upd -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
    if ($hit) { throw "更新包里出现了不该有的文件: $($hit.Name -join ', ')" }
}
foreach ($sub in @('data', 'logs')) {
    if (Test-Path (Join-Path $upd $sub)) { throw "更新包里出现了 $sub\ 目录" }
}
Write-Host "[OK] 更新包已确认不含 config.json / *.pdb / data\ / logs\"
