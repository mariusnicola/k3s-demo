Test-Container 'batchtracker-servicebus' @("127.0.0.1:$AmqpPort", "${HostIp}:$AmqpPort", "127.0.0.1:$AdminPort", "${HostIp}:$AdminPort")
Test-Container 'batchtracker-servicebus-sql' @()

Test-Check 'Service Bus health on loopback' { Invoke-Probe http "http://127.0.0.1:$AdminPort/health" }
Test-Check 'Service Bus health on LAN address' { Invoke-Probe http "http://${HostIp}:$AdminPort/health" }
Test-Check 'Service Bus message round trip on loopback' { Invoke-Probe sb "127.0.0.1:$AmqpPort" }
Test-Check 'Service Bus message round trip on LAN address' { Invoke-Probe sb "${HostIp}:$AmqpPort" }
