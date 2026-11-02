# The running app. Fails until `deploy\setup.ps1` has installed it.
$WorkerRouteHost = 'worker.batch-tracker.local'

function Get-AppPods {
    $pods = @((Get-KubeJson get pods -n $AppNamespace --field-selector=status.phase=Running).items | Where-Object { -not $_.metadata.deletionTimestamp })
    Assert-True ($pods.Count -gt 0) 'no app pods'
    return $pods
}

function Get-MonitorCheck([string]$Name) {
    $report = (Invoke-Wsl curl -s "http://127.0.0.1:$MonitorPort/health") -join "`n" | ConvertFrom-Json
    $report.checks | Where-Object { $_.name -eq $Name }
}

Test-Check 'app release deployed' {
    $status = (Invoke-Helm status batch-tracker -n $AppNamespace -o json) -join "`n" | ConvertFrom-Json
    Assert-True ($status.info.status -eq 'deployed') "status $($status.info.status)"
}

Test-Check 'REQ-BT-023 node2 holds only worker-node2, its netprobe and at most one dispatcher pod' {
    $onNode2 = @(Get-AppPods | Where-Object { $_.spec.nodeName -eq 'k3s-node2' })
    Assert-True ($onNode2.Count -gt 0) 'no app pod on k3s-node2'
    $other = @($onNode2 | Where-Object { $_.metadata.name -notmatch '^(worker-node2|dispatcher-node2|netprobe)-' })
    Assert-True ($other.Count -eq 0) "also on node2: $($other.metadata.name -join ',')"
    $dispatchers = @($onNode2 | Where-Object { $_.metadata.name -match '^dispatcher' })
    Assert-True ($dispatchers.Count -le 1) "$($dispatchers.Count) dispatcher pods on node2"
}

Test-Check 'REQ-BT-023 every other app pod on k3s-node1' {
    $wrong = @(Get-AppPods | Where-Object { $_.metadata.name -notmatch '^(worker-node2|dispatcher-node2|netprobe)-' -and $_.spec.nodeName -ne 'k3s-node1' })
    Assert-True ($wrong.Count -eq 0) "elsewhere: $($wrong.metadata.name -join ',')"
}

Test-Check 'REQ-BT-024 app pods match caps.json' {
    $issues = @(Get-PodCapIssues @($AppNamespace))
    Assert-True ($issues.Count -eq 0) ($issues -join '; ')
}

Test-Container 'batchtracker-line' @("127.0.0.1:$LinePort", "${HostIp}:$LinePort")

Test-Check 'REQ-BT-024 line container matches caps.json' {
    $issues = @(Get-ContainerCapIssues @($Caps.containers | Where-Object { $_.compose -eq 'line' }))
    Assert-True ($issues.Count -eq 0) ($issues -join '; ')
}

Test-Check 'images are local and tagged by content' {
    foreach ($pod in Get-AppPods) {
        foreach ($container in $pod.spec.containers) {
            Assert-True ($container.imagePullPolicy -eq 'Never') "$($pod.metadata.name) pull policy $($container.imagePullPolicy)"
            Assert-True ($container.image -match ':[0-9a-f]{12}$') "$($pod.metadata.name) image $($container.image)"
        }
    }
}

Test-Check 'ingress serves web, api and the Worker route' {
    Invoke-Probe http "$IngressOrigin/"
    Invoke-Probe http "$IngressOrigin/api/healthz"
    # Without the route this Host falls through to web, which answers 200 to any path. From outside the pod network the ingress refuses.
    $code = (Invoke-Wsl curl -s -o /dev/null -w '%{http_code}' -H "Host: $WorkerRouteHost" "$IngressOrigin/ready") -join ''
    Assert-True ($code -eq '403') "Worker route /ready answered $code"
}

