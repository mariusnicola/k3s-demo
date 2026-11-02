# Needs nothing in WSL: these rows must still run when WSL is the thing that broke.
. (Join-Path $DeployDir 'watch.ps1')
$WatchNow = [DateTimeOffset]'2026-11-01T21:00:00Z'
$GitBash = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'

function Get-TestWslReport([int]$AgeSeconds = 0, [string]$Options = 'rw,relatime,errors=remount-ro', [int]$Errors = 0, [string]$Docker = 'true') {
    $at = $WatchNow.AddSeconds(-$AgeSeconds).ToString('yyyy-MM-ddTHH:mm:ssZ')
    return "{`"at`":`"$at`",`"rootOptions`":`"$Options`",`"ioErrors`":$Errors,`"docker`":$Docker}"
}

function Get-TestFacts([double]$Free = 100, [string]$Report = (Get-TestWslReport), [int]$K3s = 200, [int]$Monitor = 200) {
    return @{ Now = $WatchNow; Drives = @(@{ Name = 'C:'; FreeGB = $Free }); DiskPath = 'C:\wsl\ext4.vhdx'; WslReport = $Report; K3s = $K3s; Monitor = $Monitor }
}

function Get-FailedNames([object[]]$Checks) { return @($Checks | Where-Object { -not $_.healthy } | ForEach-Object { $_.name }) }

Test-Check 'REQ-BT-058 the interim figures are the ones monitoring.md states' {
    $doc = Get-Content (Join-Path $PSScriptRoot '..\..\docs\architecture\monitoring.md') -Raw
    Assert-True ($BuildFreeGB -eq 18 -and $AlarmFreeGB -eq 35) "figures $BuildFreeGB and $AlarmFreeGB"
    Assert-True ($doc -match 'interim') 'monitoring.md does not call them interim'
    Assert-True ($doc -match "\b$BuildFreeGB GB" -and $doc -match "\b$AlarmFreeGB GB") 'monitoring.md misses a figure'
}

Test-Check 'REQ-BT-058 drive space passes above and at the alarm, fails under it' {
    Assert-True (-not (Get-DriveProblem 'C:' 36 35)) 'above must pass'
    Assert-True (-not (Get-DriveProblem 'C:' 35 35)) 'at the limit must pass'
    $problem = Get-DriveProblem 'C:' 34.9 35
    Assert-True ($problem -match 'C:' -and $problem -match '34\.9' -and $problem -match '35') "said: $problem"
}

Test-Check 'REQ-BT-058 the WSL disk is found from the registry wherever it moved' {
    $entries = @(
        [pscustomobject]@{ DistributionName = 'Other'; BasePath = 'E:\other'; VhdFileName = 'ext4.vhdx' }
        [pscustomobject]@{ DistributionName = 'Ubuntu-24.04'; BasePath = '\\?\D:\wsl\Ubuntu-24.04'; VhdFileName = $null }
    )
    $path = Find-WslDiskPath $entries 'Ubuntu-24.04'
    Assert-True ($path -eq 'D:\wsl\Ubuntu-24.04\ext4.vhdx') "found $path"
    Assert-True ($null -eq (Find-WslDiskPath $entries 'Gone')) 'an unknown distro must give nothing'
}

Test-Check 'REQ-BT-058 C: and the drive holding the disk are both watched, once each' {
    Assert-True ((@(Get-WatchedDrives 'D:\wsl\ext4.vhdx') -join ',') -eq 'C:,D:') 'moved disk'
    Assert-True ((@(Get-WatchedDrives 'c:\wsl\ext4.vhdx') -join ',') -eq 'C:') 'same drive twice'
    Assert-True ((@(Get-WatchedDrives $null) -join ',') -eq 'C:') 'no disk found must still watch C:'
}

Test-Check 'REQ-BT-058 a healthy WSL report has no problem, at the age limit included' {
    foreach ($age in 0, 180) {
        $found = Get-WslProblems (Get-TestWslReport -AgeSeconds $age) $WatchNow
        Assert-True (-not $found.Disk -and -not $found.Docker) "age $age said $($found.Disk) $($found.Docker)"
    }
}

