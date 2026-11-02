# The Performance card reads these series. A pod dropped from the scrape shows only as Unknown on the page.
$CounterNames = 'bt_events_created_total', 'bt_events_replayed_total', 'bt_batches_created_total', 'bt_dose1_total', 'bt_dose2_total',
    'bt_inspections_passed_total', 'bt_inspections_failed_total', 'bt_batches_shipped_total', 'bt_batches_closed_total', 'bt_batches_rejected_total',
    'bt_events_passed_total', 'bt_events_dead_lettered_total'

Test-Check 'F14 Prometheus scrapes every pod that serves /metrics' {
    $pods = @((Get-KubeJson get pods -n $AppNamespace --field-selector=status.phase=Running).items |
        Where-Object { -not $_.metadata.deletionTimestamp -and $_.metadata.name -match '^(api|worker|dispatcher)' })
    Assert-True ($pods.Count -gt 0) 'no pod that serves /metrics'
    Wait-Until 60 {
        $up = @(Get-PrometheusResult "up{namespace=`"$AppNamespace`"} == 1" | ForEach-Object { $_.metric.pod })
        $missing = @($pods.metadata.name | Where-Object { $up -notcontains $_ })
        Assert-True ($missing.Count -eq 0) "not scraped: $($missing -join ',')"
    }
}

Test-Check 'F14 Prometheus holds every bt_ counter' {
    Wait-Until 60 {
        $missing = @($CounterNames | Where-Object { @(Get-PrometheusResult $_).Count -eq 0 })
        Assert-True ($missing.Count -eq 0) "no series: $($missing -join ',')"
    }
}

Test-Check 'F14 no bt_ counter reaches a client through the ingress' {
    $page = (Invoke-Wsl curl -s -m 15 "$IngressOrigin/metrics") -join "`n"
    Assert-True ($page -notmatch 'bt_\w+_total') '/metrics served counters through the ingress'
    Wait-Until 30 {
        $code = (Invoke-Wsl curl -s -m 5 -o /dev/null -w '%{http_code}' -H 'Host: worker.batch-tracker.local' "$IngressOrigin/metrics") -join ''
        Assert-True ($code -match '^(403|404)$') "/metrics on the Worker route answered $code"
    }
}