Test-Check 'T-011 the trusted lists still hold the ingress' {
    $pod = @((Get-KubeJson get pods -n ingress-nginx -l app.kubernetes.io/component=controller).items)[0]
    $node = @((Get-KubeJson get node $pod.spec.nodeName).status.addresses | Where-Object { $_.type -eq 'InternalIP' })[0]
    $chart = Get-Content (Join-Path $ReleaseDir 'app\values.yaml') -Raw
    $network = [regex]::Match($chart, '(?m)^ingressPodNetwork: (\d+\.\d+\.\d+)\.0/24').Groups[1].Value
    Assert-True ($network -and $pod.status.podIP.StartsWith("$network.")) "ingress pod $($pod.status.podIP) outside the chart's network"
    foreach ($service in 'line', 'monitor') {
        $compose = Get-Content (Join-Path $ReleaseDir "$service\docker-compose.yaml") -Raw
        Assert-True ($compose -match "TRUSTED_PROXIES: $([regex]::Escape($node.address))/32") "$service does not trust the ingress node $($node.address)"
    }
}

Test-Check 'F14 the ingress serves /metrics/series to an admin only' {
    $code = (Invoke-Wsl curl -s -m 15 -o /dev/null -w '%{http_code}' "$IngressOrigin/metrics/series?bucket=1m") -join ''
    Assert-True ($code -eq '401') "anonymous answered $code"
    $reply = Invoke-RestMethod -Uri "$IngressOrigin/metrics/series?bucket=1m" -WebSession (Get-AdaSession) -TimeoutSec 15
    Assert-True ($reply.available -and @($reply.series).Count -eq 14) "admin got available=$($reply.available), $(@($reply.series).Count) series"
}

Test-Check 'REQ-BT-056 a request is found at the ingress by its id, for an admin only' {
    $code = (Invoke-Wsl curl -s -m 15 -o /dev/null -w '%{http_code}' "$IngressOrigin/ingress/logs?correlationId=smoke") -join ''
    Assert-True ($code -eq '401') "anonymous answered $code"
    $id = "smoke-$([guid]::NewGuid().ToString('N'))"
    Invoke-Wsl curl -s -m 15 -o /dev/null -H "X-Correlation-Id: $id" "$IngressOrigin/api/healthz?marker=hidden" | Out-Null
    $session = Get-AdaSession
    Wait-Until 15 {
        $reply = Invoke-RestMethod -Uri "$IngressOrigin/ingress/logs?correlationId=$id" -WebSession $session -TimeoutSec 15
        Assert-True (@($reply.rows).Count -eq 1) "$(@($reply.rows).Count) rows"
        Assert-True (($reply.rows[0].status -eq 200) -and ($reply.rows[0].path -eq '/api/healthz')) "row: $($reply.rows[0] | ConvertTo-Json -Compress)"
    }
}

Test-Check 'ingress never routes Live /hops' {
    # A file, because quotes do not survive the call into wsl. A routed Live answers 401 without the token, so 401 passes
    # only where a route exists by design: Live's own /ws/hops and the Worker route, which answers 403 from outside the cluster.
    $hopFile = Join-Path $env:TEMP 'batchtracker-smoke-hop.json'
    [IO.File]::WriteAllText($hopFile, '{"traceId":"smoke","batchId":"smoke","farmId":"smoke","hop":"api","state":"smoke","at":"2026-01-01T00:00:00Z","ok":true}')
    $hopPath = "@$(Invoke-Wsl wslpath -a ($hopFile -replace '\\', '/'))"
    $notRouted = '^(404|405)$'
    $byDesign = '^(401|403|404|405)$'
    foreach ($path in '/hops', '/ws/../hops', '/ws/%2e%2e/hops', '/api/../hops') {
        $code = (Invoke-Wsl curl -s --path-as-is -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data-binary $hopPath "$IngressOrigin$path") -join ''
        Assert-True ($code -match $notRouted) "POST $path answered $code"
    }
    $code = (Invoke-Wsl curl -s --path-as-is -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data-binary $hopPath "$IngressOrigin/ws/hops") -join ''
    Assert-True ($code -match $byDesign) "POST /ws/hops answered $code"
    $code = (Invoke-Wsl curl -s -o /dev/null -w '%{http_code}' -X POST -H "Host: $WorkerRouteHost" -H 'Content-Type: application/json' --data-binary $hopPath "$IngressOrigin/hops") -join ''
    Assert-True ($code -match $byDesign) "POST /hops on the Worker route answered $code"
    Remove-Item $hopFile -Force
}