Test-Check 'REQ-BT-058 a read-only root fails, and errors=remount-ro alone does not' {
    foreach ($options in 'ro,relatime', 'rw,emergency_ro,relatime', 'relatime,ro') {
        Assert-True ((Get-WslProblems (Get-TestWslReport -Options $options) $WatchNow).Disk -match 'read-only') $options
    }
    Assert-True (-not (Get-WslProblems (Get-TestWslReport -Options 'rw,errors=remount-ro') $WatchNow).Disk) 'errors=remount-ro is a setting'
}

Test-Check 'REQ-BT-058 kernel disk errors fail the disk and leave Docker alone' {
    $found = Get-WslProblems (Get-TestWslReport -Errors 3) $WatchNow
    Assert-True ($found.Disk -match '3 disk errors') "said: $($found.Disk)"
    Assert-True (-not $found.Docker) 'docker is fine'
}

Test-Check 'REQ-BT-058 Docker down fails Docker and leaves the disk alone' {
    $found = Get-WslProblems (Get-TestWslReport -Docker 'false') $WatchNow
    Assert-True ($found.Docker -match 'Docker daemon down') "said: $($found.Docker)"
    Assert-True (-not $found.Disk) 'disk is fine'
}

Test-Check 'REQ-BT-058 no answer from WSL is unknown, never healthy' {
    $cases = @{ missing = ''; broken = '{ half'; stale = (Get-TestWslReport -AgeSeconds 181); blank = '{"at":"2026-11-01T21:00:00Z"}' }
    foreach ($name in $cases.Keys) {
        $found = Get-WslProblems $cases[$name] $WatchNow
        Assert-True ($found.Disk -and $found.Docker) "$name read as healthy"
    }
}

Test-Check 'REQ-BT-058 the k3s API is up on any answer that proves it runs' {
    foreach ($status in 200, 401, 403) { Assert-True (-not (Get-K3sProblem $status)) "$status must pass" }
    foreach ($status in 0, 500, 503) { Assert-True ([bool](Get-K3sProblem $status)) "$status must fail" }
}

Test-Check 'REQ-BT-058 the k3s API is read on loopback, because the LAN address from Windows itself can hit a stale portproxy' {
    Assert-True ($K3sReadyUrl -eq 'https://127.0.0.1:6443/readyz') "reads $K3sReadyUrl"
}

Test-Check 'REQ-BT-058 Monitor is alive only on a 200' {
    Assert-True (-not (Get-MonitorProblem 200)) '200 must pass'
    foreach ($status in 0, 503) { Assert-True ([bool](Get-MonitorProblem $status)) "$status must fail" }
}

Test-Check 'REQ-BT-058 every check is named, and each failure lands on its own check' {
    $names = @((Get-HostChecks (Get-TestFacts)) | ForEach-Object { $_.name }) -join ','
    Assert-True ($names -eq 'disk C:,wsl disk,docker,k3s api,monitor') "names $names"
    Assert-True ((Get-FailedNames (Get-HostChecks (Get-TestFacts))).Count -eq 0) 'all healthy must fail nothing'
    Assert-True ((Get-FailedNames (Get-HostChecks (Get-TestFacts -Free 1))) -join ',' -eq 'disk C:') 'drive'
    Assert-True ((Get-FailedNames (Get-HostChecks (Get-TestFacts -K3s 0))) -join ',' -eq 'k3s api') 'k3s'
    Assert-True ((Get-FailedNames (Get-HostChecks (Get-TestFacts -Monitor 0))) -join ',' -eq 'monitor') 'monitor'
}

Test-Check 'REQ-BT-058 a disk the registry does not name is a failure, not a pass' {
    $facts = Get-TestFacts
    $facts.DiskPath = $null
    Assert-True ((Get-FailedNames (Get-HostChecks $facts)) -contains 'wsl disk') 'no disk path must fail'
}

Test-Check 'REQ-BT-058 only a change of state tells the owner' {
    $good = [pscustomobject]@{ name = 'docker'; healthy = $true; error = $null }
    $bad = [pscustomobject]@{ name = 'docker'; healthy = $false; error = 'down' }
    Assert-True (@(Get-HostChanges @() @($good)).Count -eq 0) 'first healthy run says nothing'
    Assert-True (@(Get-HostChanges @() @($bad))[0].Text -match 'docker.*down') 'first failing run must tell'
    Assert-True (@(Get-HostChanges @($good) @($bad))[0].Text -match 'docker.*down') 'new failure must tell'
    Assert-True (@(Get-HostChanges @($bad) @($bad)).Count -eq 0) 'still failing stays quiet'
    Assert-True (@(Get-HostChanges @($bad) @($good))[0].Text -match 'docker recovered') 'recovery must tell'
}

