# REQ-BT-042 and REQ-BT-043: real work on the cluster reaches the browser socket within 5 s.
. (Join-Path $DeployDir 'changes.ps1')
$FeedLimitSeconds = 5

function Get-SmokeTime {
    # Later smoke runs must not pass on what an earlier run left in the kept list.
    [DateTimeOffset]::UtcNow.AddSeconds(-2).ToString('o')
}

function Wait-Feed([string]$Pattern, [int]$Seconds, [string]$Since) {
    # Passed through a shell inside wsl, so patterns avoid spaces, quotes and braces.
    Invoke-AdminProbe ws-wait "ws://$IngressAddress/ws" $IngressOrigin $Pattern $Seconds $Since
}

function Get-WebPods {
    @((Get-KubeJson get pods -n $AppNamespace -l app.kubernetes.io/component=web).items | Where-Object { -not $_.metadata.deletionTimestamp } | ForEach-Object { $_.metadata.name })
}

Test-Check 'REQ-BT-042 a deleted pod shows in the feed, and in the next report' {
    $before = @(Get-WebPods)
    $since = Get-SmokeTime
    Invoke-Kubectl delete pod $before[0] -n $AppNamespace --wait=false | Out-Null
    Wait-Feed "pod.deleted.+$AppNamespace/$($before[0])" $FeedLimitSeconds $since
    $replacement = @(Get-WebPods | Where-Object { $_ -ne $before[0] })[0]
    Assert-True ($null -ne $replacement) 'no replacement pod'
    Wait-Feed "$replacement.+cpuLimitMilli" $FeedLimitSeconds $since
    Invoke-Kubectl rollout status deploy/web -n $AppNamespace --timeout=120s | Out-Null
}

Test-Check 'REQ-BT-042 a real scale shows in the feed' {
    $replicas = (Get-KubeJson get deploy/web -n $AppNamespace).spec.replicas
    $since = Get-SmokeTime
    try {
        Invoke-Kubectl scale deploy/web -n $AppNamespace "--replicas=$($replicas + 1)" | Out-Null
        Wait-Feed "deployment.replicas.+$AppNamespace/web" $FeedLimitSeconds $since
    } finally {
        Invoke-Kubectl scale deploy/web -n $AppNamespace "--replicas=$replicas" | Out-Null
    }
    Invoke-Kubectl rollout status deploy/web -n $AppNamespace --timeout=120s | Out-Null
}

Test-Check 'REQ-BT-042 a step Kubernetes cannot see shows in the feed' {
    $token = "smoke$(Get-Random)"
    $since = Get-SmokeTime
    Write-Change 'image import' $token $null 'test'
    Wait-Feed "image.import.+$token" $FeedLimitSeconds $since
}

Test-Check 'REQ-BT-043 an operation shows while it runs and clears after' {
    $token = "smoke$(Get-Random)"
    $operation = Start-Operation $token
    try {
        Wait-Feed "$token.+running" $FeedLimitSeconds ''
        Set-OperationStep $operation 'stepone'
        Wait-Feed "$token.+stepone.+running" $FeedLimitSeconds ''
    } finally {
        Stop-Operation $operation $true
    }
    Wait-Feed "$token.+done" $FeedLimitSeconds ''
    Wait-Feed "'^(?!.*$token).*operations'" 30 ''
}

Test-Check 'REQ-BT-054 a step list shows from the start and ticks as it goes' {
    $token = "smoke$(Get-Random)"
    $operation = Start-Operation $token $token @('stepone', 'steptwo')
    try {
        Wait-Feed "$token.+stepone.+pending.+steptwo.+pending" $FeedLimitSeconds ''
        Set-OperationStep $operation 'stepone'
        Wait-Feed "$token.+stepone.+running.+steptwo.+pending" $FeedLimitSeconds ''
        Set-OperationStep $operation 'steptwo'
        Wait-Feed "$token.+stepone.+done.+steptwo.+running" $FeedLimitSeconds ''
    } finally {
        Stop-Operation $operation $true
    }
    Wait-Feed "$token.+stepone.+done.+steptwo.+done" $FeedLimitSeconds ''
}

Test-Check 'REQ-BT-043 a failure stays until the same work runs again' {
    $token = "smoke$(Get-Random)"
    Stop-Operation (Start-Operation $token) $false
    Wait-Feed "$token.+failed" $FeedLimitSeconds ''
    Start-Sleep -Seconds 17
    Wait-Feed "$token.+failed" $FeedLimitSeconds ''
    Stop-Operation (Start-Operation $token) $true
    Wait-Feed "$token.+done" $FeedLimitSeconds ''
    Wait-Feed "'^(?!.*$token).*operations'" 30 ''
}
