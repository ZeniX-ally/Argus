$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$ver  = [regex]::Match((Get-Content (Join-Path $root 'FctAggregator.csproj') -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $ver) { throw 'FctAggregator.csproj 里找不到 <Version>' }
$out  = Join-Path $root 'bin\Release\net8.0-windows'

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
$rootFiles = @('启动.bat', 'app_icon.ico', '一键部署聚合服务.bat')
$docs = @('版本更新说明.md', '更新日志.md', '聚合服务说明.md')

function Write-PackageConfig([string]$destPath) {
    $cfg = Get-Content (Join-Path $root 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $cfg.webhook_url = ''
    $cfg.agg_token   = ''
    $cfg | ConvertTo-Json -Depth 6 | Set-Content $destPath -Encoding UTF8
}

function Resolve-DistFile([string]$name) {
    $direct = Join-Path $out $name
    if (Test-Path $direct) { return $direct }
    $hit = Get-ChildItem $out -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
           Sort-Object { if ($_.FullName -match 'win-x64') { 0 } elseif ($_.FullName -match 'win') { 1 } else { 2 } } |
           Select-Object -First 1
    if (-not $hit) { throw "$out 里找不到 $name（先 dotnet build -c Release）" }
    return $hit.FullName
}

function Copy-WinRuntimes([string]$stage) {
    $winRoot = Join-Path $out 'runtimes'
    if (-not (Test-Path $winRoot)) { throw "$out 下没有 runtimes\（dotnet build 产物不完整）" }
    $rtDest = Join-Path $stage 'runtimes'
    New-Item -ItemType Directory -Path $rtDest -Force | Out-Null
    foreach ($d in (Get-ChildItem $winRoot -Directory)) {
        if ($d.Name -match '^win') {
            Copy-Item $d.FullName $rtDest -Recurse -Force
        }
    }
}

function Assert-DepsWinFiles([string]$stage) {
    $depsPath = Join-Path $out 'Argus.deps.json'
    if (-not (Test-Path $depsPath)) { throw "$depsPath 不存在" }
    $deps = Get-Content $depsPath -Raw | ConvertFrom-Json
    $missing = @()
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

function Copy-WebRoot([string]$stage) {
    $web = Join-Path $out 'public'
    if (-not (Test-Path $web)) { throw "$out 下没有 public\（前端静态资源未随构建复制，检查 FctAggregator.csproj 的 Content Include）" }
    $webDest = Join-Path $stage 'public'
    New-Item -ItemType Directory -Path $webDest -Force | Out-Null
    Copy-Item (Join-Path $web '*') $webDest -Recurse -Force
}

foreach ($f in $rootFiles) {
    if ($f -eq '一键部署聚合服务.bat') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $out }
    elseif ($f -eq 'deploy_update.ps1') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $out }
    else { Copy-Item (Join-Path $root $f) $out }
}
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $out }
Write-PackageConfig (Join-Path $out 'config.json')
New-Item -ItemType Directory -Path (Join-Path $out 'data'), (Join-Path $out 'logs') -Force | Out-Null

$stage = Join-Path $env:TEMP "fct_pkg_$PID"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stage }
Copy-WinRuntimes $stage
Assert-DepsWinFiles $stage
Copy-WebRoot $stage
foreach ($f in ($rootFiles + $docs)) {
    if ($f -eq '一键部署聚合服务.bat') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $stage }
    elseif ($f -eq 'deploy_update.ps1') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $stage }
    else { Copy-Item (Join-Path $root $f) $stage }
}
Write-PackageConfig (Join-Path $stage 'config.json')
New-Item -ItemType Directory -Path (Join-Path $stage 'data'), (Join-Path $stage 'logs') -Force | Out-Null

$zipFull = Join-Path $out "Argus-v$ver.zip"
if (Test-Path $zipFull) { Remove-Item $zipFull -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zipFull -Force

$stageUpd = Join-Path $env:TEMP "fct_pkg_upd_$PID"
Remove-Item $stageUpd -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageUpd -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stageUpd }
Copy-WinRuntimes $stageUpd
Assert-DepsWinFiles $stageUpd
Copy-WebRoot $stageUpd
Copy-Item (Join-Path $root '启动.bat') $stageUpd
Copy-Item (Join-Path (Join-Path $root 'tools') '一键部署聚合服务.bat') $stageUpd
Copy-Item (Join-Path (Join-Path $root 'tools') 'deploy_update.ps1') $stageUpd
Write-PackageConfig (Join-Path $stageUpd 'config.json')
Copy-Item (Join-Path $root 'app_icon.ico') $stageUpd
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $stageUpd }

$zipUpd = Join-Path $out "Argus-v$ver-update.zip"
if (Test-Path $zipUpd) { Remove-Item $zipUpd -Force }
Compress-Archive -Path "$stageUpd\*" -DestinationPath $zipUpd -Force

$forbidden = @('*.pdb')
foreach ($pat in $forbidden) {
    $hit = Get-ChildItem $stageUpd -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
    if ($hit) { throw "更新包里出现了不该有的文件: $($hit.Name -join ', ')" }
}
foreach ($sub in @('data', 'logs')) {
    if (Test-Path (Join-Path $stageUpd $sub)) { throw "更新包里出现了 $sub\ 目录" }
}
Write-Host "[OK] 更新包已确认含 config.json 模板、不含 *.pdb / data\ / logs\"

Remove-Item $stage, $stageUpd -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "已生成（铺平在 $out ）:"
Write-Host "  完整 zip: $(Split-Path $zipFull -Leaf)  $([math]::Round((Get-Item $zipFull).Length/1KB)) KB"
Write-Host "  更新 zip: $(Split-Path $zipUpd -Leaf)  $([math]::Round((Get-Item $zipUpd).Length/1KB)) KB"
