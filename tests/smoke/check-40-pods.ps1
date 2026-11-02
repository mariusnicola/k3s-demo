# Throwaway probe pods, one per worker, installed through Helm and removed at the end.
$SmokeNamespace = 'batchtracker-smoke'
$Release = 'smoke-probe'
$ServiceHost = "$Release.$SmokeNamespace.svc.cluster.local"
$script:pods = @()

function Copy-Probe([string]$Pod) {
    # Piped inside WSL because Windows PowerShell re-encodes binary stdin.
    $source = (Invoke-Wsl wslpath -a ($ProbePublish -replace '\\', '/')) + '/probe'
    Invoke-Wsl bash -c "k3s kubectl -n $SmokeNamespace exec -i $Pod -- sh -c 'cat > /tmp/probe.part && chmod +x /tmp/probe.part && mv /tmp/probe.part /tmp/probe' < $source" | Out-Null
}

# Unique per run, so a metric left by an earlier run cannot pass the check.
$IngressHost = "smoke-$([guid]::NewGuid().ToString('N')).local"

try {
    Test-Check 'Helm installs the probe chart' {
        $chart = Invoke-Wsl wslpath -a ((Join-Path $PSScriptRoot 'chart') -replace '\\', '/')
        Invoke-Helm upgrade --install $Release $chart -n $SmokeNamespace --create-namespace --set "ingressHost=$IngressHost" --wait --timeout 180s | Out-Null
    }

    Test-Check 'one probe pod on each worker, none on the control plane' {
        $script:pods = @((Get-KubeJson get pods -n $SmokeNamespace -l "app=$Release").items)
        $actual = ($script:pods.spec.nodeName | Sort-Object) -join ','
        Assert-True ($actual -eq (($Workers | Sort-Object) -join ',')) "pods on $actual"
    }

    foreach ($pod in $script:pods) {
        $podName = $pod.metadata.name
        $node = $pod.spec.nodeName

        Test-Check "$node probe starts" {
            Copy-Probe $podName
            Wait-Until 20 { Invoke-PodProbe $podName http 'http://127.0.0.1:8081/' }
        }
        Test-Check "$node cluster DNS" { Invoke-PodProbe $podName dns $ServiceHost }
        Test-Check "$node reaches Kubernetes API" { Invoke-PodProbe $podName tcp kubernetes.default.svc.cluster.local 443 }
    }

    foreach ($pod in $script:pods) {
        $podName = $pod.metadata.name
        $node = $pod.spec.nodeName
        $peer = $script:pods | Where-Object { $_.metadata.name -ne $podName }

        Test-Check "$node reaches pod on $($peer.spec.nodeName)" { Invoke-PodProbe $podName http "http://$($peer.status.podIP):8081/" }
        Test-Check "$node reaches the Service" { Invoke-PodProbe $podName http "http://${ServiceHost}:8081/" }
        Test-Check "control plane reaches pod on $node" {
            Invoke-Kubectl get --raw "/api/v1/namespaces/$SmokeNamespace/pods/${podName}:8081/proxy/" | Out-Null
        }
        Test-Check "$node reaches Service Bus health" { Invoke-PodProbe $podName http "http://${HostIp}:$AdminPort/health" }
        Test-Check "$node Service Bus message round trip" { Invoke-PodProbe $podName sb "${HostIp}:$AmqpPort" }
        Test-Check "$node Cosmos item round trip" { Invoke-PodProbe $podName cosmos "${HostIp}:$CosmosPort" }
        Test-Check "$node reaches the monitor" { Invoke-PodProbe $podName http "http://${HostIp}:$MonitorPort/alive" }
    }

    $controller = 'http://ingress-nginx-controller.ingress-nginx.svc.cluster.local/'
    Test-Check 'ingress-nginx routes to the Service' {
        Wait-Until 30 { Invoke-PodProbe $script:pods[0].metadata.name http $controller $IngressHost }
    }

    Test-Check 'Prometheus records the ingress request' {
        Wait-Until 45 {
            $series = @(Get-PrometheusResult "nginx_ingress_controller_requests{host=`"$IngressHost`"}")
            Assert-True ($series.Count -gt 0) 'request not in Prometheus'
        }
    }

    Test-Check 'KEDA scales on a Prometheus query' {
        Wait-Until 90 {
            $scaled = Get-KubeJson get deployment "$Release-scaled" -n $SmokeNamespace
            Assert-True ($scaled.status.readyReplicas -eq 2) "ready $($scaled.status.readyReplicas) of 2"
        }
    }
} finally {
    Test-Check 'Helm removes the probe chart' {
        Invoke-Helm uninstall $Release -n $SmokeNamespace --wait | Out-Null
        Invoke-Kubectl delete namespace $SmokeNamespace | Out-Null
    }
}
