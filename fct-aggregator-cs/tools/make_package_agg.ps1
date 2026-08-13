# 打包 Argus 聚合后端纯净包 v3.4.1（机台端采集包用 make_package.ps1，本脚本只打聚合包）
#
# 本包只含聚合服务相关：Argus.exe（agg --web / agg --install）+ 运行依赖 +
# 一键部署聚合服务.bat + 聚合服务说明.md + config.json（聚合端模板）+ data\ + logs\。
#
# ⚠ 不打包：启动.bat（机台采集用）、版本更新说明.md / 覆盖说明.md / 更新日志.md
#   （机台端文档，聚合端不需要）、webhook（聚合端没有飞书推送）。
#
# ⚠ 聚合端 config 模板直接写死（与 Program.BuildDefaultAggConfigJson 一致），
#   不用 webhook——机台端 config 不在这里，聚合端不需要飞书推送。
#
# ⚠ 程序文件来自 bin\Release\net8.0-windows（先 dotnet build -c Release）；
#   本脚本输出到 dist-agg\（仓库根下新建，dotnet clean 不会动它）。
#   版本号 $ver 保持与 FctAggregator.csproj 的 <Version> 一致。
$ErrorActionPreference = 'Stop'
# SSH/CI 非交互会话无控制台：Write-Progress 抛 HostException 会中止脚本，静默进度条
$ProgressPreference = 'SilentlyContinue'
# 路径自己推（写死绝对路径会静静失效）
$root  = (Resolve-Path "$PSScriptRoot\..").Path
$ver   = '3.4.1'
$build = Join-Path $root 'bin\Release\net8.0-windows'
$out   = Join-Path $root 'dist-agg'

# 程序文件（不含 pdb / config.json；聚合服务是同一 exe，依赖清单与机台包一致）
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
    'TDMSReader.dll'
)
# 聚合包随包文件：bat 在 tools\ 下，说明文档在仓库根
# ⚠ config.json 不在此列表：由 Write-PackageAggConfig 生成聚合模板后单独入包
$aggFiles = @('一键部署聚合服务.bat', '聚合服务说明.md')

<#
  生成聚合端 config.json 模板（写死，与 Program.BuildDefaultAggConfigJson 一致）。
  ⚠ 无 webhook：机台端 config 不在这里，聚合端不需要飞书推送。
#>
function Write-PackageAggConfig([string]$destPath) {
    $json = @'
{
  "station_id": "AGG",
  "results_root": "D:\\Results",
  "agg_transport": "http",
  "agg_http_port": 8080,
  "agg_share_root": "",
  "log_level": "INFO"
}
'@
    Set-Content -Path $destPath -Value $json -Encoding UTF8
}

<#
  把程序文件从 $build 里找出来。
  ⚠ 踩过的坑：原生库 e_sqlite3.dll 只在 runtimes\win-x64\native\ 下（build/publish 都不平铺到根）。
     根目录没有时向下递归找，优先 win-x64。
#>
function Resolve-DistFile([string]$name) {
    $direct = Join-Path $build $name
    if (Test-Path $direct) { return $direct }
    $hit = Get-ChildItem $build -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
           Sort-Object { if ($_.FullName -match 'win-x64') { 0 } elseif ($_.FullName -match 'win') { 1 } else { 2 } } |
           Select-Object -First 1
    if (-not $hit) { throw "$build 里找不到 $name（先 dotnet build -c Release）" }
    return $hit.FullName
}

<#
  把 win 平台的 runtimes 树拷进 staging（保持相对路径，deps.json 按此路径加载）。
  ⚠ 踩过的坑（v3.2.2 现场报错）：System.IO.Ports 的 NuGet 包把**实现**放在
     runtimes\win\lib\net8.0\System.IO.Ports.dll（87728B），根目录那个 35488B 只是
     reference assembly（编译期空壳）。只拷根目录 → 运行时按 deps.json 找 runtimes 路径
     → FileNotFoundException「Could not load file or assembly 'System.IO.Ports'」。
     e_sqlite3.dll 同理：真正会被加载的是 runtimes\win-x64\native\ 下那份。
  所以整个 runtimes\win* 目录树必须随包走。
