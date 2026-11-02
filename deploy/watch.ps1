# Watches the machine under the cluster from Windows, because Monitor runs in Docker and dies with it.
# A scheduled task runs this every minute. It only reads, and never calls WSL: a check must not
# start WSL on a sick disk. What only the inside can see arrives in a file a timer there writes.
param([switch]$Run)
. (Join-Path $PSScriptRoot 'changes.ps1')

# Interim. The build cache read 17.9 GB by docker system df on 2026-10-31. That is every build to that
# date, not one. Write-BuildSpace logs a real build, and its figure replaces this one.
$BuildFreeGB = 18
# Interim. 24 h at 1.43 GB/h: ext4 went from 28G to 98G between 2026-10-30 19:51 and 2026-11-01 20:37 UTC.
$AlarmFreeGB = 35
$WslReportMaxSeconds = 180
$WatchDistro = 'Ubuntu-24.04'
$WslRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss'
$K3sReadyUrl = 'https://127.0.0.1:6443/readyz'
$MonitorAliveUrl = 'http://127.0.0.1:5320/alive'

function Get-DriveProblem([string]$Drive, [double]$FreeGB, [double]$LimitGB) {
    if ($FreeGB -ge $LimitGB) { return '' }
    return "$Drive has $FreeGB GB free, under $LimitGB GB"
}

function Find-WslDiskPath([object[]]$Entries, [string]$Distro) {
    $entry = @($Entries | Where-Object { $_.DistributionName -eq $Distro })[0]
    if (-not $entry) { return $null }
    $file = if ($entry.VhdFileName) { $entry.VhdFileName } else { 'ext4.vhdx' }
    return Join-Path ($entry.BasePath -replace '^\\\\\?\\', '') $file
}

function Get-WatchedDrives([string]$DiskPath) {
    $drives = @('C:')
    if ($DiskPath) { $drives += $DiskPath.Substring(0, 2).ToUpper() }
    return @($drives | Select-Object -Unique)
}

function Get-WslProblems([string]$Text, [DateTimeOffset]$Now) {
    $unknown = { param($why) @{ Disk = $why; Docker = $why } }
    if (-not $Text) { return & $unknown 'no report from WSL' }
    try {
        $report = $Text | ConvertFrom-Json
        $at = [DateTimeOffset]::Parse("$($report.at)", [Globalization.CultureInfo]::InvariantCulture)
    } catch {
        return & $unknown 'the WSL report is unreadable'
    }
    $age = [int]($Now - $at).TotalSeconds
    if ($age -gt $WslReportMaxSeconds) { return & $unknown "the WSL report is $age s old" }
    if ($null -eq $report.rootOptions -or $null -eq $report.ioErrors -or $null -eq $report.docker) { return & $unknown 'the WSL report is incomplete' }
    $options = @("$($report.rootOptions)" -split ',')
    $disk = ''
    if ($options -contains 'ro' -or $options -contains 'emergency_ro') { $disk = 'WSL root disk is read-only' }
    elseif ($report.ioErrors -gt 0) { $disk = "$($report.ioErrors) disk errors in the WSL kernel log" }
    return @{ Disk = $disk; Docker = $(if ($report.docker -eq $true) { '' } else { 'Docker daemon down' }) }
}

function Get-K3sProblem([int]$Status) {
    # 401 and 403 come from the API server itself, which is the question.
    if ($Status -in 200, 401, 403) { return '' }
    if ($Status -eq 0) { return 'k3s API gave no answer' }
    return "k3s API answered $Status"
}

function Get-MonitorProblem([int]$Status) {
    if ($Status -eq 200) { return '' }
    if ($Status -eq 0) { return 'Monitor gave no answer' }
    return "Monitor answered $Status"
}

function New-HostCheck([string]$Name, [string]$Problem) {
    return [pscustomobject]@{ name = $Name; healthy = (-not $Problem); error = $(if ($Problem) { $Problem } else { $null }) }
}

function Get-HostChecks([hashtable]$Facts) {
    $wsl = Get-WslProblems $Facts.WslReport $Facts.Now
    $disk = if ($Facts.DiskPath) { $wsl.Disk } else { "the registry names no disk for $WatchDistro" }
    foreach ($drive in $Facts.Drives) { New-HostCheck "disk $($drive.Name)" (Get-DriveProblem $drive.Name $drive.FreeGB $AlarmFreeGB) }
    New-HostCheck 'wsl disk' $disk
    New-HostCheck 'docker' $wsl.Docker
    New-HostCheck 'k3s api' (Get-K3sProblem $Facts.K3s)
    New-HostCheck 'monitor' (Get-MonitorProblem $Facts.Monitor)
}

function Get-HostChanges([object[]]$Old, [object[]]$New) {
    foreach ($check in $New) {
        $before = @($Old | Where-Object { $_.name -eq $check.name })[0]
        $wasHealthy = (-not $before) -or $before.healthy
        if ($wasHealthy -eq $check.healthy) {
            if ($before -or $check.healthy) { continue }
        }
        $text = if ($check.healthy) { "BatchTracker $($check.name) recovered" } else { "BatchTracker $($check.name) failing: $($check.error)" }
        [pscustomobject]@{ Name = $check.name; Old = $(if ($wasHealthy) { 'healthy' } else { 'failing' }); New = $(if ($check.healthy) { 'healthy' } else { 'failing' }); Text = $text }
    }
}