Test-Check 'REQ-BT-058 a watch run writes the file Monitor reads and tells once' {
    $state = Join-Path $env:TEMP "batchtracker-host-$(Get-Random).json"
    $told = New-Object Collections.ArrayList
    $tell = { param($change) [void]$told.Add($change.Text) }
    try {
        Invoke-HostWatch $state (Get-TestFacts -Monitor 0) $tell
        Invoke-HostWatch $state (Get-TestFacts -Monitor 0) $tell
        $written = Get-Content $state -Raw | ConvertFrom-Json
        Assert-True ($told.Count -eq 1 -and $told[0] -match 'monitor') "told $($told.Count)"
        Assert-True ($written.checkedAt -match '^2026-11-01T21:00:00' -and @($written.checks).Count -eq 5) 'file shape'
        Invoke-HostWatch $state (Get-TestFacts) $tell
        Assert-True ($told.Count -eq 2 -and $told[1] -match 'recovered') "recovery told $($told.Count)"
    } finally {
        Remove-Item $state -ErrorAction SilentlyContinue
    }
}

Test-Check 'REQ-BT-058 a damaged state file does not stop the watch' {
    $state = Join-Path $env:TEMP "batchtracker-host-$(Get-Random).json"
    [IO.File]::WriteAllText($state, '{ half')
    try {
        Invoke-HostWatch $state (Get-TestFacts) { param($change) }
        Assert-True (@((Get-Content $state -Raw | ConvertFrom-Json).checks).Count -eq 5) 'not rewritten'
    } finally {
        Remove-Item $state -ErrorAction SilentlyContinue
    }
}

Test-Check 'REQ-BT-058 setup refuses a build under the figure and names the drive' {
    $free = { param($drive) if ($drive -eq 'D:') { 17.9 } else { 500 } }
    $refused = ''
    try { Assert-BuildSpace @('C:', 'D:') $free } catch { $refused = "$_" }
    Assert-True ($refused -match 'D:' -and $refused -match '17\.9' -and $refused -match '18') "said: $refused"
}

Test-Check 'REQ-BT-058 setup lets a build start at the figure exactly' {
    Assert-BuildSpace @('C:', 'D:') { param($drive) 18 }
}

Test-Check 'REQ-BT-058 setup refuses when free space cannot be read' {
    $refused = ''
    try { Assert-BuildSpace @('Z:') { param($drive) throw 'no such drive' } } catch { $refused = "$_" }
    Assert-True ($refused -match 'Z:') "said: $refused"
}

Test-Check 'REQ-BT-058 setup checks space before it writes anything and logs around the builds' {
    $setup = Get-Content (Join-Path $DeployDir 'setup.ps1') -Raw
    $release = Get-Content (Join-Path $DeployDir 'release.ps1') -Raw
    Assert-True ($setup.IndexOf('Assert-BuildSpace') -gt 0 -and $setup.IndexOf('Assert-BuildSpace') -lt $setup.IndexOf('Export-Commit $RepoRoot')) 'space must come before the export'
    Assert-True (($release -split 'Write-BuildSpace').Count -ge 3) 'gate builds must log before and after'
    Assert-True (($setup -split 'Write-BuildSpace').Count -ge 3) 'image builds must log before and after'
    Assert-True ($setup -match 'Set-WatchTask' -and $setup -match 'Set-WslReport') 'setup must install the watch'
}

Test-Check 'REQ-BT-058 the build log names the disk size and the free space' {
    $line = Get-BuildSpaceLine 'before image builds' 'D:\wsl\ext4.vhdx' 99.85 @(@{ Name = 'D:'; FreeGB = 496.7 })
    Assert-True ($line -match 'before image builds' -and $line -match '99\.85' -and $line -match 'D: 496\.7') "said: $line"
}

