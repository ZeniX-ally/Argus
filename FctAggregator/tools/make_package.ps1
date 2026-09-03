# 打包 FCT 工具套件 v3.3.0（单 exe：主程序 + 排行/取数/TDMS 三个工具）
# ⚠ v3.0.0：数据分发已独立成 FCT-Distributor.exe，单独打包（见 ../FctDistributor/tools/make_package.ps1）
# ⚠ v3.4.1：聚合后端纯净包用 make_package_agg.ps1 单独打包（输出 dist-agg\Argus-Agg-v3.4.1.zip）
#
# ⚠ 2026-08-01 交付形态变更：**不再生成 package_v*/ 子目录**。
#   产物直接铺平到 bin\Release\net8.0-windows（build 标准输出）：
#     程序文件 + config.json + 启动.bat + app_icon.ico + 文档 + data\ + logs\ + 两个 zip
#   两个 zip 从临时 staging 生成，完整 zip 与更新 zip 的区别：
#     完整包：含 config.json（webhook 置空，机台现场填）、data\、logs\
#     更新包：含 config.json（配置模板，webhook 同样置空）、无 *.pdb / data\ / logs\，多一份 覆盖说明.md
#
# ⚠ 2026-08-11 安全治理：webhook_url 是飞书机器人凭据，**禁止进入任何发布产物**。
#   包内 config.json 一律经 Write-PackageConfig 生成（webhook_url 置空）——
#   即使工作区 config.json 配了真实 token，打包也不外泄。
#   现场部署由 deploy_update.ps1 合并配置：现场已有 webhook 则保留现场值，
#   现场为空（含全新安装）则保持空，由机台侧填入后生效。
#
# ⚠ dotnet clean / Rebuild 会清掉本目录内容，打好的 zip 请及时拷走归档。
$ErrorActionPreference = 'Stop'
# SSH/CI 非交互会话无控制台：Write-Progress 抛 HostException 会中止脚本，静默进度条
$ProgressPreference = 'SilentlyContinue'
# 路径自己推（仓库目录 2026-07-31 从 G4_9-QA 改名成 Argus，写死绝对路径会静静失效）
$root = (Resolve-Path "$PSScriptRoot\..").Path
# ⚠ 版本号自动读 FctAggregator.csproj 的 <Version>（2026-08-14 修复：曾手写 $ver='3.4.1'
#   导致 csproj 升到 3.4.10 后打出的 zip 仍叫 Argus-v3.4.1.zip，包名版本滞后于程序）
$ver  = [regex]::Match((Get-Content (Join-Path $root 'FctAggregator.csproj') -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $ver) { throw 'FctAggregator.csproj 里找不到 <Version>' }
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
# ⚠ config.json 不在此列表：webhook 是凭据，由 Write-PackageConfig 置空后单独入包
$rootFiles = @('启动.bat', 'app_icon.ico', '一键部署聚合服务.bat')
# 聚合服务说明.md 随包：机台包也带了一键部署聚合服务.bat，使用说明应一并分发
$docs = @('版本更新说明.md', '更新日志.md', '聚合服务说明.md')

<#
  生成包内 config.json 模板：webhook_url / agg_token 一律置空。
  ⚠ 2026-08-11 安全治理：webhook 是飞书机器人凭据，不允许进任何发布产物/构建目录。
    2026-08-27 扩围：agg_token 是共享访问令牌，同样置空，避免开发机 token 随包外泄。
    部署端合并配置时保留现场值（deploy_update.ps1 的 keepKey 列表含 webhook_url / agg_token），
    现场为空才用模板值（模板值就是空，全新安装由机台侧填入/程序自动生成）。
#>
function Write-PackageConfig([string]$destPath) {
    $cfg = Get-Content (Join-Path $root 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $cfg.webhook_url = ''
    $cfg.agg_token   = ''
    $cfg | ConvertTo-Json -Depth 6 | Set-Content $destPath -Encoding UTF8
}

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
    $winRoot = Join-Path $out 'runtimes'
    if (-not (Test-Path $winRoot)) { throw "$out 下没有 runtimes\（dotnet build 产物不完整）" }
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
    $depsPath = Join-Path $out 'Argus.deps.json'
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

<#
  v3.8.0：把 public\ 前端静态资源（WebAggServer /public/* 服务）拷进 staging。
  ⚠ 同样必须先建目标目录再复制内容（目标不存在时 Copy-Item -Recurse 会吞源目录名，
     public 会被展开成 stage\css/js/index.html 丢 public 层级 -> 部署后 /public/* 404）。
  缺目录直接 throw：csproj 的 Content Include 漏了 public 时打包必须显式失败而不是静默出残缺包。
#>
function Copy-WebRoot([string]$stage) {
    $web = Join-Path $out 'public'
    if (-not (Test-Path $web)) { throw "$out 下没有 public\（前端静态资源未随构建复制，检查 FctAggregator.csproj 的 Content Include）" }
    $webDest = Join-Path $stage 'public'
    New-Item -ItemType Directory -Path $webDest -Force | Out-Null
    Copy-Item (Join-Path $web '*') $webDest -Recurse -Force
}

# ==================== 1) 铺平到 $out ====================
# 程序文件已在 build 时生成在 $out；补上随包文件与 data/logs 目录
foreach ($f in $rootFiles) {
    if ($f -eq '一键部署聚合服务.bat') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $out }
    elseif ($f -eq 'deploy_update.ps1') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $out }  # v3.22.1：部署引擎随包（图形升级向导/命令行部署兜底用）
    else { Copy-Item (Join-Path $root $f) $out }
}
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $out }
Write-PackageConfig (Join-Path $out 'config.json')          # webhook 置空（凭据不进构建产物）
New-Item -ItemType Directory -Path (Join-Path $out 'data'), (Join-Path $out 'logs') -Force | Out-Null

# ==================== 2) 完整 zip（staging 组装）====================
$stage = Join-Path $env:TEMP "fct_pkg_$PID"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stage }
Copy-WinRuntimes $stage
Assert-DepsWinFiles $stage
Copy-WebRoot $stage   # v3.8.0：public\ 前端静态资源（WebAggServer /public/* 依赖）
foreach ($f in ($rootFiles + $docs)) {
    if ($f -eq '一键部署聚合服务.bat') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $stage }
    elseif ($f -eq 'deploy_update.ps1') { Copy-Item (Join-Path (Join-Path $root 'tools') $f) $stage }
    else { Copy-Item (Join-Path $root $f) $stage }
}
Write-PackageConfig (Join-Path $stage 'config.json')        # 完整包模板同样置空 webhook
New-Item -ItemType Directory -Path (Join-Path $stage 'data'), (Join-Path $stage 'logs') -Force | Out-Null

$zipFull = Join-Path $out "Argus-v$ver.zip"
if (Test-Path $zipFull) { Remove-Item $zipFull -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zipFull -Force

# ==================== 3) 更新 zip（含 config.json 模板，无 data/logs/pdb，多覆盖说明）====================
# ⚠ v3.2.2 起更新包**带 config.json**：现场 config 可能被旧版取数打包 bug 冲掉，
#   需要随包恢复（fct_ini_path=PEU 等已就位）。部署端会做**合并覆盖**：
#   保留现场 station_id（库名不变）+ webhook_url（凭据，模板里是空值），现场特有字段并入，原 config 自动备份。
$stageUpd = Join-Path $env:TEMP "fct_pkg_upd_$PID"
Remove-Item $stageUpd -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stageUpd -Force | Out-Null
foreach ($f in $progFiles) { Copy-Item (Resolve-DistFile $f) $stageUpd }
Copy-WinRuntimes $stageUpd
Assert-DepsWinFiles $stageUpd
Copy-WebRoot $stageUpd   # v3.8.0：public\ 前端静态资源（看板升级也必须带上）
Copy-Item (Join-Path $root '启动.bat') $stageUpd
Copy-Item (Join-Path (Join-Path $root 'tools') '一键部署聚合服务.bat') $stageUpd  # 聚合服务端升级也可一键部署
Copy-Item (Join-Path (Join-Path $root 'tools') 'deploy_update.ps1') $stageUpd     # v3.22.1：部署引擎随更新包（图形升级向导 Argus.exe upgrade 的兜底脚本源）
Write-PackageConfig (Join-Path $stageUpd 'config.json')     # 配置模板（webhook 置空，部署端合并时保留现场值）
Copy-Item (Join-Path $root 'app_icon.ico') $stageUpd                     # 图标也跟着更新
foreach ($f in $docs) { Copy-Item (Join-Path $root $f) $stageUpd }

$zipUpd = Join-Path $out "Argus-v$ver-update.zip"
if (Test-Path $zipUpd) { Remove-Item $zipUpd -Force }
Compress-Archive -Path "$stageUpd\*" -DestinationPath $zipUpd -Force

# ==================== 4) 断言：更新包绝不能含这些 ====================
# config.json 现在**有意包含**（配置模板），其余仍禁止
$forbidden = @('*.pdb')
foreach ($pat in $forbidden) {
    $hit = Get-ChildItem $stageUpd -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
    if ($hit) { throw "更新包里出现了不该有的文件: $($hit.Name -join ', ')" }
}
foreach ($sub in @('data', 'logs')) {
    if (Test-Path (Join-Path $stageUpd $sub)) { throw "更新包里出现了 $sub\ 目录" }
}
Write-Host "[OK] 更新包已确认含 config.json 模板、不含 *.pdb / data\ / logs\"

# ==================== 5) 清理 staging ====================
Remove-Item $stage, $stageUpd -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "已生成（铺平在 $out ）:"
Write-Host "  完整 zip: $(Split-Path $zipFull -Leaf)  $([math]::Round((Get-Item $zipFull).Length/1KB)) KB"
Write-Host "  更新 zip: $(Split-Path $zipUpd -Leaf)  $([math]::Round((Get-Item $zipUpd).Length/1KB)) KB"
