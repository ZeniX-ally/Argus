<#
.SYNOPSIS
  Argus environment collector - run on the FCT machine, send the output back.

.DESCRIPTION
  Read-only: does NOT write the results tree, does NOT modify the database,
  does NOT open serial ports, does NOT start/stop Argus.

  Collects every path / config / environment fact Argus needs at runtime and
  copies the files required to rebuild an identical test environment locally:
  config.json, FCT.ini, parsers.json, sample result XMLs, database snapshot.

  Usage (on the machine, from anywhere):
    powershell -NoProfile -ExecutionPolicy Bypass -File .\collect_env.ps1
  or just double-click collect_env.bat

  Output: Desktop\Argus-env-<HOST>-<timestamp>\  plus a .zip next to it.

  Options:
    -ArgusDir C:\Argus        force the Argus install folder if not auto-found
    -OutDir D:\temp\env       output folder (default: Desktop)
    -XmlDays 2                copy result XMLs from the last N days (default 2)
    -SampleCount 8            max sample XMLs to copy (default 4)
    -DbCopyMaxMb 200          skip db copy if larger than this (default 200)
    -SkipXmlCopy / -SkipDbCopy
    -DeepIniSearch            full-disk FCT.ini search (slow; default is bounded)
    -NoZip                    do not create the zip
#>
param(
    [string]$ArgusDir = '',
    [string]$ConfigPath = '',
    [string]$OutDir = '',
    [int]$SampleCount = 4,
    [int]$SampleMaxKb = 4096,
    [int]$XmlDays = 2,
    [int]$MaxModelDirs = 30,
    [int]$DbCopyMaxMb = 200,
    [switch]$SkipXmlCopy,
    [switch]$SkipDbCopy,
    [switch]$NoZip,
    [switch]$DeepIniSearch
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

# ============================ report buffers ============================
$script:Lines = New-Object System.Collections.Generic.List[string]
$script:Issues = New-Object System.Collections.Generic.List[string]
$script:Notes = New-Object System.Collections.Generic.List[string]

function W([string]$t = '') { $script:Lines.Add($t); Write-Host $t }
function SEC([string]$t) { W ''; W "==== $t " + ('=' * [Math]::Max(0, 64 - $t.Length)) }
function SUB([string]$t) { W ''; W "  -- $t" }
function OK([string]$t) { W "    [OK]   $t" }
function WARN([string]$t) { W "    [!]    $t"; $script:Notes.Add($t) }
function BAD([string]$t) { W "    [X]    $t"; $script:Issues.Add($t) }
function INFO([string]$t) { W "           $t" }
function P([string]$t) { Write-Host "    >> $t" -ForegroundColor DarkCyan; try { [Console]::Out.Flush() } catch {} }

# ============================ helpers ============================

# read a file even if another process holds it open (FCT.ini, app.log)
function Read-FileText {
    param([string]$Path)
    if (-not $Path) { return $null }
    $fs = $null
    try {
        $fs = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $ms = New-Object System.IO.MemoryStream
        $fs.CopyTo($ms)
        $bytes = $ms.ToArray()
        $ms.Close(); $fs.Close(); $fs = $null
    }
    catch {
        if ($fs) { try { $fs.Close() } catch {} }
        return $null
    }
    if (-not $bytes -or $bytes.Length -eq 0) { return '' }
    try {
        $strict = New-Object System.Text.UTF8Encoding($false, $true)
        $s = $strict.GetString($bytes)
    }
    catch {
        try { $s = [System.Text.Encoding]::GetEncoding(936).GetString($bytes) } catch { $s = '' }
    }
    return $s.TrimStart([char]0xFEFF)
}

function Copy-FileSafe {
    param([string]$From, [string]$To)
    try {
        $dir = Split-Path $To -Parent
        if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [System.IO.File]::Copy($From, $To, $true)
        return $true
    }
    catch { WARN "copy failed: $From -> $($_.Exception.Message)"; return $false }
}

function Get-SizeStr {
    param([string]$Path)
    try {
        $len = (Get-Item -LiteralPath $Path -Force).Length
        if ($len -ge 1073741824) { return ('{0:N2} GB' -f ($len / 1073741824)) }
        if ($len -ge 1048576) { return ('{0:N1} MB' -f ($len / 1048576)) }
        if ($len -ge 1024) { return ('{0:N1} KB' -f ($len / 1024)) }
        return "$len B"
    }
    catch { return '?' }
}

function Mask-Secret {
    param([string]$V)
    if ([string]::IsNullOrWhiteSpace($V)) { return '(not set)' }
    $s = $V.Trim()
    if ($s.Length -le 48) { return $s.Substring(0, [Math]::Min(40, $s.Length)) + '...' }
    return $s.Substring(0, 40) + '...'
}

function Norm-Val {
    param($V)
    if ($null -eq $V) { return '' }
    if ($V -is [bool]) { if ($V) { return 'true' } else { return 'false' } }
    return ([string]$V).Trim().ToLowerInvariant()
}

function Check-Path {
    param([string]$Name, [string]$Path, [string]$Purpose, [string]$Source, [bool]$Must)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        W ('    [ -- ] {0,-22} = (empty)' -f $Name)
        INFO "purpose: $Purpose | source: $Source"
        return
    }
    $norm = $Path -replace '/', '\'
    $exists = $false
    try { $exists = Test-Path -LiteralPath $norm } catch {}
    if ($exists) { OK ('{0,-22} = {1}' -f $Name, $norm) }
    elseif ($Must) { BAD ('{0,-22} = {1}   <== REQUIRED but MISSING' -f $Name, $norm) }
    else { WARN ('{0,-22} = {1}   (optional; degrades gracefully)' -f $Name, $norm) }
    INFO "purpose: $Purpose | source: $Source"
}

function Test-ArgusDir([string]$Dir) {
    if (-not $Dir) { return $false }
    try { return (Test-Path -LiteralPath (Join-Path $Dir 'Argus.exe')) } catch { return $false }
}

