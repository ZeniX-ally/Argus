# xlsx 样式核对：拿同一份 fixture，用「去重后(new)」与「去重前 v2.9.2(old)」各导一次
# 四张 xlsx，然后逐字节/逐属性对比，最后打开 new 的四个文件供人眼确认。
#
# 用法：
#   .\tools\xlsx_check.ps1              # 只导 new（快，只看长相）
#   .\tools\xlsx_check.ps1 -Compare     # 额外检出 v2.9.2 导一份并对比（推荐）
#
# 输出目录：C:\Users\admin\Documents\xlsx-check\{new,old,_fixture}
[CmdletBinding()]
param(
    [switch]$Compare,
    [string]$OutRoot = "C:\Users\admin\Documents\xlsx-check",
    [string]$OldRef  = "a06d9b7"     # v2.9.2 = xlsx 去重前的最后一个提交
)

$ErrorActionPreference = 'Stop'
$repo    = (Resolve-Path "$PSScriptRoot\..\..").Path      # 仓库根（Argus）
$proj    = Join-Path $repo 'FctAggregator'
$fixture = Join-Path $OutRoot '_fixture'
$newDir  = Join-Path $OutRoot 'new'
$oldDir  = Join-Path $OutRoot 'old'
$wt      = Join-Path $repo '.wt-xlsxcmp'

# ---------------- 1. 准备 fixture（新旧共用同一份，保证唯一变量是代码） ----------------
Write-Host "[1/4] 准备 fixture ..." -ForegroundColor Cyan
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
$rDir = Join-Path $fixture 'Results\Offline\E3002781\20260722'
$tDir = Join-Path $fixture 'TDMS Log\Offline\E3002781\20260722'
New-Item -ItemType Directory -Path $rDir, $tDir -Force | Out-Null

# 真实 XML：分发工具的模板（P_ 488KB 全 PASS / O_ 22KB 中断）
Get-ChildItem (Join-Path $proj 'modules\Distributor\tpl') -Filter *.xml |
    Copy-Item -Destination $rDir
# 真实 FAIL XML + TDMS：取数工具旧自检数据集
$fetcherFx = Join-Path $repo 'Releases\fct-fetcher-cs-old\selftest'
if (Test-Path $fetcherFx) {
    Get-ChildItem "$fetcherFx\Results" -Recurse -Filter *.xml | Copy-Item -Destination $rDir
    Get-ChildItem "$fetcherFx\Results" -Recurse -Filter *.csv | Copy-Item -Destination $rDir
    Get-ChildItem "$fetcherFx\TDMS Log" -Recurse -Filter *.tdms | Copy-Item -Destination $tDir
}
# 斑马纹/多行样式要有足够行数才看得出来：把 FAIL 样本按不同 SN 各再复制 3 份
# （只改文件名里的 SN 段与时间戳，内容不动；确定性，无随机）
foreach ($f in @(Get-ChildItem $rDir -Filter 'F_*.xml')) {
    $parts = $f.BaseName.Split('_')
    $snIdx = 5
    if ($parts.Length -gt $snIdx) {
        for ($k = 1; $k -le 3; $k++) {
            $p2 = $parts.Clone()
            $p2[$snIdx] = $parts[$snIdx].Substring(0, $parts[$snIdx].Length - 2) + ('X' + $k)
            Copy-Item $f.FullName (Join-Path $rDir (($p2 -join '_') + '.xml'))
        }
    }
}
$xmlN = (Get-ChildItem $rDir -Filter *.xml).Count
Write-Host "      Results: $xmlN 个 XML；TDMS: $((Get-ChildItem $tDir -Filter *.tdms).Count) 个"

# 维修记录：真实库的**副本**（Database 构造会写库，绝不碰原库）
$dbSrc = Join-Path $proj 'dist\data\fct.db'
$dbCopy = ''
if (Test-Path $dbSrc) {
    $dbCopy = Join-Path $fixture 'maint.db'
    Copy-Item $dbSrc $dbCopy
    Write-Host "      维修记录库副本: $([math]::Round((Get-Item $dbCopy).Length/1KB)) KB"
} else {
    Write-Warning "      没找到 $dbSrc —— 第 4 张表（维修记录）会跳过"
}

# ---------------- 2. 导 new ----------------
Write-Host "[2/4] 用当前代码（去重后）导出 ..." -ForegroundColor Cyan
if (Test-Path $newDir) { Remove-Item $newDir -Recurse -Force }
Push-Location $proj
dotnet run --project tools\xlsxdump\XlsxDump.csproj -c Release -- $newDir $fixture $dbCopy
$rc = $LASTEXITCODE
Pop-Location
if ($rc -ne 0) { throw "new 导出失败（exit $rc）" }

# ---------------- 3. 导 old（v2.9.2，去重前） ----------------
if ($Compare) {
    Write-Host "[3/4] 检出 $OldRef（v2.9.2，去重前）并导出 ..." -ForegroundColor Cyan
    if (Test-Path $wt) { git -C $repo worktree remove $wt --force 2>$null; Remove-Item $wt -Recurse -Force -ErrorAction SilentlyContinue }
    git -C $repo worktree add --detach $wt $OldRef | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "git worktree add 失败" }

    # 把 harness 原样搬进旧代码（它只调高层入口，旧版一样能编）
    $wtProj = Join-Path $wt 'FctAggregator'
    Copy-Item (Join-Path $proj 'tools\xlsxdump') (Join-Path $wtProj 'tools\xlsxdump') -Recurse -Force
    if (Test-Path $oldDir) { Remove-Item $oldDir -Recurse -Force }
    Push-Location $wtProj
    dotnet run --project tools\xlsxdump\XlsxDump.csproj -c Release -- $oldDir $fixture $dbCopy
    $rc = $LASTEXITCODE
    Pop-Location
    if ($rc -ne 0) { throw "old 导出失败（exit $rc）" }

    Write-Host "[4/4] 对比 ..." -ForegroundColor Cyan
    python (Join-Path $PSScriptRoot 'xlsx_cmp.py') $oldDir $newDir
    $cmp = $LASTEXITCODE

    git -C $repo worktree remove $wt --force
    if ($cmp -ne 0) { Write-Warning "对比发现差异，见上方报告" } else { Write-Host "对比：完全一致" -ForegroundColor Green }
} else {
    Write-Host "[3/4] 跳过 old 对比（加 -Compare 打开）" -ForegroundColor DarkGray
    Write-Host "[4/4] -" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "产物（用 Excel 打开人眼确认配色/列宽/冻结/合并）：" -ForegroundColor Yellow
Get-ChildItem $newDir -Filter *.xlsx | ForEach-Object {
    Write-Host ("  {0,-30} {1,8:N0} 字节" -f $_.Name, $_.Length)
}
Write-Host ""
Write-Host "打开：explorer `"$newDir`""
