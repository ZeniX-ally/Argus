# ============================================================================
# GitHub 开源导出（白名单制）：从当前 main 生成单提交导出分支并强推 github/main
#
# 白名单之外的任何文件（agent 配置、工作日志、内部工具目录）一律不进导出树。
# 新增需公开的顶层文件/目录时，必须显式加进 $include 白名单。
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\make_github_export.ps1
# ============================================================================

$repo = (Resolve-Path "$PSScriptRoot\..").Path
Push-Location $repo
# PS 5.1：git 会把进度写到 stderr，Stop 偏好会把它当异常终止脚本
$ErrorActionPreference = 'Continue'

$include = @('.gitignore', 'README.md', 'LICENSE', 'Argus.sln', 'FctAggregator', 'scripts', 'docs')
$branch = 'github-main'
# 审计 M15：提交信息版本号曾硬编码 v3.26.3 与 csproj 失真——改为从 csproj 读取
[xml]$csprojXml = Get-Content (Join-Path $repo 'FctAggregator/FctAggregator.csproj')
$csprojVer = ($csprojXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($csprojVer)) { Pop-Location; throw 'cannot read Version from FctAggregator.csproj' }
$msg = @"
Argus v$csprojVer - open-source export

Standalone FCT production-line test data collection and analysis tool (single machine, no server).
Single WinExe: collection engine, fail/todo loop, local analytics, feishu cards, hot upgrade.
MIT licensed. See README.md.
"@

function Fail([string]$step) { if ($LASTEXITCODE -ne 0) { throw "step failed: $step" } }

# 前置：工作区必须干净。非白名单的 tracked 文件在孤儿分支上会变成 untracked，
# 切回 main 需要 -f 强切；若带着未提交改动，强切会把它们静默丢掉。
git diff --quiet
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'working tree has uncommitted changes; commit or stash before exporting' }
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'index has staged changes; commit or reset before exporting' }

git show-ref --verify --quiet "refs/heads/$branch"
if ($LASTEXITCODE -eq 0) { git branch -D $branch | Out-Null }
git checkout --orphan $branch | Out-Null
Fail 'orphan checkout'

git rm -rf --cached . 2>$null | Out-Null
foreach ($p in $include) { git add -- $p }
Fail 'whitelist add'

$staged = git diff --cached --name-only
# 审计 C1：config.json（真实 agg_token）绝不允许进导出树；密钥值扫描兜底
$bad = $staged | Where-Object { $_ -in @('AGENT_RULES.md', 'CODEBUDDY.md', 'FctAggregator/config.json', 'config.json') -or $_ -match '^[.](?!gitignore)' }
if ($bad) { Pop-Location; throw "export tree contains non-whitelisted paths: $($bad -join ', ')" }
$leaks = @()
foreach ($f in $staged) {
    if ($f -notmatch '\.(json|md|ps1|cs|html|js|css|bat|txt)$') { continue }
    $txt = git show ":$f" 2>$null
    if ("$txt" -match '"(agg_token|webhook_url|agg_webhook_url|fallback_webhook_url)"\s*:\s*"[0-9a-fA-F]{16,}"') { $leaks += $f }
}
if ($leaks) { Pop-Location; throw "export tree contains secrets in: $($leaks -join ', ')" }
Write-Host ("export files: " + (git diff --cached --name-only).Count)

git commit -m $msg | Out-Null
Fail 'commit'
# 非白名单 tracked 文件（AGENT_RULES/CODEBUDDY 等）在孤儿分支上是 untracked，
# 普通 checkout 会以 "would be overwritten" 中止；前置已保证无未提交改动，故强切安全。
git checkout -f main | Out-Null
Fail 'back to main'
git push github "${branch}:main" --force
Fail 'push'
Write-Host "export pushed: github/main <- $branch (whitelist: $($include -join ', '))"
Pop-Location
