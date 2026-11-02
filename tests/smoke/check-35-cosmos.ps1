$cosmosPorts = @($CosmosPort, $CosmosHealthPort, $CosmosExplorerPort)
Test-Container 'batchtracker-cosmos' @($cosmosPorts | ForEach-Object { "127.0.0.1:$_"; "${HostIp}:$_" })

Test-Check 'Cosmos data on a named volume' {
    $mount = $script:container.Mounts | Where-Object { $_.Destination -eq '/data' }
    Assert-True ($mount.Type -eq 'volume') 'data not on a volume'
}

Test-Check 'Cosmos ready on loopback' { Invoke-Probe http "http://127.0.0.1:$CosmosHealthPort/ready" }
Test-Check 'Cosmos ready on LAN address' { Invoke-Probe http "http://${HostIp}:$CosmosHealthPort/ready" }
Test-Check 'Cosmos item round trip on loopback' { Invoke-Probe cosmos "127.0.0.1:$CosmosPort" }
Test-Check 'Cosmos item round trip on LAN address' { Invoke-Probe cosmos "${HostIp}:$CosmosPort" }