Test-Check 'REQ-BT-058 the watch never calls wsl, so it cannot start WSL on a sick disk' {
    $code = @(Get-Content (Join-Path $DeployDir 'watch.ps1') | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    # bash too: on Windows that name can be the launcher that starts WSL.
    Assert-True ($code -cnotmatch '\bwsl\.exe|\bwsl\s+-|\bbash(\.exe)?\s') 'watch.ps1 calls wsl'
    Assert-True ((Get-WatchTaskCommand 'E:\deploy\watch.ps1') -match 'watch\.ps1.*-Run') 'task must run the watch'
}

Test-Check 'REQ-BT-058 no deploy script shows a popup on the desktop' {
    $files = @(Get-ChildItem $DeployDir -Recurse -File -Include *.ps1, *.psm1, *.sh, *.cmd, *.bat | Where-Object { $_.FullName -notmatch '\\(\.release|\.generated|\.changes|terraform)\\' })
    Assert-True ($files.Count -gt 0) 'no deploy script found'
    $pattern = '\bmsg(\.exe)?\s+[\$"*]|msg\.exe|MessageBox|BurntToast|ToastNotification|Show-Notification|Send-HostAlert|AlertSeconds|NotifyIcon|wscript\.shell|\.Popup\('
    $hits = foreach ($file in $files) {
        Get-Content -LiteralPath $file.FullName | Where-Object { $_ -notmatch '^\s*#' -and $_ -match $pattern } | ForEach-Object { "$($file.Name): $_" }
    }
    Assert-True (-not $hits) "popup code: $($hits -join ' | ')"
}

Test-Check 'REQ-BT-058 a host change is written to the change feed and nowhere else' {
    $script:written = New-Object Collections.ArrayList
    function Write-Change([string]$Kind, [string]$Object, [string]$Old, [string]$New) { [void]$script:written.Add("$Kind|$Object|$Old|$New") }
    $change = [pscustomobject]@{ Name = 'docker'; Old = 'healthy'; New = 'failing'; Text = 'BatchTracker docker failing: Docker daemon down' }
    # A SystemRoot with no System32 means an old popup call finds nothing to show.
    $root = $env:SystemRoot
    $env:SystemRoot = Join-Path $env:TEMP "batchtracker-noroot-$(Get-Random)"
    try { Send-HostChange $change 2>$null } finally { $env:SystemRoot = $root }
    Assert-True ($script:written.Count -eq 1-and $script:written[0] -eq 'host check|docker|healthy|failing') "wrote: $($script:written -join ' ; ')"
}

Test-Check 'REQ-BT-058 the WSL report script writes a file the watcher reads' {
    Assert-True (Test-Path $GitBash) "Git bash missing at $GitBash"
    $dir = Join-Path $env:TEMP "batchtracker-report-$(Get-Random)"
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        $stubs = @{ findmnt = 'echo rw,emergency_ro'; dmesg = 'echo "EXT4-fs error (device sdd)"; echo "fine"; echo "I/O error, dev sdd"'; docker = 'if [ "$1" = logs ]; then echo "Error code : 50002"; echo fine; echo "Error code : 50002"; exit 0; fi; exit 1'; timeout = 'shift; "$@"' }
        foreach ($name in $stubs.Keys) { [IO.File]::WriteAllText((Join-Path $dir $name), "#!/bin/sh`n$($stubs[$name])`n") }
        $script = Get-WslReportScript
        Assert-True ($script -notmatch "`r") 'a carriage return breaks the script on Linux'
        [IO.File]::WriteAllText((Join-Path $dir 'report.sh'), $script)
        $unix = '/' + $dir.Substring(0, 1).ToLower() + ($dir.Substring(2) -replace '\\', '/')
        & $GitBash -c "PATH=$unix`:`$PATH sh $unix/report.sh $unix/wsl.json"
        $text = Get-Content (Join-Path $dir 'wsl.json') -Raw
        Assert-True (-not (Test-Path (Join-Path $dir 'wsl.json.tmp'))) 'temporary file left behind'
        $found = Get-WslProblems $text ([DateTimeOffset]::UtcNow)
        Assert-True ($found.Disk -match 'read-only') "disk said: $($found.Disk)"
        Assert-True ($found.Docker -match 'Docker daemon down') "docker said: $($found.Docker)"
        Assert-True (($text | ConvertFrom-Json).ioErrors -eq 2) "counted $(($text | ConvertFrom-Json).ioErrors)"
        Assert-True (($text | ConvertFrom-Json).busThrottled -eq 2) "counted $(($text | ConvertFrom-Json).busThrottled) throttle lines"
    } finally {
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