function Read-HostState([string]$State) {
    try { return @((Get-Content -LiteralPath $State -Raw -ErrorAction Stop | ConvertFrom-Json).checks) } catch { return @() }
}

function Invoke-HostWatch([string]$State, [hashtable]$Facts, [scriptblock]$Tell) {
    $checks = @(Get-HostChecks $Facts)
    # Telling comes first: on a full drive the state write below fails, and the change line is already written.
    foreach ($change in @(Get-HostChanges (Read-HostState $State) $checks)) { & $Tell $change }
    $temporary = "$State.tmp"
    [IO.File]::WriteAllText($temporary, (@{ checkedAt = $Facts.Now.ToString('o'); checks = $checks } | ConvertTo-Json -Depth 4))
    Move-Item -LiteralPath $temporary -Destination $State -Force
}

function Get-DriveFreeGB([string]$Drive) {
    return [math]::Round((New-Object IO.DriveInfo $Drive).AvailableFreeSpace / 1GB, 1)
}

function Read-WslDiskPath {
    $entries = Get-ChildItem $WslRegistry -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath }
    return Find-WslDiskPath @($entries) $WatchDistro
}

function Read-HttpStatus([string]$Url, [int]$Milliseconds = 3000) {
    $request = [Net.HttpWebRequest][Net.WebRequest]::Create($Url)
    $request.Timeout = $Milliseconds
    $request.ReadWriteTimeout = $Milliseconds
    # k3s signs with its own CA, and this sends nothing secret: only the status is read.
    $request.ServerCertificateValidationCallback = { $true }
    try {
        $response = $request.GetResponse()
        $status = [int]$response.StatusCode
        $response.Close()
        return $status
    } catch [Net.WebException] {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        return 0
    }
}

function Read-HostFacts {
    $disk = Read-WslDiskPath
    $drives = foreach ($drive in (Get-WatchedDrives $disk)) {
        # A drive that cannot be read must alarm, and 0 does.
        $free = try { Get-DriveFreeGB $drive } catch { 0 }
        @{ Name = $drive; FreeGB = $free }
    }
    $report = Join-Path $ChangesDir 'wsl.json'
    return @{
        Now = [DateTimeOffset]::UtcNow; Drives = @($drives); DiskPath = $disk
        WslReport = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { '' }
        K3s = Read-HttpStatus $K3sReadyUrl; Monitor = Read-HttpStatus $MonitorAliveUrl
    }
}

function Send-HostChange($Change) {
    Write-Change 'host check' $Change.Name $Change.Old $Change.New
}

function Get-BuildDrives {
    return @(@(Get-WatchedDrives (Read-WslDiskPath)) + $env:TEMP.Substring(0, 2).ToUpper() | Select-Object -Unique)
}

function Assert-BuildSpace([string[]]$Drives, [scriptblock]$Free = { param($drive) Get-DriveFreeGB $drive }) {
    foreach ($drive in $Drives) {
        try { $gb = & $Free $drive } catch { throw "Build refused: cannot read free space on $drive." }
        $problem = Get-DriveProblem $drive $gb $BuildFreeGB
        if ($problem) { throw "Build refused: $problem." }
    }
}

function Get-BuildSpaceLine([string]$When, [string]$DiskPath, [double]$DiskGB, [object[]]$Drives) {
    $free = @($Drives | ForEach-Object { "$($_.Name) $($_.FreeGB) GB free" }) -join ', '
    return "build space $When`: $DiskPath $DiskGB GB, $free"
}

function Write-BuildSpace([string]$When) {
    # Each line is half of a measure: the gap across a real build is the figure $BuildFreeGB waits for.
    $disk = Read-WslDiskPath
    $size = if ($disk -and (Test-Path -LiteralPath $disk)) { [math]::Round((Get-Item -LiteralPath $disk).Length / 1GB, 2) } else { 0 }
    $drives = @(Get-BuildDrives | ForEach-Object { @{ Name = $_; FreeGB = Get-DriveFreeGB $_ } })
    $line = Get-BuildSpaceLine $When $disk $size $drives
    Write-Output "[OK] $line" | Out-Host
    Write-Change 'build space' $When $null $line
}

function Get-WatchTaskCommand([string]$Script) {
    # Headless, or a console flashes on the owner's desktop every minute.
    return "conhost.exe --headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Script -Run"
}

function Get-WslReportScript {
    $script = @'
#!/bin/sh
# Writes to the Windows disk, so a read-only root can still say so. The count lasts until the VM restarts.
target="$1"
options=$(findmnt -no OPTIONS / 2>/dev/null)
errors=$(dmesg 2>/dev/null | grep -cE 'EXT4-fs error|I/O error|Remounting filesystem read-only')
if timeout 10 docker info >/dev/null 2>&1; then docker=true; else docker=false; fi
throttled=$(timeout 10 docker logs --since 60s batchtracker-servicebus 2>&1 | grep -c 'Error code : 50002')
printf '{"at":"%s","rootOptions":"%s","ioErrors":%s,"docker":%s,"busThrottled":%s}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$options" "${errors:-0}" "$docker" "${throttled:-0}" > "$target.tmp"
mv -f "$target.tmp" "$target"
'@
    return $script.Replace("`r`n", "`n") + "`n"
}

if ($Run) {
    if (-not (Test-Path $ChangesDir)) { New-Item -ItemType Directory -Path $ChangesDir | Out-Null }
    Invoke-HostWatch (Join-Path $ChangesDir 'host.json') (Read-HostFacts) { param($change) Send-HostChange $change }
}