#>
function Copy-WinRuntimes([string]$stage) {
    $winRoot = Join-Path $build 'runtimes'
    if (-not (Test-Path $winRoot)) { throw "$build 下没有 runtimes\（dotnet build 产物不完整）" }
    # ⚠ 必须先建目标目录：目标不存在时 Copy-Item -Recurse 会把源目录名吞掉
    #   （runtimes\win 会被展开成 runtimes\lib\...，丢 win 层级 -> 运行时照样找不到）
    $rtDest = Join-Path $stage 'runtimes'
    New-Item -ItemType Directory -Path $rtDest -Force | Out-Null
    foreach ($d in (Get-ChildItem $winRoot -Directory)) {
        if ($d.Name -match '^win') {
            Copy-Item $d.FullName $rtDest -Recurse -Force
        }
    }
}

<#
  校验：deps.json 声明的**所有** win 平台运行时文件（rid 以 win 开头）必须都已入包。
  这是对 Copy-WinRuntimes 的兜底——以后新增 NuGet 包也不会再漏。
#>
function Assert-DepsWinFiles([string]$stage) {
    $depsPath = Join-Path $build 'Argus.deps.json'
    if (-not (Test-Path $depsPath)) { throw "$depsPath 不存在" }
    $deps = Get-Content $depsPath -Raw | ConvertFrom-Json
    $missing = @()
    # ⚠ runtimeTargets 在 targets.<框架>.包节点 里，不在顶层——之前遍历错层级导致校验空转
    foreach ($tf in $deps.targets.PSObject.Properties) {
        foreach ($p in $tf.Value.PSObject.Properties) {
            $node = $p.Value
            if (-not $node -or -not $node.runtimeTargets) { continue }
            foreach ($rt in $node.runtimeTargets.PSObject.Properties) {
                $rel = $rt.Name; $rid = $rt.Value.rid
                if ($rel -like 'runtimes/*' -and $rid -like 'win*') {
                    $local = Join-Path $stage ($rel -replace '/', '\')
                    if (-not (Test-Path $local)) { $missing += "$rel (rid=$rid)" }
                }
            }
        }
    }
    if ($missing.Count -gt 0) { throw "包内缺少 deps.json 声明的 win 运行时文件: $($missing -join '; ')" }
    Write-Host "[OK] deps.json 的 win 运行时文件已全部入包"
}

# ==================== 1) 铺平到 dist-agg\ ====================
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $out -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $out }
Copy-WinRuntimes $out
Assert-DepsWinFiles $out
Copy-Item (Join-Path (Join-Path $root 'tools') '一键部署聚合服务.bat') $out
Copy-Item (Join-Path $root '聚合服务说明.md') $out
Write-PackageAggConfig (Join-Path $out 'config.json')          # 聚合模板（无 webhook）
New-Item -ItemType Directory -Path (Join-Path $out 'data'), (Join-Path $out 'logs') -Force | Out-Null

# ==================== 2) zip（staging 组装，避免把 zip 打进 zip）====================
$stage = Join-Path $env:TEMP "fct_agg_pkg_$PID"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stage }
Copy-WinRuntimes $stage
Assert-DepsWinFiles $stage
foreach ($f in $aggFiles) {
    if ($f -eq '一键部署聚合服务.bat') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $stage }
    else { Copy-Item (Join-Path $root $f) $stage }
}
Write-PackageAggConfig (Join-Path $stage 'config.json')
New-Item -ItemType Directory -Path (Join-Path $stage 'data'), (Join-Path $stage 'logs') -Force | Out-Null

$zip = Join-Path $out "Argus-Agg-v$ver.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force

# ==================== 3) 断言：聚合包绝不能含这些 ====================
$forbidden = @('*.pdb', '启动.bat')
foreach ($pat in $forbidden) {
    $hit = Get-ChildItem $stage -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
    if ($hit) { throw "聚合包里出现了不该有的文件: $($hit.Name -join ', ')" }
}
Write-Host "[OK] 聚合包已确认不含 *.pdb / 启动.bat"

# ==================== 4) 清理 staging ====================
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# ==================== 5) 输出 ====================
Write-Host "聚合后端包已生成（铺平在 $out ）:"
Write-Host "  zip: $(Split-Path $zip -Leaf)  $([math]::Round((Get-Item $zip).Length/1KB)) KB"
