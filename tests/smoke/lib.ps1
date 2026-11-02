# Shared helpers for smoke checks.
$Distro = 'Ubuntu-24.04'
$HostIp = '<HOST_IP>'
$AmqpPort = 5682
$AdminPort = 5310
$CosmosPort = 8091
$CosmosHealthPort = 8092
$CosmosExplorerPort = 1244
$MonitorPort = 5320
$ProbeDir = Join-Path $PSScriptRoot 'probe'
$ProbePublish = Join-Path $ProbeDir 'publish'
$DeployDir = Join-Path $PSScriptRoot '..\..\deploy'
$AppNamespace = 'batch-tracker'
$IngressAddress = '<INGRESS_IP>'
$IngressOrigin = "http://$IngressAddress"
$LinePort = 5321
$MonitorContainer = 'batchtracker-monitor'
. (Join-Path $DeployDir 'caps.ps1')
# Smoke compares the cluster to what was deployed, never to a file someone is editing.
$ReleaseDir = Join-Path $DeployDir '.release\deploy'
$Caps = Read-Caps (Join-Path $ReleaseDir 'caps.json')
$Workers = @($Caps.nodes.PSObject.Properties.Name)
$ControlPlane = 'Node1'

$script:Passed = 0
$script:Failed = 0

function Test-Check([string]$Name, [scriptblock]$Test) {
    try {
        & $Test
        $script:Passed++
        Write-Output "[OK] $Name"
    } catch {
        $script:Failed++
        $message = $_.Exception.Message
        Write-Output "[FAIL] $Name : $($message.Substring(0, [Math]::Min(300, $message.Length)))"
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-Container([string]$containerName, [string[]]$expectedBindings) {
    $script:container = $null

    Test-Check "$containerName running" {
        $script:container = (Invoke-Wsl docker inspect $containerName) -join "`n" | ConvertFrom-Json
        Assert-True ($script:container.State.Status -eq 'running') "status $($script:container.State.Status)"
        Assert-True (-not $script:container.State.OOMKilled) 'killed for memory'
    }

    Test-Check "$containerName restart policy and memory limit" {
        Assert-True ($script:container.HostConfig.RestartPolicy.Name -eq 'unless-stopped') 'restart policy differs'
        Assert-True ($script:container.HostConfig.Memory -gt 0) 'no memory limit'
    }

    Test-Check "$containerName bound only to expected addresses" {
        Assert-True ($null -ne $script:container) 'container not found'
        $bindings = @($script:container.HostConfig.PortBindings.PSObject.Properties.Value |
            ForEach-Object { $_ } | Where-Object { $_ } | ForEach-Object { "$($_.HostIp):$($_.HostPort)" })
        $actual = ($bindings | Sort-Object) -join ','
        $expected = ($expectedBindings | Sort-Object) -join ','
        Assert-True ($actual -eq $expected) "bound to $actual"
    }
}

function Get-IngressPrefix([object[]]$Command) {
    # A root namespace socket can land on a port Docker's NAT holds. Conntrack then moves it outside the range WSL
    # receives on and the connect hangs. Line and Monitor go through that NAT, so ingress rows take their path.
    $targets = if ($Command[0] -eq 'curl') { $Command } elseif ($Command[0] -eq './probe') { $Command[2] }
    $ingress = "^([a-z]+://)?$([regex]::Escape($IngressAddress))([:/]|$)"
    if (@(@($targets) -match $ingress).Count -eq 0) { return @() }
    $state = try { ((Invoke-Wsl docker inspect $MonitorContainer) -join "`n" | ConvertFrom-Json).State } catch { $null }
    if ($state.Status -ne 'running') { throw "ingress row needs $MonitorContainer and it is not running" }
    return @('sudo', '-n', '--preserve-env=PROBE_SESSION', 'nsenter', '-t', "$($state.Pid)", '-n')
}

function Get-NatHeldPorts([string[]]$Table) {
    $open = "ESTABLISHED src=172\.\S+ dst=$([regex]::Escape($IngressAddress)) sport=\d+ dport=80 .* dport=(\d+) "
    return @($Table | ForEach-Object { if ($_ -match $open) { $Matches[1] } })
}

function Invoke-Wsl {
    $prefix = Get-IngressPrefix $args
    # Warnings on stderr must stay out of the result, callers parse it as JSON.
    $output = wsl -d $Distro -- @prefix @args 2>&1
    $exitCode = $LASTEXITCODE
    $stdout = $output | Where-Object { $_ -isnot [Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }
    if ($exitCode -ne 0) { throw "$($args -join ' ') failed: $($output -join ' ')" }
    return $stdout
}

function Invoke-Kubectl { Invoke-Wsl k3s kubectl @args }

function Invoke-Helm { Invoke-Wsl env KUBECONFIG=/etc/rancher/k3s/k3s.yaml helm @args }

function Get-KubeJson { (Invoke-Kubectl @args -o json) -join "`n" | ConvertFrom-Json }

function Get-PrometheusResult([string]$Query) {
    # EscapeDataString leaves parentheses alone here, and the shell inside wsl would read them.
    $encoded = [uri]::EscapeDataString($Query).Replace('(', '%28').Replace(')', '%29')
    $path = "/api/v1/namespaces/prometheus/services/http:prometheus-server:80/proxy/api/v1/query?query=$encoded"
    ((Invoke-Kubectl get --raw $path) -join "`n" | ConvertFrom-Json).data.result
}

function Wait-Until([int]$Seconds, [scriptblock]$Test) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ($true) {
        try {
            & $Test
            return
        } catch {
            if ((Get-Date) -gt $deadline) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

# FailureRules.Quiet: only this long without a failure clears a count, so a second run keeps adding to the first.
$FailureQuietMinutes = 15
$FailureStamp = Join-Path $env:TEMP 'batchtracker-smoke-failures.txt'

function Get-FailureWait([string]$Stamp, [datetime]$Now) {
    if (-not (Test-Path $Stamp)) { return 0 }
    $left = ([datetime]::Parse((Get-Content $Stamp -Raw).Trim()).AddMinutes($FailureQuietMinutes) - $Now).TotalMinutes
    return [int][Math]::Max(0, [Math]::Ceiling($left))
}

function Get-SpoofGap([string[]]$Answers) {
    # Each spoofed value believed would be a new client with free failures, so the third answer would not wait.
    if ($Answers.Count -ne 3) { return "wanted 3 answers, got $($Answers.Count)" }
    foreach ($answer in $Answers) {
        $code = ($answer -split ' ')[0]
        if ($code -ne '401') { return "answered $code" }
    }
    $last = [double]::Parse(($Answers[2] -split ' ')[1], [Globalization.CultureInfo]::InvariantCulture)
    if ($last -lt 0.9) { return "header believed: the third failure waited $last s" }
    return ''
}

function Invoke-Probe {
    $prefix = Get-IngressPrefix (@('./probe') + $args)
    $output = wsl -d $Distro --cd $ProbePublish -- @prefix ./probe @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "probe $($args -join ' ') failed: $($output -join ' ')" }
}

function Get-AdaSession {
    Invoke-RestMethod -Method Post -Uri "$IngressOrigin/api/auth/dev-login" -ContentType 'application/json' -Body '{"user":"ada"}' -SessionVariable session | Out-Null
    return $session
}

function Invoke-AdminProbe {
    # By environment: a failed probe prints its arguments, and a session must not reach a log.
    $env:PROBE_SESSION = (Get-AdaSession).Cookies.GetCookies([uri]$IngressOrigin)['bt_session'].Value
    $env:WSLENV = 'PROBE_SESSION'
    try { Invoke-Probe @args } finally { Remove-Item Env:PROBE_SESSION, Env:WSLENV }
}

function Invoke-StoreProbe {
    # By environment: a failed probe prints its arguments, and the connection holds a password.
    $line = @(Get-Content (Join-Path $DeployDir 'app\.env') | Where-Object { $_ -match '^MONGO_CONNECTION=' })[0]
    $env:PROBE_MONGO = ($line -split '=', 2)[1]
    $env:WSLENV = 'PROBE_MONGO'
    try { Invoke-Probe @args } finally { Remove-Item Env:PROBE_MONGO, Env:WSLENV }
}

function Get-PodCapIssues([string[]]$Namespaces) {
    # Compares running pods to caps.json: limits, node and pod count.
    $issues = @()
    foreach ($namespace in $Namespaces) {
        $pods = @((Get-KubeJson get pods -n $namespace --field-selector=status.phase!=Succeeded).items |
            Where-Object { -not $_.metadata.deletionTimestamp })
        if ($pods.Count -eq 0) { $issues += "$namespace has no pods" }
        $counted = @{}
        foreach ($pod in $pods) {
            $name = $pod.metadata.name
            if (@($Caps.reserved | Where-Object { $_.namespace -eq $namespace -and $name -match $_.pod }).Count -gt 0) { continue }
            foreach ($container in $pod.spec.containers) {
                $entry = $Caps.pods | Where-Object { $_.namespace -eq $namespace -and $_.container -eq $container.name -and $name -cmatch $_.pod } | Select-Object -First 1
                if (-not $entry) { $issues += "$name/$($container.name) not in caps.json"; continue }
                $counted[$entry.name] = @($counted[$entry.name]) + $name | Where-Object { $_ } | Sort-Object -Unique
                $limits = $container.resources.limits
                if (-not $limits.cpu -or -not $limits.memory) { $issues += "$name/$($container.name) has no CPU or memory limit"; continue }
                $cpu = ConvertTo-CpuMilli $limits.cpu
                $memory = ConvertTo-MemoryMi $limits.memory
                if ($cpu -ne $entry.cpuMilli -or $memory -ne $entry.memoryMi) {
                    $issues += "$name/$($container.name) limit ${cpu}m ${memory}Mi, file says $($entry.cpuMilli)m $($entry.memoryMi)Mi"
                }
                if ($entry.node -ne 'all' -and $pod.spec.nodeName -ne $entry.node) { $issues += "$name on $($pod.spec.nodeName), file says $($entry.node)" }
            }
        }
        foreach ($entry in @($Caps.pods | Where-Object { $_.namespace -eq $namespace })) {
            $allowed = $entry.replicas * $(if ($entry.node -eq 'all') { @($Caps.nodes.PSObject.Properties.Name).Count } else { 1 })
            $count = @($counted[$entry.name]).Count
            if ($count -gt $allowed) { $issues += "$($entry.name) has $count pods, file allows $allowed" }
            if ($count -eq 0 -and -not $entry.transient) { $issues += "$($entry.name) has no pod" }
        }
    }
    return $issues
}

function Get-ContainerCapIssues([object[]]$Containers) {
    $issues = @()
    foreach ($entry in $Containers) {
        $live = ((Invoke-Wsl docker inspect $entry.name) -join "`n" | ConvertFrom-Json).HostConfig
        $cpu = [int]($live.NanoCpus / 1000000)
        $memory = [int]($live.Memory / 1048576)
        if ($cpu -ne $entry.cpuMilli -or $memory -ne $entry.memoryMi) {
            $issues += "$($entry.name) limit ${cpu}m ${memory}Mi, file says $($entry.cpuMilli)m $($entry.memoryMi)Mi"
        }
    }
    return $issues
}

function Invoke-PodProbe([string]$Pod) {
    $command = @('-n', $SmokeNamespace, 'exec', $Pod, '--', '/tmp/probe') + $args
    Invoke-Kubectl @command | Out-Null
}
