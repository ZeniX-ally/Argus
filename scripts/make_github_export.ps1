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
$msg = @'
Argus v3.26.3 - initial open-source release

FCT production-line test data collection, mesh aggregation and dashboard suite.
Single WinExe: collection engine, P2P mesh, web dashboard, feishu cards, hot upgrade.
MIT licensed. See README.md.
'@

function Fail([string]$step) { if ($LASTEXITCODE -ne 0) { throw "step failed: $step" } }

if (git show-ref --verify --quiet "refs/heads/$branch") { git branch -D $branch | Out-Null }
git checkout --orphan $branch | Out-Null
Fail 'orphan checkout'

git rm -rf --cached . 2>$null | Out-Null
foreach ($p in $include) { git add -- $p }
Fail 'whitelist add'

$staged = git diff --cached --name-only
$bad = $staged | Where-Object { $_ -in @('AGENT_RULES.md', 'CODEBUDDY.md') -or $_ -match '^[.](?!gitignore)' }
if ($bad) { Pop-Location; throw "export tree contains non-whitelisted paths: $($bad -join ', ')" }
Write-Host ("export files: " + (git diff --cached --name-only).Count)

git commit -m $msg | Out-Null
Fail 'commit'
git checkout main | Out-Null
Fail 'back to main'
git push github "${branch}:main" --force
Fail 'push'
Write-Host "export pushed: github/main <- $branch (whitelist: $($include -join ', '))"
Pop-Location