# ============================ output folder ============================
if (-not $OutDir) {
    $desk = [Environment]::GetFolderPath('Desktop')
    if (-not $desk) { $desk = $env:USERPROFILE }
    $OutDir = Join-Path $desk ("Argus-env-{0}-{1}" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
try {
    if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
}
catch {
    Write-Host "[X] cannot create output folder: $OutDir ($($_.Exception.Message))" -ForegroundColor Red
    exit 1
}
$BundleDir = Join-Path $OutDir 'bundle'
$SamplesDir = Join-Path $BundleDir 'samples'
foreach ($d in @($BundleDir, $SamplesDir)) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
}
$ReportPath = Join-Path $OutDir 'ENV-REPORT.txt'

# ============================ banner ============================
$isAdmin = $false
try {
    $wp = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
    $isAdmin = $wp.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}
catch {}
$osv = ''
try { $osv = [System.Environment]::OSVersion.VersionString } catch {}
$clrv = ''
try { $clrv = [string][System.Environment]::Version } catch {}
$tzName = ''
try { $tzName = [System.TimeZoneInfo]::Local.DisplayName } catch {}
$sysCs = ''
try { $sysCs = [System.Globalization.CultureInfo]::InstalledUICulture.Name } catch {}
$hostName = $env:COMPUTERNAME
$today = Get-Date -Format 'yyyyMMdd'
$todayDash = Get-Date -Format 'yyyy-MM-dd'
$yesterday = (Get-Date).AddDays(-1).ToString('yyyyMMdd')

W '============================================================'
W ' Argus environment collector'
W " time: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W " host: $hostName  user: $env:USERNAME  admin: $isAdmin"
W " out : $OutDir"
W '============================================================'

# ============================ [1] station and Argus ============================
SEC '[1] station and Argus'
P 'detecting station by IP (fast path, no Get-NetIPAddress)...'
$ipMap = [ordered]@{
    '172.28.55.11' = 'FCT1'; '172.28.55.12' = 'FCT2'; '172.28.55.13' = 'FCT3'; '172.28.55.14' = 'FCT4'
    '172.28.55.15' = 'FCT5'; '172.28.55.16' = 'FCT6'; '172.28.55.18' = 'FCT7'
}
$myIps = New-Object System.Collections.Generic.List[string]
try {
    [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object {
        $ni = $_
        try {
            if ($ni.OperationalStatus -ne 'Up') { return }
            if ($ni.NetworkInterfaceType -eq 'Loopback') { return }
            foreach ($ua in $ni.GetIPProperties().UnicastAddresses) {
                $ip = $ua.Address.ToString()
                if ($ip -match '^\d+\.\d+\.\d+\.\d+$') { $myIps.Add($ip) }
            }
        }
        catch {}
    }
}
catch {}
$ipList = @($myIps | Select-Object -Unique)
$ipHit = ''
foreach ($ip in $ipList) {
    if ($ipMap.Contains($ip)) {
        $ipHit = $ipMap[$ip]
        W "    IP: $ip -> $ipHit"
    }
}
if (-not $ipHit) {
    if ($ipList.Count -gt 0) { W "    IP: $($ipList -join ', ') (no match in 172.28.55.11-18)" }
    else { W '    IP: (could not read adapters)' }
    WARN 'host IP is not in the 172.28.55.11-18 map: IP based station detection will fail'
}

P 'locating Argus.exe...'
$script:AppDir = ''
if (Test-ArgusDir $ArgusDir) { $script:AppDir = (Resolve-Path -LiteralPath $ArgusDir).Path }
if (-not $script:AppDir) {
    $cands = @(
        $PSScriptRoot,
        (Get-Location).Path,
        'C:\Argus', 'D:\Argus', 'E:\Argus',
        'C:\Program Files\Argus', 'D:\Program Files\Argus',
        'C:\FCT-Aggregator-CS\FCT-Aggregator-CS'
    )
    foreach ($c in ($cands | Select-Object -Unique)) {
        if (Test-ArgusDir $c) { $script:AppDir = (Resolve-Path -LiteralPath $c).Path; break }
    }
}
if (-not $script:AppDir) {
    # bounded fallback: top-level dirs matching *Argus*/*FCT* on fixed drives, then 1 level deeper
    $drives = @()
    try { $drives = [System.IO.DriveInfo]::GetDrives() } catch {}
    foreach ($d in $drives) {
        try {
            if (-not $d.IsReady -or $d.DriveType -ne 'Fixed') { continue }
            $subs = @(Get-ChildItem -LiteralPath $d.RootDirectory.FullName -Directory -ErrorAction SilentlyContinue |
                      Where-Object { $_.Name -match 'Argus|FCT' })
            foreach ($s in $subs) {
                if (Test-ArgusDir $s.FullName) { $script:AppDir = $s.FullName; break }
                $sub2 = @(Get-ChildItem -LiteralPath $s.FullName -Directory -ErrorAction SilentlyContinue)
                foreach ($s2 in $sub2) {
                    if (Test-ArgusDir $s2.FullName) { $script:AppDir = $s2.FullName; break }
                }
                if ($script:AppDir) { break }
            }
        }
        catch {}
        if ($script:AppDir) { break }
    }
}
if ($script:AppDir) {
    OK "Argus dir: $script:AppDir"
}
else {
    BAD 'Argus.exe not found (use -ArgusDir to point at the install folder)'
}

$argusVersion = ''
if ($script:AppDir) {
    $exe = Join-Path $script:AppDir 'Argus.exe'
    try {
        $fi = Get-Item -LiteralPath $exe
        $vi = $fi.VersionInfo
        $argusVersion = [string]$vi.FileVersion
        W "    Argus.exe : version $($vi.FileVersion)   size $(Get-SizeStr $exe)   modified $($fi.LastWriteTime)"
        W "    product   : $($vi.ProductName) / $($vi.CompanyName)"
    }
    catch { WARN "cannot read Argus.exe version: $($_.Exception.Message)" }

    SUB 'install folder contents'
    try {
        foreach ($f in @(Get-ChildItem -LiteralPath $script:AppDir -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
            $ver = ''
            if ($f.Extension -eq '.exe' -or $f.Extension -eq '.dll') {
                try { $ver = '  v' + $f.VersionInfo.FileVersion } catch {}
            }
            W ('      {0,-40} {1,10}  {2}{3}' -f $f.Name, (Get-SizeStr $f.FullName), $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm'), $ver)
        }
        foreach ($sd in @(Get-ChildItem -LiteralPath $script:AppDir -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
            W "      [dir] $($sd.Name)"
        }
    }
    catch { WARN "listing install folder failed: $($_.Exception.Message)" }
    $oldFiles = @()
    try { $oldFiles = @(Get-ChildItem -LiteralPath $script:AppDir -Filter '*.old' -File -ErrorAction SilentlyContinue) } catch {}
    if ($oldFiles.Count -gt 0) {
        WARN "$($oldFiles.Count) leftover *.old files (unfinished hot swap): $(($oldFiles.Name) -join ', ')"
    }

    SUB 'autostart'
    try {
        $startup = [Environment]::GetFolderPath('Startup')
        $lnk = Join-Path $startup 'Argus.lnk'
        if (Test-Path -LiteralPath $lnk) {
            OK "startup shortcut: $lnk"
            try {
                $sh = New-Object -ComObject WScript.Shell
                $t = $sh.CreateShortcut($lnk)
                W "           target : $($t.TargetPath)"
                W "           workdir: $($t.WorkingDirectory)"
                W "           args   : $($t.Arguments)"
            }
            catch {}
        }
        else { WARN "no Argus.lnk in startup folder: $startup" }
    }
    catch {}

    SUB 'processes'
    try {
        $procs = @(Get-Process -Name Argus -ErrorAction SilentlyContinue)
        if ($procs.Count -gt 0) {
            foreach ($pr in $procs) {
                $mod = ''
                try { $mod = $pr.MainModule.FileName } catch { $mod = '(needs admin)' }
                W "    Argus running: PID $($pr.Id)  started $($pr.StartTime)  path $mod"
            }
        }
        else { W '    Argus is not running' }
        foreach ($o in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'XPT|FTS|TestStand|LabVIEW' })) {
            $op = ''
            try { $op = $o.MainModule.FileName } catch { $op = '(needs admin)' }
            W "    tester software: $($o.ProcessName) (PID $($o.Id)) -> $op"
        }
    }
    catch {}
}

# ============================ [2] config.json ============================
SEC '[2] config.json'
P 'reading config...'
$cfgFile = ''
$cfgObj = $null
if ($ConfigPath -and (Test-Path -LiteralPath $ConfigPath)) {
    $cfgFile = (Resolve-Path -LiteralPath $ConfigPath).Path
}
elseif ($script:AppDir) {
    $c = Join-Path $script:AppDir 'config.json'
    if (Test-Path -LiteralPath $c) { $cfgFile = $c }
}
if (-not $cfgFile) {
    foreach ($c in @((Join-Path $PSScriptRoot 'config.json'), (Join-Path (Get-Location).Path 'config.json'), 'C:\Argus\config.json')) {
        if (Test-Path -LiteralPath $c) { $cfgFile = (Resolve-Path -LiteralPath $c).Path; break }
    }
}

# known keys and their defaults (Infra/Config.cs)
$KnownKeys = [ordered]@{
    'station_id'                      = ''
    'results_root'                    = 'D:\Results'
    'fct_ini_path'                    = 'C:\FTS\Apps\PEU\Cfg\FCT.ini'
    'webhook_url'                     = ''
    'skip_historical_scan'            = $false
    'log_level'                       = 'INFO'
    'desktop_notify'                  = $true
    'notify_min_interval_sec'         = 15
    'todo_scan_days'                  = 30
    'feishu_banner_img_key'           = ''
    'feishu_banner_img_key_red'       = ''
    'feishu_banner_img_key_orange'    = ''
    'feishu_banner_img_key_blue'      = ''
    'feishu_banner_img_key_green'     = ''
    'feishu_banner_img_key_yellow'    = ''
    'parsers_path'                    = 'parsers.json'
    'todo_spec_merge'                 = $true
    'db_maintenance_hour'             = 3
    'auto_update'                     = $true
    'update_source'                   = ''
    'learn_baseline_enabled'          = $false
    'learn_resource_sampling_enabled' = $false
    'learn_priority_enabled'          = $false
    'learn_fail_merge_enabled'        = $false
    'learn_fail_merge_level'          = 'signal'
    'learn_baseline_window_days'      = 7
    'learn_baseline_sigma'            = 3.0
    'learn_baseline_min_samples'      = 30
    'learn_normal_enabled'            = $true
    'learn_normal_min_samples'        = 30
    'learn_normal_stale_days'         = 14
    'learn_normal_vote_signals'       = 3
    'learn_normal_event_score'        = 75
    'learn_normal_max_events_per_day' = 10
    'learn_group_alert_min'           = 3
    'learn_resource_retention_days'   = 14
    'analyze_collect_pass'            = $true
    'analyze_max_file_kb'             = 2048
    'analyze_max_tests_per_file'      = 1000
    'analyze_drift_enabled'           = $false
    'analyze_cpk_enabled'             = $false
    'analyze_window_days'             = 7
    'analyze_min_samples'             = 30
    'analyze_drift_sigma'             = 3.0
    'analyze_drift_warn_ratio'        = 1.0
    'analyze_measure_retention_days'  = 90
    'analyze_failitem_retention_days' = 90
    'analyze_tdms_enabled'            = $true
    'tdms_root'                       = ''
    'analyze_tdms_max_kb'             = 65536
    'analyze_tdms_max_channels'       = 400
    'analyze_fail_attr_enabled'       = $true
    'analyze_fixture_enabled'         = $false
    'analyze_attr_min_bucket'         = 20
    'analyze_attr_dev_ratio'          = 1.5
    'analyze_effect_d'                = 0.8
    # ── 审计 F6：以下 25 个键是 v3.41~v3.44 飞书卡片/存储监控/备份/更新目录期新增的，
    #    漏登记会把现场生效配置 WARN 成「Argus does not read… consider deleting」——现场误删即丢功能。
    #    默认值与 config.example.json / Infra/Config.cs 对齐。
    'feishu_fail_merge_enabled'           = $true
    'feishu_fail_merge_window_sec'        = 60
    'feishu_fail_merge_max'               = 20
    'feishu_collect_alert_enabled'        = $true
    'feishu_collect_alert_window_min'     = 60
    'feishu_collect_alert_parse_min'      = 10
    'feishu_collect_alert_retry_min'      = 20
    'feishu_collect_alert_throttle_min'   = 60
    'feishu_daily_summary_enabled'        = $true
    'feishu_group_alert_enabled'          = $true
    'feishu_deviation_alert_enabled'      = $true
    'feishu_deviation_min_score'          = 90
    'feishu_deviation_max_per_day'        = 5
    'feishu_storage_alert_enabled'        = $true
    'feishu_storage_alert_score'          = 60
    'feishu_todo_overdue_enabled'         = $true
    'feishu_todo_overdue_days'            = 3
    'feishu_todo_overdue_min_severity'    = 'critical'
    'update_dir'                          = 'data/updates'
    'db_storage_auto_optimize'            = $true
    'db_storage_monitor_hours'            = 4
    'db_vacuum_threshold_mb'              = 500
    'db_parse_failure_retention_days'     = 30
    'fct_program_source_root'             = 'C:/FTS'
    'fct_program_backup_dir'              = 'D:/backup'
    'fct_program_backup_enabled'          = $true
    'fct_program_backup_keep'             = 10
}
# keys Argus itself does not read but the updater / autostart uses - keep them
$KeepKeys = @('auto_start', 'fts')

$resultsRoot = 'D:\Results'
$cfgIniPath = 'C:\FTS\Apps\PEU\Cfg\FCT.ini'
$cfgStation = ''
$skipHist = $false

if ($cfgFile) {
    OK "path: $cfgFile   ($(Get-SizeStr $cfgFile), modified $((Get-Item -LiteralPath $cfgFile).LastWriteTime))"
    $cfgText = Read-FileText $cfgFile
    if ($null -eq $cfgText) {
        BAD 'cannot read config.json (locked or no permission)'
    }
    else {
        try {
            $cfgObj = $cfgText | ConvertFrom-Json
            OK 'JSON parsed'
        }
        catch {
            BAD "JSON parse failed: $($_.Exception.Message)  -- Argus would fall back to defaults / data\config_backups"
        }
    }
    Copy-FileSafe $cfgFile (Join-Path $BundleDir 'config.json') | Out-Null
}
else {
    BAD 'config.json not found (Argus uses built-in defaults: results_root=D:\Results)'
}

if ($cfgObj) {
    if ($cfgObj.PSObject.Properties.Name -contains 'results_root') {
        $tmp = [string]$cfgObj.results_root
        if (-not [string]::IsNullOrWhiteSpace($tmp)) { $resultsRoot = $tmp }
    }
    if ($cfgObj.PSObject.Properties.Name -contains 'fct_ini_path') {
        $tmp = [string]$cfgObj.fct_ini_path
        if (-not [string]::IsNullOrWhiteSpace($tmp)) { $cfgIniPath = $tmp }
    }
    if ($cfgObj.PSObject.Properties.Name -contains 'station_id') { $cfgStation = [string]$cfgObj.station_id }
    if ($cfgObj.PSObject.Properties.Name -contains 'skip_historical_scan') { $skipHist = $cfgObj.skip_historical_scan -eq $true }

    SUB 'keys present in file (raw)'
    foreach ($prop in $cfgObj.PSObject.Properties) {
        if ($prop.Name -eq 'webhook_url') { W "      webhook_url = $(Mask-Secret ([string]$prop.Value))"; continue }
        W "      $($prop.Name) = $($prop.Value)"
    }

    SUB 'key by key vs default'
    foreach ($k in $KnownKeys.Keys) {
        $present = ($cfgObj.PSObject.Properties.Name -contains $k)
        if ($k -eq 'webhook_url') {
            if ($present) { W ('      {0,-32} = {1}' -f $k, (Mask-Secret ([string]$cfgObj.$k))) }
            else { W ('      {0,-32} = (absent)  => default (not set)' -f $k) }
            continue
        }
        if (-not $present) { continue }
        $actual = $cfgObj.$k
        $defNorm = Norm-Val $KnownKeys[$k]
        if ((Norm-Val $actual) -ne $defNorm -and $defNorm -ne '') {
            W ('      {0,-32} = {1}   <== NON-DEFAULT (default {2})' -f $k, [string]$actual, $defNorm)
        }
        else {
            W ('      {0,-32} = {1}' -f $k, [string]$actual)
        }
    }

    SUB 'unknown / legacy keys'
    $unknown = @()
    foreach ($prop in $cfgObj.PSObject.Properties) {
        if ($KnownKeys.Contains($prop.Name)) { continue }
        if ($KeepKeys -contains $prop.Name) {
            W "      $($prop.Name) = $($prop.Value)   (kept: used by updater / autostart whitelist)"
            continue
        }
        $unknown += $prop.Name
        W "      $($prop.Name) = $($prop.Value)"
    }
    if ($unknown.Count -eq 0) { W '      (none)' }
    else { WARN "$($unknown.Count) key(s) Argus does not read: $($unknown -join ', ') (legacy agg-era keys can hijack parsing, consider deleting)" }

    if ($cfgObj.PSObject.Properties.Name -contains 'auto_start' -and ($cfgObj.auto_start -ne $true)) {
        WARN 'auto_start=false: Argus does not start with Windows'
    }
}
elseif (-not $cfgFile) {
    W '    (no config.json: defaults in effect)'
}

# ============================ [3] station / paths ============================
SEC '[3] station and paths'
$resolvedStation = ''
if (-not [string]::IsNullOrWhiteSpace($cfgStation)) { $resolvedStation = $cfgStation.Trim() }
elseif ($ipHit) { $resolvedStation = $ipHit }
W "    config station_id : $(if ($cfgStation) { $cfgStation } else { '(empty -> IP detect)' })"
W "    resolved station  : $(if ($resolvedStation) { $resolvedStation } else { '(none -> db falls back to fct.db)' })"
if (-not $resolvedStation) { WARN 'station_id is empty and IP did not match: database falls back to fct.db' }

$dbName = 'fct.db'
if ($resolvedStation) { $dbName = "$resolvedStation.db" }

if ($script:AppDir) {
    $baseDir = $script:AppDir
    $dataDir = Join-Path $baseDir 'data'
    $dbPath = Join-Path $dataDir $dbName
    $parsersPath = 'parsers.json'
    if ($cfgObj -and ($cfgObj.PSObject.Properties.Name -contains 'parsers_path') -and $cfgObj.parsers_path) { $parsersPath = [string]$cfgObj.parsers_path }
    if ([System.IO.Path]::IsPathRooted($parsersPath)) { $parsersFull = $parsersPath } else { $parsersFull = Join-Path $baseDir $parsersPath }
    $tdmsRoot = ''
    if ($cfgObj -and ($cfgObj.PSObject.Properties.Name -contains 'tdms_root')) { $tdmsRoot = [string]$cfgObj.tdms_root }
    $updateSource = ''
    if ($cfgObj -and ($cfgObj.PSObject.Properties.Name -contains 'update_source')) { $updateSource = [string]$cfgObj.update_source }

    SUB 'paths Argus uses at runtime'
    Check-Path 'base dir' $baseDir 'anchor for every relative path (= folder of Argus.exe)' 'AppContext.BaseDirectory' $true
    Check-Path 'config.json' (Join-Path $baseDir 'config.json') 'main config; missing = built-in defaults' 'BaseDir/config.json' $false
    Check-Path 'parsers.json' $parsersFull 'XML parsing rules; missing = built-in rules' 'config.parsers_path' $false
    Check-Path 'results_root' $resultsRoot 'result tree; missing = collection does NOT start' 'config.results_root' $true
    Check-Path 'data' $dataDir 'db / retry queue / config backups / updates' 'BaseDir/data' $false
    Check-Path ("db ({0})" -f $dbName) $dbPath 'local SQLite database (WAL)' 'BaseDir/data/{station}.db' $false
    Check-Path 'retry_queue.json' (Join-Path $dataDir 'retry_queue.json') 'parse retry queue' 'hardcoded' $false
    Check-Path 'config_backups' (Join-Path $dataDir 'config_backups') 'config backups (keeps 20)' 'hardcoded' $false
    Check-Path 'archive' (Join-Path $dataDir 'archive') 'cold data archive tsv' 'hardcoded' $false
    Check-Path 'updates' (Join-Path $dataDir 'updates') 'staged hot updates' 'config.update_dir, default data/updates' $false
    Check-Path 'update_source' $updateSource 'read-only update share (UNC); replaces updates scan' 'config.update_source' $false
    Check-Path 'fct_ini_path' $cfgIniPath 'device status page source' 'config.fct_ini_path' $false
    Check-Path 'tdms_root' $tdmsRoot 'TDMS feature source; empty = disabled' 'config.tdms_root' $false
    Check-Path 'logs' (Join-Path $baseDir 'logs') 'log folder' 'BaseDir/logs' $false
    Check-Path 'app.log' (Join-Path $baseDir 'logs\app.log') 'main log (20MB roll, keeps 7)' 'BaseDir/logs/app.log' $false
    Check-Path 'out' (Join-Path $baseDir 'out') 'Fetcher tool output' 'modules/Fetcher default' $false
}
else {
    WARN 'skipped: Argus folder not located'
}

# ============================ [4] results_root ============================
SEC '[4] results_root layout'
P "scanning $resultsRoot ..."
$rootNorm = $resultsRoot -replace '/', '\'
$dateList = @()
for ($i = 0; $i -lt [Math]::Max(1, $XmlDays); $i++) { $dateList += (Get-Date).AddDays(-1 * $i).ToString('yyyyMMdd') }

$dbPass = 0; $dbFail = 0; $dbTotal = -1
$diskTodayTotal = -1; $diskTodayP = 0; $diskTodayF = 0; $diskTodayO = 0

if (-not (Test-Path -LiteralPath $rootNorm)) {
    BAD "results_root does not exist: $rootNorm  -- Argus collection will NOT start"
}
else {
    OK "results_root: $rootNorm"
    try {
        $ri = Get-Item -LiteralPath $rootNorm -Force
        W "    created $($ri.CreationTime)   modified $($ri.LastWriteTime)"
    }
    catch {}
    try {
        $rootDrive = [System.IO.Path]::GetPathRoot($rootNorm)
        $di = New-Object System.IO.DriveInfo($rootDrive)
        if ($di.DriveType -eq 'Network') { WARN "results_root lives on a network drive ($rootDrive): network hiccups cause missed ingests" }
    }
    catch {}

    SUB 'Online / Offline and model folders'
    foreach ($cat in @('Online', 'Offline')) {
        $catDir = Join-Path $rootNorm $cat
        if (-not (Test-Path -LiteralPath $catDir)) { WARN "$cat folder missing: $catDir"; continue }
        $models = @(Get-ChildItem -LiteralPath $catDir -Directory -ErrorAction SilentlyContinue | Sort-Object Name)
        W "    [$cat] $($models.Count) model folder(s): $($models.Name -join ', ')"
        $bad = @($models | Where-Object { $_.Name -notmatch '^E\d{7}$' })
        if ($bad.Count -gt 0) { WARN "$cat has $($bad.Count) folder(s) not matching ^E\d{7}$ - Argus SKIPS them: $($bad.Name -join ', ')" }
        foreach ($m in ($models | Select-Object -First $MaxModelDirs)) {
            if ($m.Name -notmatch '^E\d{7}$') { continue }
            $days = @(Get-ChildItem -LiteralPath $m.FullName -Directory -ErrorAction SilentlyContinue | Sort-Object Name)
            $parts = @()
            foreach ($d in ($days | Select-Object -Last 8)) {
                if ($d.Name -notmatch '^\d{8}$') { $parts += "$($d.Name)(not-a-date)"; continue }
                $cnt = 0
                try { $cnt = @(Get-ChildItem -LiteralPath $d.FullName -Filter '*.xml' -File -Recurse -ErrorAction SilentlyContinue).Count } catch {}
                $parts += "$($d.Name)=$cnt"
            }
            W "           $cat/$($m.Name): $($days.Count) date folder(s)  last: $($parts -join '  ')"
        }
    }

    SUB 'per day counts (Online+Offline)'
    foreach ($ymd in $dateList) {
        $tot = 0; $pc = 0; $fc = 0; $oc = 0; $other = 0
        $perModel = @{}
        foreach ($cat in @('Online', 'Offline')) {
            $catDir = Join-Path $rootNorm $cat
            if (-not (Test-Path -LiteralPath $catDir)) { continue }
            foreach ($m in @(Get-ChildItem -LiteralPath $catDir -Directory -ErrorAction SilentlyContinue)) {
                if ($m.Name -notmatch '^E\d{7}$') { continue }
                $dayDir = Join-Path $m.FullName $ymd
                if (-not (Test-Path -LiteralPath $dayDir)) { continue }
                $files = @(Get-ChildItem -LiteralPath $dayDir -Filter '*.xml' -File -Recurse -ErrorAction SilentlyContinue)
                if ($files.Count -eq 0) { continue }
                $key = "$cat/$($m.Name)"
                if (-not $perModel.ContainsKey($key)) { $perModel[$key] = 0 }
                $perModel[$key] += $files.Count
                foreach ($f in $files) {
                    $tot++
                    $pfx = ''
                    if ($f.Name.Length -ge 2) { $pfx = $f.Name.Substring(0, 2).ToUpperInvariant() }
                    if ($pfx -eq 'P_') { $pc++ } elseif ($pfx -eq 'F_') { $fc++ } elseif ($pfx -eq 'O_') { $oc++ } else { $other++ }
                }
            }
        }
        W "    [$ymd] XML total: $tot  (P_=$pc  F_=$fc  O_=$oc  other=$other)"
        foreach ($k in ($perModel.Keys | Sort-Object)) { W "             $k = $($perModel[$k])" }
        if ($ymd -eq $today) {
            $diskTodayTotal = $tot; $diskTodayP = $pc; $diskTodayF = $fc; $diskTodayO = $oc
        }
    }

    SUB 'path pattern examples (rebuild this shape locally)'
    $shown = 0
    foreach ($cat in @('Online', 'Offline')) {
        $catDir = Join-Path $rootNorm $cat
        if (-not (Test-Path -LiteralPath $catDir)) { continue }
        foreach ($m in @(Get-ChildItem -LiteralPath $catDir -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
            if ($m.Name -notmatch '^E\d{7}$') { continue }
            $dayDir = Join-Path $m.FullName $today
            if (-not (Test-Path -LiteralPath $dayDir)) { continue }
            foreach ($f in @(Get-ChildItem -LiteralPath $dayDir -Filter '*.xml' -File -ErrorAction SilentlyContinue | Select-Object -First 2)) {
                W "      $($f.FullName)   ($(Get-SizeStr $f.FullName))"
                $shown++
            }
            if ($shown -ge 6) { break }
        }
        if ($shown -ge 6) { break }
    }
    if ($shown -eq 0) { W '      (no XML found for today)' }

    SUB 'folder tree (3 levels, names only)'
    try {
        foreach ($l1 in (@(Get-ChildItem -LiteralPath $rootNorm -Directory -ErrorAction SilentlyContinue | Sort-Object Name) | Select-Object -First 20)) {
            W "      $($l1.Name)\"
            foreach ($l2 in (@(Get-ChildItem -LiteralPath $l1.FullName -Directory -ErrorAction SilentlyContinue | Sort-Object Name) | Select-Object -First 20)) {
                W "        $($l2.Name)\"
                $l3 = @(Get-ChildItem -LiteralPath $l2.FullName -Directory -ErrorAction SilentlyContinue | Sort-Object Name)
                if ($l3.Count -gt 0) {
                    $tail = ''
                    if ($l3.Count -gt 20) { $tail = ' ...' }
                    W "          $((($l3 | Select-Object -First 20).Name) -join '  ')$tail"
                }
            }
        }
    }
    catch {}

    if (-not $SkipXmlCopy) {
        SUB "sample XML copy (max $SampleCount, prefers F_/P_/O_)"
        P 'copying sample XML...'
        $cand = New-Object System.Collections.Generic.List[object]
        foreach ($cat in @('Online', 'Offline')) {
            $catDir = Join-Path $rootNorm $cat
            if (-not (Test-Path -LiteralPath $catDir)) { continue }
            foreach ($m in (@(Get-ChildItem -LiteralPath $catDir -Directory -ErrorAction SilentlyContinue | Sort-Object Name) | Select-Object -First $MaxModelDirs)) {
                if ($m.Name -notmatch '^E\d{7}$') { continue }
                foreach ($ymd in $dateList) {
                    $dayDir = Join-Path $m.FullName $ymd
                    if (-not (Test-Path -LiteralPath $dayDir)) { continue }
                    foreach ($f in @(Get-ChildItem -LiteralPath $dayDir -Filter '*.xml' -File -Recurse -ErrorAction SilentlyContinue)) {
                        if ($f.Length -gt ($SampleMaxKb * 1024)) { continue }
                        $cand.Add([pscustomobject]@{ File = $f; Cat = $cat; Model = $m.Name; Date = $ymd })
                    }
                }
            }
        }
        $picked = New-Object System.Collections.Generic.List[object]
        foreach ($pfx in @('F_', 'P_', 'O_')) {
            $hit = $cand | Where-Object { $_.File.Name.ToUpperInvariant().StartsWith($pfx) } | Select-Object -First 1
            if ($hit) { $picked.Add($hit) }
        }
        foreach ($c in $cand) {
            if ($picked.Count -ge $SampleCount) { break }
            $already = $false
            foreach ($pp in $picked) { if ($pp.File.FullName -eq $c.File.FullName) { $already = $true; break } }
            if (-not $already) { $picked.Add($c) }
        }
        if ($picked.Count -eq 0) { WARN 'no sample XML found to copy' }
        foreach ($pk in $picked) {
            $destDir = $SamplesDir + '\' + $pk.Cat + '\' + $pk.Model + '\' + $pk.Date
            $destFile = Join-Path $destDir $pk.File.Name
            if (Copy-FileSafe $pk.File.FullName $destFile) { W "      copied: $($pk.Cat)\$($pk.Model)\$($pk.Date)\$($pk.File.Name)" }
        }
    }
}

# ============================ [5] FCT.ini ============================
SEC '[5] FCT.ini'
P 'locating FCT.ini...'
$candidates = @()
if ($cfgIniPath) { $candidates += $cfgIniPath }
$candidates += @('C:\FTS\Apps\PEU\Cfg\FCT.ini', 'D:\FTS\Apps\PEU\Cfg\FCT.ini', 'C:\FTS\Cfg\FCT.ini', 'C:\FTS\FCT.ini')
W '    candidates (program order):'
$iniFound = $null
foreach ($p in ($candidates | Select-Object -Unique)) {
    $norm = $p -replace '/', '\'
    if (Test-Path -LiteralPath $norm) {
        OK "found: $norm  ($(Get-SizeStr $norm), modified $((Get-Item -LiteralPath $norm).LastWriteTime))"
        if (-not $iniFound) { $iniFound = $norm }
    }
    else {
        $dir = Split-Path $norm -Parent
        if (Test-Path -LiteralPath $dir) { W "    [ -]    $norm  (folder exists, file missing)" }
        else { W "    [ -]    $norm" }
    }
}
if (-not $iniFound) {
    WARN 'FCT.ini not in the usual places; Argus will do its own bounded search'
    if ($DeepIniSearch) {
        P 'full-disk FCT.ini search (slow)...'
        $drives = @()
        try { $drives = [System.IO.DriveInfo]::GetDrives() } catch {}
        foreach ($d in $drives) {
            try {
                if (-not $d.IsReady -or $d.DriveType -ne 'Fixed') { continue }
                $found = @(Get-ChildItem -LiteralPath $d.RootDirectory.FullName -Filter 'FCT.ini' -Recurse -File -ErrorAction SilentlyContinue -Force | Select-Object -First 5)
                foreach ($f in $found) { $iniFound = $f.FullName; OK "found: $($f.FullName)"; break }
            }
            catch {}
            if ($iniFound) { break }
        }
    }
    else {
        foreach ($d in @('C:\FTS', 'D:\FTS', 'C:\Program Files\FTS', 'D:\Apps')) {
            if (-not (Test-Path -LiteralPath $d)) { continue }
            $found = @(Get-ChildItem -LiteralPath $d -Filter 'FCT.ini' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 3)
            foreach ($f in $found) { $iniFound = $f.FullName; OK "found: $($f.FullName)"; break }
            if ($iniFound) { break }
        }
        if (-not $iniFound) { WARN 'still not found (use -DeepIniSearch for a full scan)' }
    }
}

if ($iniFound) {
    $iniText = Read-FileText $iniFound
    if ($null -eq $iniText) {
        BAD 'cannot read FCT.ini (might be locked by the tester; try running as administrator)'
    }
    else {
        OK "read ok (shared mode), $(($iniText -split "`r?`n").Count) line(s)"
        Copy-FileSafe $iniFound (Join-Path $BundleDir 'FCT.ini') | Out-Null
        SUB 'sections used by Argus (device status page)'
        $cur = ''
        $dump = 0
        foreach ($line in ($iniText -split "`r?`n")) {
            $t = $line.Trim()
            if ($t -match '^\[(.+)\]$') {
                $cur = $Matches[1]
                if ($cur -eq 'Resource Name' -or $cur -eq 'A2L') { W "      [$cur]" }
                continue
            }
            if (-not $t -or $t.StartsWith(';')) { continue }
            if ($cur -eq 'Resource Name' -or $cur -eq 'A2L') {
                W "        $t"
                $dump++
                if ($dump -gt 80) { W '        ...(truncated)'; break }
            }
        }
        if ($dump -eq 0) { WARN 'no [Resource Name] / [A2L] section: device status page will show no devices' }
    }
}

SUB 'serial ports (registry + SerialPort, never opened)'
try {
    $ports = @()
    try {
        $rk = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('HARDWARE\DEVICEMAP\SERIALCOMM')
        if ($rk) { $ports += @($rk.GetValueNames() | ForEach-Object { [string]$rk.GetValue($_) }) }
    }
    catch {}
    try {
        foreach ($p in [System.IO.Ports.SerialPort]::GetPortNames()) { $ports += [string]$p }
    }
    catch {}
    $ports = @($ports | Where-Object { $_ } | Select-Object -Unique)
    if ($ports.Count -gt 0) { W "    ports: $($ports -join ', ')" }
    else { WARN 'no serial port detected (device status page will be empty)' }
}
catch { WARN "serial port enumeration failed: $($_.Exception.Message)" }

# ============================ [6] parsers.json ============================
SEC '[6] parsers.json'
if (-not $script:AppDir) { WARN 'skipped: Argus folder not located' }
else {
    $pp = 'parsers.json'
    if ($cfgObj -and ($cfgObj.PSObject.Properties.Name -contains 'parsers_path') -and $cfgObj.parsers_path) { $pp = [string]$cfgObj.parsers_path }
    if ([System.IO.Path]::IsPathRooted($pp)) { $parsersFull2 = $pp } else { $parsersFull2 = Join-Path $script:AppDir $pp }
    if (Test-Path -LiteralPath $parsersFull2) {
        OK "custom rules found: $parsersFull2  ($(Get-SizeStr $parsersFull2))"
        $pt = Read-FileText $parsersFull2
        if ($pt) {
            Copy-FileSafe $parsersFull2 (Join-Path $BundleDir 'parsers.json') | Out-Null
            SUB 'parsers.json content'
            foreach ($l in ($pt -split "`r?`n")) { W "      $l" }
        }
    }
    else {
        W "    no $parsersFull2 : Argus uses built-in rules"
        $alt = Join-Path $script:AppDir 'parsers.example.json'
        if (Test-Path -LiteralPath $alt) {
            Copy-FileSafe $alt (Join-Path $BundleDir 'parsers.example.json') | Out-Null
            W '    (parsers.example.json copied for reference)'
        }
    }
}

# ============================ [7] database ============================
SEC '[7] SQLite database'
P 'inspecting database...'
$sqliteExe = $null
$localSqlite = Join-Path $PSScriptRoot 'sqlite3.exe'
if (Test-Path -LiteralPath $localSqlite) { $sqliteExe = $localSqlite }
else {
    $cmd = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if ($cmd) { $sqliteExe = $cmd.Source }
}
if ($sqliteExe) { W "    sqlite3: $sqliteExe" }
else { WARN 'no sqlite3 on this machine: database gets a file-level snapshot only (put sqlite3.exe next to this script for tables)' }

function Invoke-Sqlite {
    param([string]$DbPath, [string]$Sql)
    if (-not $script:SqliteExe) { return $null }
    if ($Sql -notmatch '^\s*(?i)(select|pragma)') { return '(only SELECT/PRAGMA allowed)' }
    try {
        $out = & $script:SqliteExe -header -column $DbPath $Sql 2>&1
        if ($LASTEXITCODE -eq 0) { return ($out | Out-String).Trim() }
        return "(exit $LASTEXITCODE) $($out | Out-String)"
    }
    catch { return "(error) $($_.Exception.Message)" }
}
$script:SqliteExe = $sqliteExe
$dbQueryOk = $false

if (-not $script:AppDir) { WARN 'skipped: Argus folder not located' }
else {
    $dataDir2 = Join-Path $script:AppDir 'data'
    if (-not (Test-Path -LiteralPath $dataDir2)) {
        BAD "data folder missing: $dataDir2  -- has Argus ever run successfully?"
    }
    else {
        OK "data folder: $dataDir2"
        SUB 'data folder contents'
        try {
            foreach ($f in @(Get-ChildItem -LiteralPath $dataDir2 -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
                W ('      {0,-40} {1,10}  {2}' -f $f.Name, (Get-SizeStr $f.FullName), $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
            }
            foreach ($sd in @(Get-ChildItem -LiteralPath $dataDir2 -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
                $n = 0
                try { $n = @(Get-ChildItem -LiteralPath $sd.FullName -Recurse -File -ErrorAction SilentlyContinue).Count } catch {}
                W "      [dir] $($sd.Name)  ($n file(s))"
            }
        }
        catch {}

        $allDbs = @(Get-ChildItem -LiteralPath $dataDir2 -Filter '*.db' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        if ($allDbs.Count -eq 0) {
            BAD 'no .db in data folder: nothing has ever been ingested'
        }
        else {
            W "    $($allDbs.Count) .db file(s) in data:"
            foreach ($d in $allDbs) { W "      $($d.Name)  $(Get-SizeStr $d.FullName)  modified $($d.LastWriteTime)" }
            if ($allDbs.Count -gt 1) {
                WARN 'more than one .db: the station id changed at some point, history may sit in the old file'
            }
            $targetDb = $allDbs[0].FullName
            $expPath = Join-Path $dataDir2 $dbName
            if (Test-Path -LiteralPath $expPath) { $targetDb = $expPath }
            else { WARN "db for resolved station ($dbName) not found; using newest: $($allDbs[0].Name)" }
            OK "current db: $targetDb  ($(Get-SizeStr $targetDb))"
            foreach ($suf in @('-wal', '-shm')) {
                $x = $targetDb + $suf
                if (Test-Path -LiteralPath $x) { W "    sidecar: $(Split-Path $x -Leaf)  $(Get-SizeStr $x)" }
            }
            try { W "    SHA256: $((Get-FileHash -LiteralPath $targetDb -Algorithm SHA256).Hash)" } catch {}

            if ($sqliteExe) {
                SUB 'tables and row counts'
                $tabs = Invoke-Sqlite $targetDb "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"
                if ($tabs) { foreach ($tl in ($tabs -split "`r?`n")) { W "      $tl" } }
                foreach ($t in @('test_records', 'fail_items', 'test_measurements', 'tdms_features', 'normal_models', 'device_samples_local', 'meta')) {
                    $c = Invoke-Sqlite $targetDb "SELECT COUNT(*) FROM $t;"
                    if ($c -and $c -notmatch 'no such table' -and $c -notmatch '^\(exit') {
                        W "      $t rows: $(($c -split "`r?`n") | Select-Object -Last 1)"
                    }
                }
                SUB 'test_records date range'
                W (Invoke-Sqlite $targetDb "SELECT MIN(test_date) AS min_date, MAX(test_date) AS max_date, COUNT(*) AS total FROM test_records;")
                SUB 'today / yesterday by result'
                W (Invoke-Sqlite $targetDb "SELECT test_date, result, COUNT(*) AS cnt FROM test_records WHERE test_date IN ('$today','$yesterday') GROUP BY test_date, result ORDER BY test_date DESC, result;")
                SUB 'today by station'
                W (Invoke-Sqlite $targetDb "SELECT station_id, result, COUNT(*) AS cnt FROM test_records WHERE test_date = '$today' GROUP BY station_id, result;")
                SUB 'latest 10 FAIL'
                $f10 = Invoke-Sqlite $targetDb "SELECT id, sn, model, result, fail_reason, test_date FROM test_records WHERE result='FAIL' ORDER BY id DESC LIMIT 10;"
                W $f10
                if ($f10 -and $f10 -notmatch '^\(exit' -and $f10 -notmatch 'error') {
                    $dbQueryOk = $true
                    foreach ($row in ($f10 -split "`r?`n")) {
                        if ($row -match '\|') { $dbFail++ }
                    }
                    $cnt1 = Invoke-Sqlite $targetDb "SELECT COUNT(*) FROM test_records WHERE test_date = '$today';"
                    $cnt2 = Invoke-Sqlite $targetDb "SELECT COUNT(*) FROM test_records WHERE test_date = '$today' AND result='PASS';"
                    $cnt3 = Invoke-Sqlite $targetDb "SELECT COUNT(*) FROM test_records WHERE test_date = '$today' AND result='FAIL';"
                    try { $dbTotal = [int](($cnt1 -split "`r?`n") | Select-Object -Last 1) } catch { $dbTotal = -1 }
                    try { $dbPass = [int](($cnt2 -split "`r?`n") | Select-Object -Last 1) } catch { $dbPass = 0 }
                    try { $dbFail = [int](($cnt3 -split "`r?`n") | Select-Object -Last 1) } catch { $dbFail = 0 }
                    SUB 'today totals (db, all stations)'
                    W "      test_date=$today total=$dbTotal PASS=$dbPass FAIL=$dbFail"
                }
                SUB 'latest 10 records'
                W (Invoke-Sqlite $targetDb "SELECT id, sn, model, result, test_date FROM test_records ORDER BY id DESC LIMIT 10;")
                SUB 'schema (also saved to bundle\db_schema.sql)'
                $schema = Invoke-Sqlite $targetDb "SELECT sql FROM sqlite_master WHERE type IN ('table','index') AND sql IS NOT NULL ORDER BY type, name;"
                if ($schema) {
                    foreach ($l in ($schema -split "`r?`n")) { W "      $l" }
                    try { [System.IO.File]::WriteAllText((Join-Path $BundleDir 'db_schema.sql'), $schema, (New-Object System.Text.UTF8Encoding($true))) } catch {}
                }
            }

            if (-not $SkipDbCopy) {
                $sz = 0
                try { $sz = (Get-Item -LiteralPath $targetDb -Force).Length } catch {}
                if ($sz -gt ($DbCopyMaxMb * 1048576)) {
                    WARN "db is $([Math]::Round($sz/1MB,1)) MB, over the $DbCopyMaxMb MB cap: not copied (raise -DbCopyMaxMb to include it)"
                }
                else {
                    P 'copying database snapshot...'
                    $copied = 0
                    foreach ($suf in @('', '-wal', '-shm')) {
                        $src = $targetDb + $suf
                        if (Test-Path -LiteralPath $src) {
                            if (Copy-FileSafe $src (Join-Path $BundleDir (Split-Path $src -Leaf))) { $copied++ }
                        }
                    }
                    OK "database snapshot copied ($copied file(s), incl. wal/shm; opens as-is locally)"
                }
            }
        }
    }
}

# ============================ [8] logs ============================
SEC '[8] logs'
if (-not $script:AppDir) { WARN 'skipped: Argus folder not located' }
else {
    $logDir = Join-Path $script:AppDir 'logs'
    if (-not (Test-Path -LiteralPath $logDir)) { WARN "log folder missing: $logDir" }
    else {
        try {
            foreach ($f in @(Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
                W ('      {0,-20} {1,10}  {2}' -f $f.Name, (Get-SizeStr $f.FullName), $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
            }
        }
        catch {}
        $appLog = Join-Path $logDir 'app.log'
        if (Test-Path -LiteralPath $appLog) {
            $tail = @()
            try { $tail = @(Get-Content -LiteralPath $appLog -Tail 4000 -ErrorAction SilentlyContinue) } catch {}
            $cntIngest = 0; $cntFailIngest = 0; $cntPush = 0; $cntErr = 0; $cntWarn = 0; $cntScan = 0
            foreach ($l in $tail) {
                if ($l -notmatch $todayDash) { continue }
                if ($l -match 'xml=\d+.*\|') { $cntScan++ }
                if ($l -match '\] .+ \| (PASS|FAIL|INTERRUPTED) \|') {
                    $cntIngest++
                    if ($l -match '\] .+ \| FAIL \|') { $cntFailIngest++ }
                }
                if ($l -match 'FAIL /') { $cntPush++ }
                if ($l -match '\[ERR|\[ERROR') { $cntErr++ }
                if ($l -match '\[WARN') { $cntWarn++ }
            }
            W "    app.log tail $($tail.Count) line(s), today ($todayDash):"
            W "      hist_scan_end      = $cntScan"
            W "      ingest             = $cntIngest"
            W "      ingest_FAIL        = $cntFailIngest"
            W "      feishu_FAIL_push   = $cntPush"
            W "      ERROR lines        = $cntErr"
            W "      WARN lines         = $cntWarn"
            if ($cntFailIngest -gt 0 -and $cntPush -lt $cntFailIngest) {
                WARN "ingest FAIL ($cntFailIngest) > feishu push ($cntPush): FAILs were ingested without alerting"
            }
        }
        else { WARN "app.log missing: $appLog" }
    }
}

# ============================ [9] updates and backups ============================
SEC '[9] updates and backups'
if (-not $script:AppDir) { WARN 'skipped: Argus folder not located' }
else {
    $bk = Join-Path $script:AppDir 'data\config_backups'
    if (Test-Path -LiteralPath $bk) {
        try {
            $backs = @(Get-ChildItem -LiteralPath $bk -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
            W "    config_backups: $($backs.Count) file(s)"
            foreach ($b in ($backs | Select-Object -First 3)) { W "      $($b.Name)  $($b.LastWriteTime)" }
            if ($backs.Count -gt 0) { Copy-FileSafe $backs[0].FullName (Join-Path $BundleDir 'config_backup_latest.json') | Out-Null }
        }
        catch {}
    }
    $upd = Join-Path $script:AppDir 'data\updates'
    if (Test-Path -LiteralPath $upd) {
        SUB 'data\updates'
        try {
            $u = @(Get-ChildItem -LiteralPath $upd -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -First 40)
            if ($u.Count -eq 0) { W '      (empty)' }
            foreach ($f in $u) { W "      $($f.FullName.Substring($upd.Length).TrimStart('\'))  $(Get-SizeStr $f.FullName)" }
        }
        catch {}
    }
    $rq = Join-Path $script:AppDir 'data\retry_queue.json'
    if (Test-Path -LiteralPath $rq) {
        W "    retry_queue.json present ($(Get-SizeStr $rq)): files failed to parse are pending"
        $rqt = Read-FileText $rq
        if ($rqt) { foreach ($l in (($rqt -split "`r?`n") | Select-Object -First 30)) { W "      $l" } }
    }
    $sb = @()
    try { $sb = @(Get-ChildItem -LiteralPath $script:AppDir -Directory -Filter '_backup_*' -ErrorAction SilentlyContinue) } catch {}
    if ($sb.Count -gt 0) {
        W "    $($sb.Count) upgrade backup folder(s):"
        foreach ($b in $sb) { W "      $($b.Name)  ($($b.LastWriteTime))" }
    }
    if (-not (Test-Path -LiteralPath $bk) -and -not (Test-Path -LiteralPath $upd) -and -not (Test-Path -LiteralPath $rq) -and $sb.Count -eq 0) {
        W '    (none yet: no config_backups / updates / retry_queue / upgrade backups)'
    }
}

# ============================ [10] system ============================
SEC '[10] system'
W "    OS            : $osv"
W "    arch          : $env:PROCESSOR_ARCHITECTURE   (CPU cores: $env:NUMBER_OF_PROCESSORS)"
W "    PowerShell    : $($PSVersionTable.PSVersion)  (edition $($PSVersionTable.PSEdition))"
W "    CLR           : $clrv"
W "    locale        : $sysCs"
W "    timezone      : $tzName"
W "    local time    : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  (log lines / date folders use this)"
W "    system drive  : $env:SystemDrive"
if (-not $isAdmin) { WARN 'not running as administrator: some folders/registry keys cannot be read' }

SUB '.NET runtimes'
try {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $dotnetOut = (& dotnet --list-runtimes 2>&1 | Out-String).Trim()
        foreach ($l in ($dotnetOut -split "`r?`n")) { W "      $l" }
        if ($dotnetOut -notmatch 'Microsoft\.WindowsDesktop\.App 8\.') {
            WARN 'no .NET 8 desktop runtime found (fine if Argus ships as a self-contained single exe)'
        }
    }
    else { W '      dotnet CLI not installed (fine for a self-contained single exe)' }
}
catch {}

SUB 'drives'
$drives = @()
try { $drives = [System.IO.DriveInfo]::GetDrives() } catch {}
W '      drive type       total      free       label'
foreach ($d in $drives) {
    try {
        if (-not $d.IsReady) { W ('      {0,-5} {1,-10} (not ready)' -f $d.Name, [string]$d.DriveType); continue }
        $vol = ''
        try { $vol = $d.VolumeLabel } catch {}
        W ('      {0,-5} {1,-10} {2,7} GB {3,7} GB   {4}' -f $d.Name, [string]$d.DriveType, [Math]::Round($d.TotalSize / 1GB, 1), [Math]::Round($d.AvailableFreeSpace / 1GB, 1), $vol)
        if ($d.DriveType -eq 'Network') { WARN "network drive $($d.Name): Argus reading it may be slow" }
    }
    catch { W "      $($d.Name) unreadable" }
}

# ============================ [11] summary ============================
SEC '[11] summary'
W "    host              : $hostName  user $env:USERNAME"
W "    Argus dir         : $(if ($script:AppDir) { $script:AppDir } else { '(NOT FOUND)' })"
W "    Argus version     : $(if ($argusVersion) { $argusVersion } else { '(unknown)' })"
W "    station_id        : $(if ($cfgStation) { $cfgStation } else { '(empty)' })"
W "    resolved station  : $(if ($resolvedStation) { $resolvedStation } else { '(none)' })"
W "    results_root      : $resultsRoot"
W "    today XML (disk)  : $(if ($diskTodayTotal -ge 0) { "$diskTodayTotal  (P_=$diskTodayP F_=$diskTodayF O_=$diskTodayO)" } else { '(unreadable)' })"
W "    today DB rows     : $(if ($dbQueryOk) { "$dbTotal  (PASS=$dbPass FAIL=$dbFail)" } else { '(sqlite3 not available - see bundle db snapshot)' })"
W "    db file           : $dbName"
W "    config.json       : $(if ($cfgFile) { $cfgFile } else { '(NOT FOUND - defaults in use)' })"
W ''

if ($dbQueryOk -and $diskTodayTotal -ge 0) {
    $dbSum = $dbPass + $dbFail
    if ($diskTodayTotal -gt $dbSum + 2) {
        WARN "disk today ($diskTodayTotal) > DB PASS+FAIL ($dbSum), gap $($diskTodayTotal - $dbSum): likely missed scan/parse"
    }
    elseif ($diskTodayTotal -lt $dbSum - 2) {
        WARN "DB ($dbSum) > disk today ($diskTodayTotal): check results_root path"
    }
    else {
        W "    disk and DB are close (disk=$diskTodayTotal, db=$dbSum)"
    }
}
elseif (-not $dbQueryOk) {
    W '    [!]    DB counts unavailable on this machine (no sqlite3). The db snapshot in'
    W '           bundle\ is the source of truth - it will be inspected after upload.'
}

if ($skipHist) { WARN 'skip_historical_scan=true: only the today folder is scanned at startup' }
if (-not $cfgStation) { WARN 'station_id is empty: set it explicitly (e.g. FCT4) to avoid IP-detect surprises' }

if ($script:Issues.Count -gt 0) {
    W ''
    W "    BLOCKING problems: $($script:Issues.Count)"
    foreach ($i in $script:Issues) { W "      - $i" }
}
else {
    W ''
    W '    No blocking problem found.'
}

# ============================ write report ============================
W ''
W '============================================================'
W ' Send this full report to developer'
W '============================================================'

$finalText = ($script:Lines -join "`r`n") + "`r`n"
try {
    [System.IO.File]::WriteAllText($ReportPath, $finalText, (New-Object System.Text.UTF8Encoding($true)))
}
catch {
    Write-Host "[X] cannot write report: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ''
Write-Host 'bundle contents:'
try {
    foreach ($f in @(Get-ChildItem -LiteralPath $BundleDir -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName)) {
        Write-Host ('  {0,-58} {1,10}' -f $f.FullName.Substring($BundleDir.Length).TrimStart('\'), (Get-SizeStr $f.FullName))
    }
}
catch {}

$zipPath = $null
if (-not $NoZip) {
    P 'zipping...'
    $zipPath = "$OutDir.zip"
    try {
        if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue }
        Compress-Archive -Path $OutDir -DestinationPath $zipPath -Force
    }
    catch { Write-Host "zip failed (send the folder instead): $($_.Exception.Message)" -ForegroundColor Yellow; $zipPath = $null }
}

Write-Host ''
Write-Host '============================================================'
Write-Host ' done'
Write-Host " report : $ReportPath"
if ($zipPath) { Write-Host " zip    : $zipPath   (send this one)" }
Write-Host " issues : $($script:Issues.Count)  notes: $($script:Notes.Count)"
Write-Host '============================================================'
