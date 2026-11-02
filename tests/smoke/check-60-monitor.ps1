function Get-MonitorReport {
    (Invoke-Wsl curl -s "http://127.0.0.1:$MonitorPort/health") -join "`n" | ConvertFrom-Json
}

Test-Container 'batchtracker-monitor' @("127.0.0.1:$MonitorPort", "${HostIp}:$MonitorPort")

Test-Check 'monitor alive on loopback and LAN address' {
    Invoke-Probe http "http://127.0.0.1:$MonitorPort/alive"
    Invoke-Probe http "http://${HostIp}:$MonitorPort/alive"
}

Test-Check 'B-042 monitor gives the LAN address a status and no report' {
    $code = (Invoke-Wsl curl -s -o /dev/null -m 10 -w '%{http_code}' "http://${HostIp}:$MonitorPort/health") -join ''
    Assert-True ($code -match '^(200|503)$') "answered $code"
    $body = (Invoke-Wsl curl -s -m 10 "http://${HostIp}:$MonitorPort/health") -join "`n"
    $keys = (($body | ConvertFrom-Json).PSObject.Properties.Name | Sort-Object) -join ','
    Assert-True ($keys -eq 'checkedAt,healthy') "LAN address answered with: $keys"
    $full = Get-MonitorReport
    Assert-True (@($full.checks).Count -gt 0) 'loopback lost the report'
}

Test-Check 'monitor reports every check, freshly' {
    $report = Get-MonitorReport
    Assert-True (@($report.checks).Count -gt 0) 'no report yet'
    $age =([DateTimeOffset]::UtcNow - [DateTimeOffset]$report.checkedAt).TotalSeconds
    Assert-True ($age -lt 90) "report is $([int]$age) s old"
    $expected = @($Workers | ForEach-Object { "node $_" }) + @('control plane', 'workloads kube-system', 'workloads metallb-system',
        'workloads ingress-nginx', 'workloads prometheus', 'workloads keda', 'ingress address', 'service bus', 'mongo', 'caps', 'prometheus', 'monitor loops', 'nightly job', 'service bus throttle')
    $missing = @($expected | Where-Object { $report.checks.name -notcontains $_ })
    Assert-True ($missing.Count -eq 0) "missing $($missing -join ',')"
    Assert-True (($report.store -eq 'mongo') -ne ($report.checks.name -contains 'cosmos')) "store $($report.store) with the cosmos check wrong way round"
}

Test-Check 'REQ-BT-044 pod DNS and real UDP and TCP on every pair of nodes' {
    $names = @((Get-MonitorReport).checks.name)
    $expected = @($Workers | ForEach-Object { "pod dns $_" })
    foreach ($from in $Workers) {
        $expected += @($Workers | Where-Object { $_ -ne $from } | ForEach-Object { "pod net $from to $_" })
    }
    $missing = @($expected | Where-Object { $names -notcontains $_ })
    Assert-True ($missing.Count -eq 0) "missing $($missing -join ',')"
}

Test-Check 'TM-2213 report names only nodes in caps.json' {
    $text = (Invoke-Wsl curl -s "http://127.0.0.1:$MonitorPort/health") -join "`n"
    $named = @([regex]::Matches($text, 'k3s-[a-z0-9]+') | ForEach-Object { $_.Value } | Sort-Object -Unique)
    $unknown = @($named | Where-Object { $Workers -notcontains $_ })
    Assert-True ($unknown.Count -eq 0) "names $($unknown -join ',')"
}

Test-Check 'monitor compares against the deployed caps.json, not the working file' {
    $mount = @($script:container.Mounts | Where-Object { $_.Destination -eq '/caps.json' })[0]
    Assert-True ($mount.Source -match '/deploy/\.release/deploy/caps\.json$') "mounts $($mount.Source)"
    Assert-True ($mount.RW -eq $false) 'writable'
}

