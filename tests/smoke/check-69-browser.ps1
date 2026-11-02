# The paths a browser uses, through the ingress, from WSL and from Windows. Windows cannot reach
# WSL services on its own LAN address, so a check from WSL alone would miss a browser failure.
$BrowserPages = @('/', '/signin', '/alerts', '/dashboard', '/design-system')
$MonitorSocket = "ws://$IngressAddress/ws"

function Get-WindowsResponse([string]$Url) {
    # Windows PowerShell throws on 4xx and 5xx; the status is what the checks read.
    try {
        return Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 15 -MaximumRedirection 0
    } catch {
        if ($_.Exception.Response) { return @{ StatusCode = [int]$_.Exception.Response.StatusCode; Headers = @{}; Content = '' } }
        throw
    }
}

function Assert-WindowsPage([string]$Path) {
    $page = Get-WindowsResponse "$IngressOrigin$Path"
    Assert-True ($page.StatusCode -eq 200) "answered $($page.StatusCode)"
    Assert-True ("$($page.Headers['Content-Type'])" -match '^text/html') "not HTML: $($page.Headers['Content-Type'])"
    $assets = @([regex]::Matches($page.Content, '(?:src|href)="([^"]+\.(?:js|css)(?:\?[^"]*)?)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    Assert-True ($assets.Count -gt 0) 'names no JS or CSS'
    foreach ($asset in $assets) {
        $file = Get-WindowsResponse ([uri]::new([uri]"$IngressOrigin$Path", $asset)).AbsoluteUri
        Assert-True ($file.StatusCode -eq 200) "$asset answered $($file.StatusCode)"
        Assert-True ("$($file.Headers['Content-Type'])" -notmatch '^text/html') "$asset answered with the HTML page"
    }
    return $assets
}

function Assert-WindowsSocket {
    $socket = New-Object Net.WebSockets.ClientWebSocket
    $socket.Options.SetRequestHeader('Origin', $IngressOrigin)
    $socket.Options.Cookies = (Get-AdaSession).Cookies
    $limit = New-Object Threading.CancellationTokenSource 30000
    try {
        $socket.ConnectAsync([uri]$MonitorSocket, $limit.Token).GetAwaiter().GetResult() | Out-Null
        $buffer = New-Object 'byte[]' (4 * 1024 * 1024)
        $length = 0
        do {
            $part = $socket.ReceiveAsync((New-Object 'ArraySegment[byte]' -ArgumentList $buffer, $length, ($buffer.Length - $length)), $limit.Token).GetAwaiter().GetResult()
            $length += $part.Count
        } until ($part.EndOfMessage)
        $report = [Text.Encoding]::UTF8.GetString($buffer, 0, $length) | ConvertFrom-Json
        Assert-True (@($report.caps).Count -gt 0) 'report has no caps'
    } finally {
        $socket.Dispose()
    }
}

foreach ($path in $BrowserPages) {
    Test-Check "Windows page $path" { Assert-WindowsPage $path | Out-Null }
    Test-Check "WSL page $path" { Invoke-Probe page $IngressOrigin $path }
}

Test-Check 'Windows ingress /ws pushes ada a report' { Assert-WindowsSocket }
Test-Check 'WSL ingress /ws pushes ada a report' { Invoke-AdminProbe ws-report $MonitorSocket $IngressOrigin }
Test-Check 'ingress /ws gives an anonymous browser operations frames only' { Invoke-Probe ws $MonitorSocket $IngressOrigin }
Test-Check 'ingress /ws gives a script with no Origin operations frames only' { Invoke-Probe ws $MonitorSocket }

Test-Check 'ingress /ws stays open past the default read timeout' { Invoke-Probe ws-hold $MonitorSocket $IngressOrigin }

Test-Check 'ingress /ws refuses another origin' {
    $refused = $false
    try { Invoke-Probe ws $MonitorSocket 'http://example.invalid' } catch { $refused = "$_" -match '403' }
    Assert-True $refused 'another origin got a socket'
}

Set-Content $FailureStamp (Get-Date).ToString('o')
foreach ($path in '/line', '/ws/hops', '/live/logs') {
    Test-Check "Windows ingress $path answers 401" {
        $code = (Get-WindowsResponse "$IngressOrigin$path").StatusCode
        Assert-True ($code -eq 401) "answered $code"
    }
    Test-Check "WSL ingress $path answers 401" { Invoke-Probe unauthorized $IngressOrigin $path }
}

Test-Check 'ingress exposes nothing else of the monitor' {
    foreach ($path in '/health', '/alive', '/ws/health', '/ws/alive', '/ws/../health') {
        $body = (Invoke-Wsl curl -s --path-as-is -m 10 "$IngressOrigin$path") -join "`n"
        Assert-True ($body -notmatch '"checks"|"healthy"') "$path answered with the monitor report"
        Assert-True ($body -match '<!doctype html>') "$path did not fall through to web"
    }
}

Test-Check 'ingress exposes no internal Live route' {
    foreach ($path in '/hops', '/changes', '/logs') {
        $code = (Invoke-Wsl curl -s -m 10 -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' "$IngressOrigin$path") -join ''
        Assert-True ($code -match '^(404|405)$') "$path answered $code"
    }
}

Test-Check 'ingress exposes nothing else of the line' {
    # Root /healthz is ingress-nginx's own location. Under /line, Line sees the full path and knows only /line itself,
    # and its policy denies an unknown path, so anything but 401 or 404 means more of it is reachable.
    foreach ($path in '/line/healthz', '/line/line') {
        $code = (Invoke-Wsl curl -s --path-as-is -m 10 -o /dev/null -w '%{http_code}' "$IngressOrigin$path") -join ''
        Assert-True ($code -match '^(401|404)$') "$path answered $code"
    }
}

Test-Check 'Windows web holds no host address or port' {
    $scripts = @(Assert-WindowsPage '/')
    foreach ($asset in $scripts) {
        $content = (Get-WindowsResponse ([uri]::new([uri]"$IngressOrigin/", $asset)).AbsoluteUri).Content
        if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
        Assert-True ($content -notmatch '<HOST_IP>|:5320|:5321') "$asset holds a host address or port"
    }
}

Test-Check 'monitor reports every browser path healthy' {
    Wait-Until 45 {
        $report = (Invoke-Wsl curl -s "http://127.0.0.1:$MonitorPort/health") -join "`n" | ConvertFrom-Json
        $expected = @($BrowserPages | ForEach-Object { "page $_" }) + @('ingress /ws', 'ingress /line', 'ingress /ws/hops')
        foreach ($name in $expected) {
            $check = $report.checks | Where-Object { $_.name -eq $name }
            Assert-True ($null -ne $check) "$name not in report"
            Assert-True $check.healthy "${name}: $($check.error)"
        }
    }
}