Test-Check 'REQ-BT-047 Live refuses a report with no token inside the cluster' {
    # The API proxy reaches Live without the ingress, so this proves the token, not routing.
    $live = "/api/v1/namespaces/$AppNamespace/services/live:8080/proxy"
    # /healthz is open, so reaching it means a later 401 came from Live and not from the API server.
    Invoke-Kubectl get --raw "$live/healthz" | Out-Null
    $bodyFile = Join-Path $env:TEMP 'batchtracker-smoke-report.json'
    [IO.File]::WriteAllText($bodyFile, '{}')
    $bodyPath = Invoke-Wsl wslpath -a ($bodyFile -replace '\\', '/')
    foreach ($path in '/logs', '/hops', '/changes') {
        $answer = try { Invoke-Kubectl create --raw "$live$path" -f $bodyPath | Out-Null; 'accepted' } catch { "$_" }
        # kubectl's wording for a 401. Any other answer names another status.
        Assert-True ($answer -match 'must be logged in to the server') "POST $path answered: $answer"
    }
    Remove-Item $bodyFile -Force
}

Test-Check 'dispatcher pods hold the Lease with their own RBAC, exactly one connected' {
    $lease = Get-KubeJson get lease dispatcher -n $AppNamespace
    $holder = $lease.spec.holderIdentity
    $dispatchers = @(Get-AppPods | Where-Object { $_.metadata.name -match '^dispatcher' })
    Assert-True ($dispatchers.Count -eq 3) "$($dispatchers.Count) dispatcher pods"
    Assert-True ($dispatchers.metadata.name -contains $holder) "holder $holder is not a running dispatcher"
    $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$lease.spec.renewTime).TotalSeconds
    Assert-True ($age -lt 30) "Lease last renewed $([int]$age) s ago"
    $connected = @()
    foreach ($pod in $dispatchers) {
        $status = (Invoke-Kubectl get --raw "/api/v1/namespaces/$AppNamespace/pods/$($pod.metadata.name):8080/proxy/status") -join '' | ConvertFrom-Json
        if ($status.connected) { $connected += $pod.metadata.name }
    }
    Assert-True (($connected.Count -eq 1) -and ($connected[0] -eq $holder)) "connected: $($connected -join ','), holder $holder"
}

Test-Check 'a real event reaches the Worker through the ingress Host route' {
    Invoke-RestMethod -Method Post -Uri "$IngressOrigin/api/auth/dev-login" -ContentType 'application/json' -Body '{"user":"olivia"}' -SessionVariable session | Out-Null
    $headers = @{ Origin = $IngressOrigin }
    $query = "sum(nginx_ingress_controller_requests{ingress=`"batch-tracker-worker`",status=`"200`"})"
    $before = [double](@(Get-PrometheusResult $query) | ForEach-Object { $_.value[1] } | Select-Object -First 1)
    $created = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$IngressOrigin/api/batches" -WebSession $session -Headers $headers -ContentType 'application/json' -Body '{"farmId":"farm-a","product":"smoke"}'
    $batch = $created.Content | ConvertFrom-Json
    try {
        Wait-Until 30 {
            $read = Invoke-RestMethod -Uri "$IngressOrigin/api/batches/farm-a/$($batch.id)" -WebSession $session
            Assert-True ($null -eq $read.pending) 'event still pending'
            Assert-True ([bool]$read.steps[-1].recordId) 'no compliance record'
        }
        Wait-Until 60 {
            $after = [double](@(Get-PrometheusResult $query) | ForEach-Object { $_.value[1] } | Select-Object -First 1)
            Assert-True ($after -gt $before) "Worker route count stayed at $before"
        }
    } finally {
        Invoke-StoreProbe overdue-delete $batch.id 'farm-a'
    }
}