Test-Check 'monitor reports all healthy' {
    $failing = @((Get-MonitorReport).checks | Where-Object { -not $_.healthy } | ForEach-Object { "$($_.name): $($_.error)" })
    Assert-True ($failing.Count -eq 0) ($failing -join '; ')
}

Test-Check 'REQ-BT-026 node list is exactly the control plane and every worker' {
    $report = Get-MonitorReport
    $actual = (@($report.nodes.name) | Sort-Object) -join ','
    $expected = (@($Workers) + $ControlPlane | Sort-Object) -join ','
    Assert-True ($actual -eq $expected) "nodes $actual, expected $expected"
    $row = @($report.nodes | Where-Object { $_.name -eq $ControlPlane })[0]
    Assert-True ($row.role -eq 'control plane') "role $($row.role)"
    Assert-True ($row.ready) 'control plane not ready'
    Assert-True ($null -eq $row.allocatableMemoryMi) 'control plane has an allocatable value'
    Assert-True (($row.memoryUsageMi -gt 0) -and ($null -ne $row.cpuUsageMilli)) 'control plane has no usage'
}

Test-Check 'REQ-BT-026 monitor reports node and pod usage' {
    $report = Get-MonitorReport
    $nodes = @($report.nodes | Where-Object { $_.ready -and ($null -ne $_.cpuUsageMilli) -and ($_.memoryUsageMi -gt 0) -and ($_.allocatableMemoryMi -gt 0) })
    Assert-True ($nodes.Count -eq $Workers.Count) "$($nodes.Count) nodes with usage"
    $pods = @($report.pods | Where-Object { $_.namespace -ne 'kube-system' })
    Assert-True ($pods.Count -gt 0) 'no pods in report'
    $unmeasured = @($pods | Where-Object { ($_.phase -eq 'Running') -and ($_.memoryLimitMi -le 0 -or $_.cpuLimitMilli -le 0 -or $null -eq $_.memoryUsageMi) })
    Assert-True ($unmeasured.Count -eq 0) "no limit or usage: $($unmeasured.name -join ',')"
}

Test-Check 'REQ-BT-026 monitor caps match caps.json sums, no breach' {
    $report = Get-MonitorReport
    $sums = Get-CapSums $Caps
    Assert-True (@($report.caps).Count -eq 4) "$(@($report.caps).Count) cap rows"
    foreach ($row in $report.caps) {
        $expected = if ($row.resource -eq 'cpuMilli') { $sums[$row.node].cpu } else { $sums[$row.node].memory }
        Assert-True ($row.sum -eq $expected) "$($row.node) $($row.resource) sum $($row.sum), file gives $expected"
        Assert-True ($row.cap -eq $Caps.nodes.($row.node).($row.resource)) "$($row.node) $($row.resource) cap $($row.cap)"
        Assert-True (-not $row.breach) "$($row.node) $($row.resource) in breach"
    }
    Assert-True (@($report.capIssues).Count -eq 0) ($report.capIssues -join '; ')
}

Test-Check 'REQ-BT-026 monitor WebSocket pushes the report to the ingress origin' {
    Invoke-AdminProbe ws-report "ws://${HostIp}:$MonitorPort/ws" $IngressOrigin
}

Test-Check 'monitor WebSocket refuses another origin' {
    $refused = $false
    try { Invoke-Probe ws "ws://${HostIp}:$MonitorPort/ws" 'http://example.invalid' } catch { $refused = "$_" -match '403' }
    Assert-True $refused 'another origin got a socket'
}

Test-Check 'monitor report is fresh after a restart' {
    $restartedAt = [DateTimeOffset]::UtcNow
    Invoke-Wsl docker restart batchtracker-monitor | Out-Null
    Wait-Until 60 {
        $report = Get-MonitorReport
        Assert-True (@($report.checks).Count -gt 0) 'no checks yet'
        Assert-True ([DateTimeOffset]$report.checkedAt -gt $restartedAt) 'report older than the restart'
    }
}
