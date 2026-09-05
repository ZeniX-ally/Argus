[CmdletBinding()]
param(
    [switch]$Compare,
    [string]$OutRoot = "C:\Users\admin\Documents\xlsx-check",
    [string]$OldRef  = "a06d9b7"
)

$ErrorActionPreference = 'Stop'
$repo    = (Resolve-Path "$PSScriptRoot\..\..").Path
$proj    = Join-Path $repo 'FctAggregator'
$fixture = Join-Path $OutRoot '_fixture'
$newDir  = Join-Path $OutRoot 'new'
$oldDir  = Join-Path $OutRoot 'old'
$wt      = Join-Path $repo '.wt-xlsxcmp'

Write-Host "[1/4] 准备 fixture ..." -ForegroundColor Cyan
if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
$rDir = Join-Path $fixture 'Results\Offline\E3002781\20260722'
$tDir = Join-Path $fixture 'TDMS Log\Offline\E3002781\20260722'
New-Item -ItemType Directory -Path $rDir, $tDir -Force | Out-Null

Get-ChildItem (Join-Path $proj 'modules\Distributor\tpl') -Filter *.xml |
    Copy-Item -Destination $rDir
$fetcherFx = Join-Path $repo 'Releases\fct-fetcher-cs-old\selftest'
if (Test-Path $fetcherFx) {
    Get-ChildItem "$fetcherFx\Results" -Recurse -Filter *.xml | Copy-Item -Destination $rDir
    Get-ChildItem "$fetcherFx\Results" -Recurse -Filter *.csv | Copy-Item -Destination $rDir
    Get-ChildItem "$fetcherFx\TDMS Log" -Recurse -Filter *.tdms | Copy-Item -Destination $tDir
}
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

$dbSrc = Join-Path $proj 'dist\data\fct.db'
$dbCopy = ''
if (Test-Path $dbSrc) {
    $dbCopy = Join-Path $fixture 'maint.db'
    Copy-Item $dbSrc $dbCopy
    Write-Host "      维修记录库副本: $([math]::Round((Get-Item $dbCopy).Length/1KB)) KB"
} else {
    Write-Warning "      没找到 $dbSrc —— 第 4 张表（维修记录）会跳过"
}

Write-Host "[2/4] 用当前代码（去重后）导出 ..." -ForegroundColor Cyan
if (Test-Path $newDir) { Remove-Item $newDir -Recurse -Force }
Push-Location $proj
dotnet run --project tools\xlsxdump\XlsxDump.csproj -c Release -- $newDir $fixture $dbCopy
$rc = $LASTEXITCODE
Pop-Location
if ($rc -ne 0) { throw "new 导出失败（exit $rc）" }

if ($Compare) {
    Write-Host "[3/4] 检出 $OldRef（v2.9.2，去重前）并导出 ..." -ForegroundColor Cyan
    if (Test-Path $wt) { git -C $repo worktree remove $wt --force 2>$null; Remove-Item $wt -Recurse -Force -ErrorAction SilentlyContinue }
    git -C $repo worktree add --detach $wt $OldRef | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "git worktree add 失败" }

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
