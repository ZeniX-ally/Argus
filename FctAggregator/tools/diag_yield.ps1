<#
.SYNOPSIS
  Argus yield reconciliation diagnostic.

.DESCRIPTION
  Run on FCT machine (e.g. FCT4). Copy script next to Argus.exe:
    powershell -ExecutionPolicy Bypass -File .\diag_yield.ps1

  Or:
    .\diag_yield.ps1 -ConfigPath C:\Argus\config.json -ArgusDir C:\Argus

  Report saved to Desktop: Argus-yield-diag-yyyyMMdd-HHmmss.txt
#>
param(
    [string]$ConfigPath = '',
    [string]$ArgusDir = '',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$lines = New-Object System.Collections.Generic.List[string]
function W([string]$t = '') { $lines.Add($t); Write-Host $t }
function Progress([string]$t) {
    Write-Host "    >> $t" -ForegroundColor DarkCyan
    try { [Console]::Out.Flush() } catch {}
}

function Mask-Webhook([string]$url) {
    if ([string]::IsNullOrWhiteSpace($url)) { return '(not set)' }
    if ($url.Length -le 48) { return $url.Substring(0, [Math]::Min(40, $url.Length)) + '...' }
    return $url.Substring(0, 40) + '...'
}

function Find-ArgusDir {
    param([string]$Hint)
    if ($Hint -and (Test-Path (Join-Path $Hint 'Argus.exe'))) { return (Resolve-Path $Hint).Path }
    $cands = @(
        $PSScriptRoot,
        (Get-Location).Path,
        'C:\Argus',
        'D:\Argus',
        'C:\Program Files\Argus'
    ) | Select-Object -Unique
    foreach ($c in $cands) {
        if (Test-Path (Join-Path $c 'Argus.exe')) { return (Resolve-Path $c).Path }
    }
    return $null
}

function Find-Config {
    param([string]$Hint, [string]$AppDir)
    if ($Hint -and (Test-Path $Hint)) { return (Resolve-Path $Hint).Path }
    $cands = @()
    if ($AppDir) { $cands += (Join-Path $AppDir 'config.json') }
    $cands += @(
        (Join-Path $PSScriptRoot 'config.json'),
        (Join-Path (Get-Location) 'config.json'),
        'C:\Argus\config.json',
        (Join-Path $env:USERPROFILE 'Desktop\config.json')
    )
    foreach ($c in ($cands | Select-Object -Unique)) {
        if ($c -and (Test-Path $c)) { return (Resolve-Path $c).Path }
    }
    return $null
}

function Detect-StationByIp {
    # Do NOT use Get-NetIPAddress here: on some FCT images it hangs 30s+ (WMI/CIM).
    $map = @{
        '172.28.55.11' = 'FCT1'
        '172.28.55.12' = 'FCT2'
        '172.28.55.13' = 'FCT3'
        '172.28.55.14' = 'FCT4'
        '172.28.55.15' = 'FCT5'
        '172.28.55.16' = 'FCT6'
        '172.28.55.18' = 'FCT7'
    }
    $hits = @()
    try {
        [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
            ForEach-Object {
                $ip = $_.Address.ToString()
                if ($map.ContainsKey($ip)) { $hits += "$ip -> $($map[$ip])" }
            }
    } catch {}
    return $hits
}

function Test-ValidModel([string]$name) {
    return $name -match '^E\d{7}$'
}

function Count-XmlUnder {
    param([string]$Root, [string]$DateYmd)
    $stats = @{
        Total = 0; PassPrefix = 0; FailPrefix = 0; AutoPrefix = 0; Other = 0
        Online = 0; Offline = 0
        Models = @{}; SkippedDirs = @()
    }
    if (-not $Root -or -not (Test-Path $Root)) { return $stats }
    foreach ($cat in @('Online', 'Offline')) {
        $catDir = Join-Path $Root $cat
        if (-not (Test-Path $catDir)) { continue }
        foreach ($modelDir in (Get-ChildItem $catDir -Directory -ErrorAction SilentlyContinue)) {
            $model = $modelDir.Name
            if (-not (Test-ValidModel $model)) {
                $stats.SkippedDirs += "$cat/$model"
                continue
            }
            $dayDir = Join-Path $modelDir.FullName $DateYmd
            if (-not (Test-Path $dayDir)) { continue }
            $files = @(Get-ChildItem $dayDir -Filter '*.xml' -Recurse -File -ErrorAction SilentlyContinue)
            if ($files.Count -eq 0) { continue }
            if (-not $stats.Models.ContainsKey($model)) { $stats.Models[$model] = 0 }
            $stats.Models[$model] += $files.Count
            foreach ($f in $files) {
                $stats.Total++
                if ($cat -eq 'Online') { $stats.Online++ } else { $stats.Offline++ }
                $pfx = if ($f.Name.Length -ge 2) { $f.Name.Substring(0, 2).ToUpperInvariant() } else { '??' }
                switch ($pfx) {
                    'P_' { $stats.PassPrefix++ }
                    'F_' { $stats.FailPrefix++ }
                    'O_' { $stats.AutoPrefix++ }
                    default { $stats.Other++ }
                }
            }
        }
    }
    return $stats
}

function Format-DiskStats([hashtable]$s, [string]$label) {
    W "  [$label] XML total: $($s.Total)  (P_=$($s.PassPrefix)  F_=$($s.FailPrefix)  O_=$($s.AutoPrefix)  other=$($s.Other))"
    W "           Online=$($s.Online)  Offline=$($s.Offline)"
    if ($s.Models.Count -gt 0) {
        $modelLine = ($s.Models.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
        W "           models: $modelLine"
    }
    if ($s.SkippedDirs.Count -gt 0) {
        W "           [!] invalid model dirs (Argus skips): $($s.SkippedDirs -join ', ')"
    }
}

function Invoke-SqliteQuery {
    param([string]$DbPath, [string]$Sql)
    if (-not (Test-Path $DbPath)) { return $null }

    $sqlite3 = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if ($sqlite3) {
        try {
            $out = & sqlite3 -header -column $DbPath $Sql 2>&1
            if ($LASTEXITCODE -eq 0) { return ($out | Out-String).Trim() }
        } catch {}
    }

    if (-not $script:ArgusDirResolved) { return $null }
    $deps = @(
        'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll',
        'SQLitePCLRaw.batteries_v2.dll',
        'Microsoft.Data.Sqlite.dll'
    )
    foreach ($d in $deps) {
        $p = Join-Path $script:ArgusDirResolved $d
        if (Test-Path $p) {
            try { [void][Reflection.Assembly]::LoadFrom($p) } catch {}
        }
    }
    $md = Join-Path $script:ArgusDirResolved 'Microsoft.Data.Sqlite.dll'
    if (-not (Test-Path $md)) { return $null }
    try {
        [void][Reflection.Assembly]::LoadFrom($md)
        $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DbPath;Mode=ReadOnly")
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $Sql
        $r = $cmd.ExecuteReader()
        $rows = @()
        while ($r.Read()) {
            $cells = @()
            for ($i = 0; $i -lt $r.FieldCount; $i++) {
                $cells += if ($r.IsDBNull($i)) { '' } else { $r.GetValue($i).ToString() }
            }
            $rows += ($cells -join ' | ')
        }
        $r.Close()
        $conn.Close()
        return ($rows -join [Environment]::NewLine)
    } catch {
        return $null
    }
}

function Scan-LogPatterns {
    param([string]$LogPath)
    $patterns = @{
        hist_scan_end = 0
        ingest = 0
        ingest_fail = 0
        feishu_fail_push = 0
        skip_push = 0
        parse_giveup = 0
        unstable_file = 0
    }
    if (-not (Test-Path $LogPath)) { return $patterns }
    $todayDash = Get-Date -Format 'yyyy-MM-dd'
    try {
        $tail = Get-Content $LogPath -Tail 8000 -Encoding UTF8 -ErrorAction SilentlyContinue
        foreach ($line in $tail) {
            if ($line -notmatch $todayDash) { continue }
            # ASCII-only markers (log lines mix EN/CN; avoid CN literals in script encoding)
            if ($line -match 'xml=\d+.*\|') { $patterns.hist_scan_end++ }
            if ($line -match '\] .+ \| (PASS|FAIL|INTERRUPTED) \|') {
                $patterns.ingest++
                if ($line -match '\] .+ \| FAIL \|') { $patterns.ingest_fail++ }
            }
            if ($line -match 'FAIL /') { $patterns.feishu_fail_push++ }
            if ($line -match 'retry|Retry') { $patterns.parse_giveup++ }
            if ($line -match 'stable|Stable') { $patterns.unstable_file++ }
        }
    } catch {}
    return $patterns
}

# --- main ---
$script:ArgusDirResolved = Find-ArgusDir $ArgusDir
$cfgFile = Find-Config $ConfigPath $script:ArgusDirResolved

$today = Get-Date -Format 'yyyyMMdd'
$yesterday = (Get-Date).AddDays(-1).ToString('yyyyMMdd')
$diskToday = $null
$outToday = $null
$dbPass = 0
$dbFail = 0
$dbInt = 0

W '============================================================'
W ' Argus yield diagnostic'
W " time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W " host: $env:COMPUTERNAME  user: $env:USERNAME"
W '============================================================'
W ''

W '[1] station and Argus'
Progress 'detecting station by IP (fast path, no Get-NetIPAddress)...'
$ipStations = Detect-StationByIp
if ($ipStations.Count -gt 0) {
    foreach ($h in $ipStations) { W "    IP: $h" }
} else {
    W '    IP: (no match in 172.28.55.11-18)'
}
if ($script:ArgusDirResolved) {
    W "    Argus dir: $script:ArgusDirResolved"
} else {
    W '    Argus dir: (not found; use -ArgusDir or run from install folder)'
}
W ''

W '[2] config.json'
$resultsRoot = 'D:/Results'
$stationCfg = ''
$skipHist = $false
$autoStart = $false
if ($cfgFile) {
    W "    path: $cfgFile"
    try {
        $cfg = Get-Content $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $resultsRoot = [string]$cfg.results_root
        if ([string]::IsNullOrWhiteSpace($resultsRoot)) { $resultsRoot = 'D:/Results' }
        $stationCfg = [string]$cfg.station_id
        $skipHist = [bool]$cfg.skip_historical_scan
        if ($cfg.PSObject.Properties.Name -contains 'auto_start') { $autoStart = [bool]$cfg.auto_start }
        W "    station_id: $(if ($stationCfg) { $stationCfg } else { '(empty, use IP detect)' })"
        W "    results_root: $resultsRoot"
        W "    skip_historical_scan: $skipHist"
        W "    auto_start: $autoStart"
        W "    webhook: $(Mask-Webhook ([string]$cfg.webhook_url))"
        if ($cfg.PSObject.Properties.Name -contains 'parsers_path') {
            W "    [!] parsers_path: $($cfg.parsers_path)  (remove this key)"
        }
    } catch {
        W "    [X] parse error: $($_.Exception.Message)"
    }
} else {
    W '    (config.json not found; default results_root=D:/Results)'
}
$resolvedStation = if ($stationCfg) { $stationCfg } elseif ($ipStations.Count -gt 0) {
    if ($ipStations[0] -match '->\s*(\S+)') { $Matches[1] } else { '' }
} else { '' }
W "    resolved station: $(if ($resolvedStation) { $resolvedStation } else { '(none)' })"
W ''

W '[3] disk XML'
$rootPath = $resultsRoot -replace '/', '\'
Progress "counting XML under $rootPath (may take 1-2 min on network drive)..."
if (-not (Test-Path $rootPath)) {
    W "    [X] results_root missing: $rootPath"
} else {
    $diskToday = Count-XmlUnder $rootPath $today
    $diskYest = Count-XmlUnder $rootPath $yesterday
    Format-DiskStats $diskToday "today $today"
    W ''
    Format-DiskStats $diskYest "yesterday $yesterday"
}
W ''

W '[4] SQLite DB'
Progress 'querying SQLite database...'
$dbPath = $null
if ($script:ArgusDirResolved) {
    $dataDir = Join-Path $script:ArgusDirResolved 'data'
    if ($resolvedStation) {
        $cand = Join-Path $dataDir "$resolvedStation.db"
        if (Test-Path $cand) { $dbPath = $cand }
    }
    if (-not $dbPath -and (Test-Path $dataDir)) {
        $dbs = @(Get-ChildItem $dataDir -Filter '*.db' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if ($dbs.Count -gt 0) { $dbPath = $dbs[0].FullName }
    }
}
if (-not $dbPath) {
    W '    (no data/*.db found)'
} else {
    W "    db: $dbPath"
    $stationFilter = if ($resolvedStation) { " AND station_id = '$resolvedStation'" } else { '' }
    # 审计 F10：test_date 有 yyyyMMdd 与 yyyy-MM-dd 两种格式（主程序 TestDateEqDay 双格式），
    # 单格式等值会少算 dash 行 → 误报「disk>DB 疑漏采」。这里同样双格式。
    $todayDash = (Get-Date).ToString('yyyy-MM-dd')
    $dateEq = "(test_date = '$today' OR test_date = '$todayDash')"
    $sqlToday = "SELECT result, COUNT(*) AS cnt FROM test_records WHERE $dateEq$stationFilter GROUP BY result;"
    $outToday = Invoke-SqliteQuery $dbPath $sqlToday
    if ($outToday) {
        W "    DB today test_date=$today (folder date, same as Argus UI):"
        $outToday -split "`n" | ForEach-Object { W "      $_" }
        foreach ($row in ($outToday -split "`n")) {
            if ($row -match 'PASS\s*\|\s*(\d+)') { $dbPass = [int]$Matches[1] }
            if ($row -match 'FAIL\s*\|\s*(\d+)') { $dbFail = [int]$Matches[1] }
            if ($row -match 'INTERRUPTED\s*\|\s*(\d+)') { $dbInt = [int]$Matches[1] }
        }
    } else {
        W '    (DB query failed; use Argus debug: Today stats / DB status)'
    }

    $sqlTs = "SELECT result, COUNT(*) AS cnt FROM test_records WHERE batch_ts_date = '$(Get-Date -Format 'yyyy-MM-dd')'$stationFilter GROUP BY result;"
    $outTs = Invoke-SqliteQuery $dbPath $sqlTs
    if ($outTs) {
        W "    DB today batch_ts_date (test timestamp):"
        $outTs -split "`n" | ForEach-Object { W "      $_" }
    }

    $sqlFail = "SELECT substr(COALESCE(batch_timestamp, created_at), 1, 19) AS ts, sn, model, fail_reason FROM test_records WHERE $dateEq AND result = 'FAIL'$stationFilter ORDER BY id DESC LIMIT 10;"
    $outFail = Invoke-SqliteQuery $dbPath $sqlFail
    if ($outFail) {
        W '    recent FAIL (max 10):'
        $outFail -split "`n" | ForEach-Object { W "      $_" }
    }
}
W ''

W '[5] app.log today'
$logPath = if ($script:ArgusDirResolved) { Join-Path $script:ArgusDirResolved 'logs\app.log' } else { $null }
if (-not $logPath -or -not (Test-Path $logPath)) {
    W '    (logs\app.log not found)'
} else {
    W "    log: $logPath"
    $lp = Scan-LogPatterns $logPath
    W ("    {0,-22} {1}" -f 'hist_scan_end', $lp.hist_scan_end)
    W ("    {0,-22} {1}" -f 'ingest', $lp.ingest)
    W ("    {0,-22} {1}" -f 'ingest_FAIL', $lp.ingest_fail)
    W ("    {0,-22} {1}" -f 'feishu_FAIL_push', $lp.feishu_fail_push)
    W ("    {0,-22} {1}" -f 'skip_push', $lp.skip_push)
    W ("    {0,-22} {1}" -f 'parse_retry', $lp.parse_giveup)
    W ("    {0,-22} {1}" -f 'unstable_file', $lp.unstable_file)
    if ($lp.ingest_fail -gt 0 -and $lp.feishu_fail_push -lt $lp.ingest_fail) {
        W '    [!] ingest FAIL > feishu push: backfill ingested FAIL without alert'
    }
}
W ''

W '[6] summary'
$diskTotal = if ($diskToday) { $diskToday.Total } else { -1 }
$dbTotal = $dbPass + $dbFail
if ($diskTotal -ge 0 -and $dbTotal -ge 0) {
    if ($diskTotal -gt $dbTotal + 2) {
        $gap = $diskTotal - $dbTotal
        W "    disk today ($diskTotal) > DB PASS+FAIL ($dbTotal), gap=$gap -> likely missed scan/parse"
    } elseif ($diskTotal -lt $dbTotal - 2) {
        W "    DB ($dbTotal) > disk today ($diskTotal) -> check results_root path"
    } else {
        W "    disk and DB are close (disk=$diskTotal, db=$dbTotal)"
        W '    if company platform still differs, data source may not be the same XML tree'
    }
}
if (-not $autoStart) {
    W '    [!] auto_start=false: Argus not auto-started with Windows'
}
if ($skipHist) {
    W '    [!] skip_historical_scan=true: only today folder scanned on startup'
}
if ([string]::IsNullOrWhiteSpace($stationCfg)) {
    W '    [!] station_id empty: set e.g. FCT4 in config.json'
}
W ''
W '============================================================'
W ' Send this full report to developer'
W '============================================================'

if (-not $OutFile) {
    $OutFile = Join-Path $env:USERPROFILE "Desktop\Argus-yield-diag-$(Get-Date -Format 'yyyyMMdd-HHmmss').txt"
}
try {
    $lines | Set-Content -Path $OutFile -Encoding UTF8
    W ''
    W "saved: $OutFile"
} catch {
    W "save failed: $($_.Exception.Message)"
}
