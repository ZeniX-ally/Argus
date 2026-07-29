# 打包 FCT-Aggregator v2.4.0
# 生成 package_v2.4.0（完整包，含 config.json）与 package_v2.4.0-update（覆盖包，无 config/data/logs/pdb）
$ErrorActionPreference = 'Stop'
$root = 'F:\Programe\G4_9-QA\fct-aggregator-cs'
$ver  = '2.4.0'
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
    '启动.bat'
)
$docs = @('版本更新说明.md', '更新日志.md')

foreach ($d in @($full, $upd)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

# --- 完整包 ---
foreach ($f in $progFiles) { Copy-Item (Join-Path $dist $f) $full }
Copy-Item (Join-Path $dist 'config.json') $full            # 完整包带 config（webhook 按要求写死）
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $full }
New-Item -ItemType Directory -Path (Join-Path $full 'data') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $full 'logs') -Force | Out-Null

# --- 更新包（覆盖用）---
foreach ($f in $progFiles) { Copy-Item (Join-Path $dist $f) $upd }
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $upd }

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
