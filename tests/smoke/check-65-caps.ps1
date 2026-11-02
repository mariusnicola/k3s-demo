$capsDir = Join-Path $env:TEMP 'batchtracker-smoke-caps'
New-Item -ItemType Directory -Force -Path $capsDir | Out-Null
$setup = Join-Path $DeployDir 'setup.ps1'

function Invoke-SetupCaps($CapsObject) {
    # Runs the real setup script. -CapsOnly stops it before it changes anything.
    $file = Join-Path $capsDir 'caps.json'
    [IO.File]::WriteAllText($file, ($CapsObject | ConvertTo-Json -Depth 10))
    $output = powershell -NoProfile -ExecutionPolicy Bypass -File $setup -CapsOnly -CapsFile $file 2>&1 | Out-String
    return @{ Exit = $LASTEXITCODE; Output = $output }
}

function Copy-Caps { Read-Caps (Join-Path $DeployDir 'caps.json') }

Test-Check 'REQ-BT-024 infrastructure pods match caps.json' {
    $issues = @(Get-PodCapIssues @('metallb-system', 'ingress-nginx', 'prometheus', 'keda'))
    Assert-True ($issues.Count -eq 0) ($issues -join '; ')
}

Test-Check 'REQ-BT-024 infrastructure Docker containers match caps.json' {
    $issues = @(Get-ContainerCapIssues @($Caps.containers | Where-Object { $_.compose -ne 'line' }))
    Assert-True ($issues.Count -eq 0) ($issues -join '; ')
}

Test-Check 'REQ-BT-025 setup accepts caps.json as written' {
    $run = Invoke-SetupCaps (Copy-Caps)
    Assert-True ($run.Exit -eq 0) "exit $($run.Exit): $($run.Output)"
}

Test-Check 'REQ-BT-025 setup accepts a node sum exactly at its cap' {
    $edge = Copy-Caps
    $sums = Get-CapSums $edge
    $edge.nodes.'k3s-node1'.cpuMilli = $sums['k3s-node1'].cpu
    $edge.nodes.'k3s-node1'.memoryMi = $sums['k3s-node1'].memory
    $run = Invoke-SetupCaps $edge
    Assert-True ($run.Exit -eq 0) "exit $($run.Exit): $($run.Output)"
}

Test-Check 'REQ-BT-025 setup refuses CPU limits over a node cap' {
    $over = Copy-Caps
    $over.nodes.'k3s-node1'.cpuMilli = (Get-CapSums $over)['k3s-node1'].cpu - 1
    $run = Invoke-SetupCaps $over
    Assert-True (($run.Exit -ne 0) -and ($run.Output -match 'CPU limits .* exceed cap')) "exit $($run.Exit): $($run.Output)"
}

Test-Check 'REQ-BT-025 setup refuses memory limits over a node cap' {
    $over = Copy-Caps
    $over.nodes.'k3s-node2'.memoryMi = (Get-CapSums $over)['k3s-node2'].memory - 1
    $run = Invoke-SetupCaps $over
    Assert-True (($run.Exit -ne 0) -and ($run.Output -match 'memory limits .* exceed cap')) "exit $($run.Exit): $($run.Output)"
}

Test-Check 'REQ-BT-025 setup refuses memory requests over allocatable' {
    $over = Copy-Caps
    $over.reserved[0].requestMemoryMi = 100000
    $over.reserved[0].memoryMi = 100
    $run = Invoke-SetupCaps $over
    Assert-True (($run.Exit -ne 0) -and ($run.Output -match 'memory requests .* exceed allocatable')) "exit $($run.Exit): $($run.Output)"
}

Test-Check 'REQ-BT-025 setup refuses a missing entry' {
    $short = Copy-Caps
    $short.pods = @($short.pods | Where-Object { $_.name -ne 'prometheus-server' })
    $run = Invoke-SetupCaps $short
    Assert-True (($run.Exit -ne 0) -and ($run.Output -match 'entry missing: prometheus-server')) "exit $($run.Exit): $($run.Output)"
}

Remove-Item $capsDir -Recurse -Force
