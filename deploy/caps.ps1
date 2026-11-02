# Reads deploy/caps.json, refuses it when it breaks a rule, turns it into Helm values and compose env.

# A name gone from caps.json must fail: a chart default would otherwise apply without a word.
$RequiredCaps = @(
    'ingress-nginx', 'ingress-nginx-create', 'ingress-nginx-patch', 'metallb-controller', 'metallb-speaker',
    'prometheus-server', 'prometheus-reload', 'keda-operator', 'keda-metrics-apiserver', 'keda-webhooks',
    'api', 'worker', 'worker-node2', 'dispatcher', 'dispatcher-node2', 'live', 'web', 'nightly',
    'batchtracker-servicebus', 'batchtracker-servicebus-sql', 'batchtracker-cosmos', 'batchtracker-monitor', 'batchtracker-line', 'batchtracker-mongo'
)

function Read-Caps([string]$Path) {
    $caps = (Get-Content -Raw -Path $Path) | ConvertFrom-Json
    foreach ($section in 'nodes', 'reserved', 'pods', 'containers') {
        if ($null -eq $caps.$section) { throw "caps: section $section missing" }
    }
    return $caps
}

function Assert-CapEntry($Entry, [string[]]$Fields) {
    foreach ($field in $Fields) {
        if ($null -eq $Entry.$field) { throw "caps: $($Entry.name) has no $field" }
    }
}

function Get-CapSums($Caps) {
    $nodeNames = @($Caps.nodes.PSObject.Properties.Name)
    $sums = @{}
    foreach ($node in $nodeNames) { $sums[$node] = @{ cpu = 0; memory = 0; requestCpu = 0; requestMemory = 0 } }

    $entries = @($Caps.reserved) + @($Caps.pods)
    foreach ($entry in $entries) {
        Assert-CapEntry $entry @('name', 'node', 'cpuMilli', 'memoryMi', 'requestCpuMilli', 'requestMemoryMi')
        $replicas = if ($null -eq $entry.replicas) { 1 } else { [int]$entry.replicas }
        $targets = if ($entry.node -eq 'all') { $nodeNames } else { @($entry.node) }
        foreach ($target in $targets) {
            if ($nodeNames -notcontains $target) { throw "caps: $($entry.name) on unknown node $target" }
            $sums[$target].cpu += $entry.cpuMilli * $replicas
            $sums[$target].memory += $entry.memoryMi * $replicas
            $sums[$target].requestCpu += $entry.requestCpuMilli * $replicas
            $sums[$target].requestMemory += $entry.requestMemoryMi * $replicas
        }
    }
    return $sums
}

function Test-Caps($Caps, [hashtable]$Allocatable) {
    $present = @($Caps.pods.name) + @($Caps.containers.name)
    $missing = @($RequiredCaps | Where-Object { $present -notcontains $_ })
    if ($missing.Count -gt 0) { throw "caps: entry missing: $($missing -join ', ')" }
    foreach ($container in $Caps.containers) { Assert-CapEntry $container @('name', 'compose', 'env', 'cpuMilli', 'memoryMi') }

    $sums = Get-CapSums $Caps
    foreach ($node in @($Caps.nodes.PSObject.Properties.Name)) {
        $cap = $Caps.nodes.$node
        $sum = $sums[$node]
        if ($sum.cpu -gt $cap.cpuMilli) { throw "caps: $node CPU limits $($sum.cpu)m exceed cap $($cap.cpuMilli)m" }
        if ($sum.memory -gt $cap.memoryMi) { throw "caps: $node memory limits $($sum.memory)Mi exceed cap $($cap.memoryMi)Mi" }
        if ($Allocatable -and $Allocatable.ContainsKey($node)) {
            $free = $Allocatable[$node]
            if ($sum.requestMemory -gt $free) { throw "caps: $node memory requests $($sum.requestMemory)Mi exceed allocatable $($free)Mi" }
        }
        $scope = if ($Allocatable -and $Allocatable.ContainsKey($node)) { ", allocatable $($Allocatable[$node])Mi" } else { '' }
        Write-Output "[OK] caps $node : CPU $($sum.cpu)m of $($cap.cpuMilli)m, memory $($sum.memory)Mi of $($cap.memoryMi)Mi, memory requests $($sum.requestMemory)Mi$scope"
    }
}