Test-Check 'KEDA ScaledObjects ready' {
    foreach ($name in 'api', 'worker', 'worker-node2') {
        $scaled = Get-KubeJson get scaledobject $name -n $AppNamespace
        $ready = ($scaled.status.conditions | Where-Object { $_.type -eq 'Ready' }).status
        Assert-True ($ready -eq 'True') "$name ready $ready"
    }
}

Test-Check 'dispatcher may use only its Lease' {
    $account = "--as=system:serviceaccount:${AppNamespace}:dispatcher"
    $allowed = (Invoke-Kubectl auth can-i update lease/dispatcher -n $AppNamespace $account) -join ''
    Assert-True ($allowed -eq 'yes') "update Lease: $allowed"
    $denied = wsl -d $Distro -- k3s kubectl auth can-i get secrets -n $AppNamespace $account 2>$null
    Assert-True ("$denied" -eq 'no') "get Secrets: $denied"
}

Test-Check 'line answers on loopback and LAN address' {
    Invoke-Probe http "http://127.0.0.1:$LinePort/healthz"
    Invoke-Probe http "http://${HostIp}:$LinePort/healthz"
}

Test-Check 'REQ-BT-027 monitor reaches the API pod path' {
    Wait-Until 45 {
        $check = Get-MonitorCheck 'app api ready'
        Assert-True ($null -ne $check) 'check not in report'
        Assert-True $check.healthy "failing: $($check.error)"
    }
}

Test-Check 'monitor reports app workloads ready' {
    Wait-Until 45 {
        $check = Get-MonitorCheck "workloads $AppNamespace"
        Assert-True ($null -ne $check) 'check not in report'
        Assert-True $check.healthy "failing: $($check.error)"
    }
}

Test-Check 'REQ-BT-037 chart rolls back' {
    $history = (Invoke-Helm history batch-tracker -n $AppNamespace -o json) -join "`n" | ConvertFrom-Json
    $before = @($history)[-1].revision
    $marker = [guid]::NewGuid().ToString('N')
    Invoke-Helm upgrade batch-tracker (Invoke-Wsl wslpath -a ((Join-Path $ReleaseDir 'app') -replace '\\', '/')) -n $AppNamespace --reuse-values --set "rolloutMarker=$marker" --wait --timeout 300s | Out-Null
    $changed = (Get-KubeJson get deployment web -n $AppNamespace).spec.template.metadata.annotations.'batch-tracker/rollout-marker'
    Assert-True ($changed -eq $marker) 'upgrade did not reach the Deployment'
    Invoke-Helm rollback batch-tracker $before -n $AppNamespace --wait --timeout 300s | Out-Null
    $restored = (Get-KubeJson get deployment web -n $AppNamespace).spec.template.metadata.annotations.'batch-tracker/rollout-marker'
    Assert-True (-not $restored) 'rollback left the marker in place'
    Invoke-Probe http "$IngressOrigin/api/healthz"
}

$overdueId = "smoke-$([guid]::NewGuid().ToString('N'))"

function Invoke-NightlyJob {
    $job = "smoke-nightly-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    Invoke-Kubectl create job $job --from=cronjob/nightly -n $AppNamespace | Out-Null
    try {
        Invoke-Kubectl wait "job/$job" --for=condition=complete --timeout=120s -n $AppNamespace | Out-Null
    } finally {
        Invoke-Kubectl delete job $job -n $AppNamespace | Out-Null
    }
}

try {
    Test-Check 'REQ-BT-038 job from the nightly CronJob flags an overdue batch' {
        Invoke-StoreProbe overdue-seed $overdueId
        Invoke-NightlyJob
        Invoke-StoreProbe overdue-flags $overdueId 1
    }

    Test-Check 'REQ-BT-038 repeat run adds no flag' {
        Invoke-NightlyJob
        Invoke-StoreProbe overdue-flags $overdueId 1
    }
} finally {
    Test-Check 'overdue batch removed' { Invoke-StoreProbe overdue-delete $overdueId }
}