# Unset means every client behind the ingress is one address, so nine anonymous failures would refuse them all.
function Get-TrustedProxyGaps([string]$Rendered, [string]$Line, [string]$Monitor) {
    $deployments = @($Rendered -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: Deployment' })
    foreach ($name in 'api', 'live') {
        $held = @($deployments | Where-Object { $_ -match "(?m)^  name: $name\s" -and $_ -match 'name: TRUSTED_PROXIES\s+value: "?[0-9]' })
        if ($held.Count -eq 0) { $name }
    }
    if ($Line -notmatch '(?m)^\s+TRUSTED_PROXIES: [0-9]') { 'line' }
    if ($Monitor -notmatch '(?m)^\s+TRUSTED_PROXIES: [0-9]') { 'monitor' }
}

function Assert-TrustedProxies([string]$Rendered, [string]$DeployDir) {
    $line = Get-Content (Join-Path $DeployDir 'line\docker-compose.yaml') -Raw
    $monitor = Get-Content (Join-Path $DeployDir 'monitor\docker-compose.yaml') -Raw
    $gaps = @(Get-TrustedProxyGaps $Rendered $line $monitor)
    if ($gaps.Count -gt 0) { throw "TRUSTED_PROXIES unset on: $($gaps -join ', ')" }
    Write-Output '[OK] TRUSTED_PROXIES set on api, live, line, monitor'
}

function Set-Path([hashtable]$Root, [string]$Path, $Value) {
    $parts = $Path.Split('.')
    $current = $Root
    for ($i = 0; $i -lt $parts.Count - 1; $i++) {
        if (-not $current.ContainsKey($parts[$i])) { $current[$parts[$i]] = @{} }
        $current = $current[$parts[$i]]
    }
    $current[$parts[-1]] = $Value
}

function Get-ChartValues($Caps, [string]$Chart) {
    $values = @{}
    foreach ($entry in @($Caps.pods | Where-Object { $_.chart -eq $Chart })) {
        Set-Path $values $entry.path @{
            limits   = @{ cpu = "$($entry.cpuMilli)m"; memory = "$($entry.memoryMi)Mi" }
            requests = @{ cpu = "$($entry.requestCpuMilli)m"; memory = "$($entry.requestMemoryMi)Mi" }
        }
        if ($entry.nodePath) { Set-Path $values $entry.nodePath @{ 'kubernetes.io/hostname' = $entry.node } }
        if ($entry.replicasPath) { Set-Path $values $entry.replicasPath ([int]$entry.replicas) }
    }
    return $values
}

function Get-ComposeCaps($Caps, [string]$Compose) {
    $lines = @()
    foreach ($container in @($Caps.containers | Where-Object { $_.compose -eq $Compose })) {
        $cpus = ([decimal]$container.cpuMilli / 1000).ToString([Globalization.CultureInfo]::InvariantCulture)
        $lines += "$($container.env)_CPU=$cpus"
        $lines += "$($container.env)_MEMORY=$($container.memoryMi)m"
    }
    return $lines
}

function ConvertTo-MemoryMi([string]$Quantity) {
    if ($Quantity -match '^(\d+)Ki$') { return [int][Math]::Floor([decimal]$Matches[1] / 1024) }
    if ($Quantity -match '^(\d+)Mi$') { return [int]$Matches[1] }
    if ($Quantity -match '^(\d+)Gi$') { return [int]$Matches[1] * 1024 }
    if ($Quantity -match '^\d+$') { return [int][Math]::Floor([decimal]$Quantity / 1048576) }
    throw "memory quantity not understood: $Quantity"
}

function ConvertTo-CpuMilli([string]$Quantity) {
    if ($Quantity -match '^(\d+)m$') { return [int]$Matches[1] }
    if ($Quantity -match '^\d+(\.\d+)?$') { return [int]([decimal]$Quantity * 1000) }
    throw "CPU quantity not understood: $Quantity"
}
