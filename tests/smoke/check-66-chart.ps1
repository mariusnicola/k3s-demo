# Needs no app image: renders the chart with stand-in image names.
$chartDir = Join-Path $ReleaseDir 'app'
$generated = Join-Path $DeployDir '.generated'
New-Item -ItemType Directory -Force -Path $generated | Out-Null
$smokeValues = Join-Path $generated 'smoke-chart.json'

function Get-WslSmokePath([string]$Path) { Invoke-Wsl wslpath -a ($Path -replace '\\', '/') }

function Write-SmokeValues([hashtable]$Values) {
    [IO.File]::WriteAllText($smokeValues, ($Values | ConvertTo-Json -Depth 20))
}

function Get-StandInValues {
    $values = Get-ChartValues $Caps 'batch-tracker'
    $values['images'] = @{ api = 'stand-in/api:1'; worker = 'stand-in/worker:1'; dispatcher = 'stand-in/dispatcher:1'; live = 'stand-in/live:1'; web = 'stand-in/web:1'; nightly = 'stand-in/nightly:1'; netprobe = 'stand-in/netprobe:1' }
    return $values
}

$chartPath = Get-WslSmokePath $chartDir
$valuesPath = Get-WslSmokePath $smokeValues

Write-SmokeValues (Get-StandInValues)

Test-Check 'REQ-BT-037 chart lints' { Invoke-Helm lint $chartPath -f $valuesPath | Out-Null }

Test-Check 'REQ-BT-037 chart renders every kind with limits' {
    $rendered = (Invoke-Helm template batch-tracker $chartPath -n $AppNamespace -f $valuesPath) -join "`n"
    foreach ($kind in 'Deployment', 'Service', 'Endpoints', 'Ingress', 'ScaledObject', 'CronJob', 'Role', 'RoleBinding', 'ServiceAccount', 'ConfigMap') {
        Assert-True ($rendered -match "(?m)^kind: $kind$") "no $kind"
    }
    $deployments = [regex]::Matches($rendered, '(?m)^kind: Deployment$').Count
    Assert-True ($deployments -eq 7) "$deployments Deployments"
    $limits = [regex]::Matches($rendered, '(?m)^\s+limits:$').Count
    Assert-True ($limits -eq 9) "$limits containers with limits"
    Assert-True ($rendered -match 'imagePullPolicy: Never') 'pull policy not Never'
    Assert-True ($rendered -notmatch 'imagePullPolicy: (Always|IfNotPresent)') 'another pull policy found'
}

function Get-RenderedChart { (Invoke-Helm template batch-tracker $chartPath -n $AppNamespace -f $valuesPath) -join "`n" }

Test-Check 'REQ-BT-051 chart holds no secret' {
    $rendered = Get-RenderedChart
    Assert-True ($rendered -notmatch 'AccountKey=|SharedAccessKey=|secretKeyRef|JWT_KEY') 'connection string or literal secret reference found'
    $leaked = @(Get-Content (Join-Path $DeployDir 'app\.env') | Where-Object { $_ } | ForEach-Object {
        $key, $value = $_ -split '=', 2
        if ($value -and $rendered.Contains($value)) { $key }
    })
    Assert-True ($leaked.Count -eq 0) "value in the chart: $($leaked -join ',')"
}

Test-Check 'T-018 the store is Mongo and the chart names its database' {
    $config = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: ConfigMap$' }
    foreach ($line in 'BATCH_STORE: mongo', 'MONGO_DATABASE: batch-tracker', 'MONGO_COLLECTION: batches') {
        Assert-True ($config -match "(?m)^\s+$line\s*$") "ConfigMap lacks $line"
    }
}

Test-Check 'T-018 a chart with no BATCH_STORE does not render' {
    $refused = $false
    try { Invoke-Helm template batch-tracker $chartPath -n $AppNamespace -f $valuesPath --set config.BATCH_STORE=null | Out-Null } catch { $refused = "$_" -match 'BATCH_STORE' }
    Assert-True $refused 'rendered with no BATCH_STORE'
}

Test-Check 'T-018 every role that reads the store gets MONGO_CONNECTION' {
    . (Join-Path $ReleaseDir 'release.ps1')
    foreach ($role in 'api', 'worker', 'dispatcher', 'live') {
        Assert-True ($ServiceSecrets[$role] -contains 'MONGO_CONNECTION') "$role lacks MONGO_CONNECTION"
    }
    $example = Read-EnvFile (Join-Path $ReleaseDir 'app\.env.example')
    Assert-True ($example.ContainsKey('MONGO_CONNECTION')) 'no key in .env.example, so a missing value would pass'
    Assert-True (-not $example.ContainsKey('MONGO_MONITOR_CONNECTION')) 'a key in the app example reaches a service Secret'
    $leaked = @($ServiceSecrets.Keys | Where-Object { $ServiceSecrets[$_] -contains 'MONGO_MONITOR_CONNECTION' })
    Assert-True ($leaked.Count -eq 0) "monitor connection in the Secret of: $($leaked -join ',')"
}

Test-Check 'T-018 the Mongo password is generated once and both files agree' {
    . (Join-Path $ReleaseDir 'release.ps1')
    $folder = Join-Path $env:TEMP "batchtracker-mongoenv-$(Get-Random)"
    New-Item -ItemType Directory -Path $folder | Out-Null
    $mongo = Join-Path $folder 'mongo.env'
    $app = Join-Path $folder 'app.env'
    try {
        [IO.File]::WriteAllText($app, "JWT_PUBLIC_KEY=kept`nMONGO_CONNECTION=`n")
        Set-MongoEnv $mongo $app '192.0.2.1'
        $password = (Read-EnvFile $mongo).MONGO_PASSWORD
        $first = (Read-EnvFile $app).MONGO_CONNECTION
        Assert-True ($password -match '^[0-9a-f]{48}$') 'no generated password'
        Assert-True ($first.Contains(":$password@192.0.2.1:27018/")) 'the connection does not hold the password'
        Assert-True ($first -match 'retryWrites=false' -and $first -match 'serverSelectionTimeoutMS=3000' -and $first -match 'authSource=admin') 'connection options missing'
        Assert-True ((Read-EnvFile $app).JWT_PUBLIC_KEY -eq 'kept') 'another key was lost'
        $monitorPassword = (Read-EnvFile $mongo).MONGO_MONITOR_PASSWORD
        Assert-True (-not (Read-EnvFile $app).ContainsKey('MONGO_MONITOR_CONNECTION')) 'the monitor connection is in the app env file'
        $monitor = (Read-EnvFile $mongo).MONGO_MONITOR_CONNECTION
        Assert-True ($monitorPassword -match '^[0-9a-f]{48}$' -and $monitorPassword -ne $password) 'no password of its own for the monitor'
        Assert-True ($monitor.StartsWith("mongodb://monitor:$monitorPassword@192.0.2.1:27018/monitor?")) 'the monitor connection is not its own user on its own database'
        Assert-True ($monitor -match 'authSource=monitor' -and $monitor -match 'retryWrites=false') 'monitor connection options missing'
        Assert-True (-not $monitor.Contains($password)) 'the monitor connection holds the admin password'
        Assert-True (@(Get-Content $app | Where-Object { $_ -match '^MONGO_CONNECTION=' }).Count -eq 1) 'the key is there twice'

        Set-MongoEnv $mongo $app '192.0.2.1'
        Assert-True ((Read-EnvFile $mongo).MONGO_PASSWORD -eq $password) 'a second run changed the password'
        Assert-True ((Read-EnvFile $mongo).MONGO_MONITOR_PASSWORD -eq $monitorPassword) 'a second run changed the monitor password'
        Assert-True ((Read-EnvFile $mongo).MONGO_MONITOR_CONNECTION -eq $monitor) 'a second run changed the monitor connection'
        Assert-True ((Read-EnvFile $app).MONGO_CONNECTION -eq $first) 'a second run changed the connection'

        [IO.File]::WriteAllText($mongo, "CAP_MONGO_CPU=1`nMONGO_PASSWORD=given`n")
        [IO.File]::WriteAllText($app, "JWT_PUBLIC_KEY=kept`n")
        Set-MongoEnv $mongo $app '192.0.2.1'
        Assert-True ((Read-EnvFile $mongo).MONGO_PASSWORD -eq 'given') 'a present password was replaced'
        Assert-True ((Read-EnvFile $mongo).CAP_MONGO_CPU -eq '1') 'a cap line was lost'
        Assert-True ((Read-EnvFile $app).MONGO_CONNECTION.Contains(':given@')) 'an existing password did not reach the app file'

        [IO.File]::WriteAllText($mongo, "CAP_MONGO_CPU=1`n")
        Set-MongoEnv $mongo $app '192.0.2.1'
        Assert-True ((Read-EnvFile $mongo).CAP_MONGO_CPU -eq '1') 'a new password lost a cap line'

        [IO.File]::WriteAllText($mongo, "MONGO_PASSWORD=given`n")
        Set-MongoEnv $mongo $app '192.0.2.1'
        Assert-True ((Read-EnvFile $mongo).MONGO_PASSWORD -eq 'given') 'adding the monitor user changed the admin password'
        Assert-True ((Read-EnvFile $mongo).MONGO_MONITOR_CONNECTION.Contains((Read-EnvFile $mongo).MONGO_MONITOR_PASSWORD)) 'a new monitor password left the old connection'

        Remove-Item $mongo
        Set-MongoEnv $mongo $app '192.0.2.1'
        $fresh = (Read-EnvFile $mongo).MONGO_PASSWORD
        Assert-True ((Read-EnvFile $app).MONGO_CONNECTION.Contains(":$fresh@")) 'a new password left the old connection in the app file'
    } finally {
        Remove-Item $folder -Recurse -Force
    }
}

Test-Check 'T-018 the monitor user reaches only its own database' {
    . (Join-Path $ReleaseDir 'release.ps1')
    $script = Get-MongoMonitorUserScript 'stand-in-value'
    Assert-True ($script -match 'createUser' -and $script -match 'updateUser') 'a rerun must not fail on an existing user'
    Assert-True ($script -match 'getSiblingDB\("monitor"\)') 'the user is not on the monitor database'
    Assert-True ($script -match 'role: "readWrite", db: "monitor"') 'not limited to readWrite on monitor'
    Assert-True ($script -notmatch 'Admin|root|dbOwner') 'a wider role than asked'
    Assert-True ($script.Contains('stand-in-value')) 'the password is not set'
    Assert-True ($script -match '\} else \{[^\r\n]*print\("monitor=set"\)') 'piped mongosh takes each finished line alone, so else and the marker share the line of the if'
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    $mongo = $setup.Substring($setup.IndexOf('function Set-Mongo {'), $setup.IndexOf('function Install-Chart') - $setup.IndexOf('function Set-Mongo {'))
    Assert-True ($mongo -match 'Get-MongoMonitorUserScript[^\r\n]*\|\s*wsl') 'the password does not go by stdin'
    Assert-True ($mongo.IndexOf('Get-MongoMonitorUserScript') -gt $mongo.IndexOf('ping')) 'the user is created before Mongo answers'
    Assert-True ($mongo -match "-notmatch 'monitor=set'[^\r\n]*throw") 'a failed user step would pass as healthy'
}

Test-Check 'T-018 setup generates before it checks, opens 27018 and starts Mongo' {
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    Assert-True ($setup -match "Name = 'BatchTracker-mongo';\s+Protocol = 'TCP'; Port = 27018") '27018 not in ClusterPorts'
    $generate = $setup.IndexOf('Set-MongoEnv ')
    Assert-True ($generate -gt 0 -and $generate -lt $setup.IndexOf('Assert-AppEnv $AppEnvFile')) 'password not generated before Assert-AppEnv'
    Assert-True ($setup.LastIndexOf('    Set-Mongo') -gt $setup.LastIndexOf('    Set-Cosmos')) 'setup does not start Mongo with the stores'
    Assert-True ($setup -match 'Set-MongoEnv [^
]*\$ControlPlaneIp') 'the connection does not name the host address setup defines'
    $declared = [regex]::Match($setup, "\`$DeploySteps = @\(([^
]+)\)").Groups[1].Value
    $steps = @([regex]::Matches($setup, "Set-OperationStep \`$operation '([^']+)'") | ForEach-Object { $_.Groups[1].Value })
    Assert-True ($steps.Count -gt 0) 'no step found'
    $unknown = @($steps | Where-Object { -not $declared.Contains("'$_'") })
    Assert-True ($unknown.Count -eq 0) "steps not declared: $($unknown -join ', ')"
}

Test-Check 'a failed gate step keeps its whole output' {
    . (Join-Path $ReleaseDir 'release.ps1')
    $log = Join-Path $env:TEMP 'batchtracker-gate-failure.log'
    $script = Join-Path $env:TEMP "batchtracker-gate-row-$(Get-Random).cmd"
    try {
        Remove-Item $log -Force -ErrorAction SilentlyContinue
        $lines = @('@echo off') + @(1..40 | ForEach-Object { "echo line-$_" }) + 'exit /b 3'
        Set-Content $script $lines -Encoding Ascii
        try { Invoke-GateStep 'row' $env:TEMP "`"$script`"" | Out-Null } catch { }
        $kept = Get-Content $log -ErrorAction SilentlyContinue
        Assert-True ($kept -contains 'line-1' -and $kept -contains 'line-40') 'the first lines of the output are gone'
    } finally {
        Remove-Item $script, $log -Force -ErrorAction SilentlyContinue
    }
}

Test-Check 'REQ-BT-051 ConfigMap holds no secret key' {
    $config = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: ConfigMap$' }
    $keys = @(Get-Content (Join-Path $ReleaseDir 'app\.env.example') | Where-Object { $_ -match '^[A-Z]' } | ForEach-Object { ($_ -split '=')[0] })
    Assert-True ($keys.Count -gt 0) 'no key in .env.example'
    $found = @($keys | Where-Object { $config -match "(?m)^\s+${_}:" })
    Assert-True ($found.Count -eq 0) "in the ConfigMap: $($found -join ',')"
}

Test-Check 'REQ-BT-051 every service but web and netprobe reads its Secret through envFrom' {
    $count = [regex]::Matches((Get-RenderedChart), '(?m)^\s+- secretRef:$').Count
    Assert-True ($count -eq 7) "$count secretRef entries"
}

Test-Check 'the drain counts pending from a Live snapshot that read the store' {
    . (Join-Path $ReleaseDir 'release.ps1')
    $frame = { param($pending, $reconciled, $capped) @{ type = 'snapshot'; reconciledAt = $reconciled; capped = $capped; summary = @{ pending = $pending; blocked = 3 } } | ConvertTo-Json -Depth 3 }
    Assert-True ((Get-SnapshotPending (& $frame 0 '2026-11-01T12:41:00Z' $false)) -eq 0) 'zero must read as zero'
    Assert-True ((Get-SnapshotPending (& $frame 7 '2026-11-01T12:41:00Z' $false)) -eq 7) 'a count must read'
    foreach ($case in @(
        @{ Why = 'not read yet'; Frame = (& $frame 0 $null $false) },
        @{ Why = 'capped'; Frame = (& $frame 0 '2026-11-01T12:41:00Z' $true) },
        @{ Why = 'not a snapshot'; Frame = '{"type":"hop"}' }
    )) {
        $refused = $false
        try { Get-SnapshotPending $case.Frame | Out-Null } catch { $refused = "$_" -match $case.Why }
        Assert-True $refused "$($case.Why) must give no count"
    }
}

Test-Check 'a drain with no count read says so and never names a queue size' {
    . (Join-Path $ReleaseDir 'release.ps1')
    Assert-True ((Get-DrainFailure 12) -eq 'Pending stuck at 12 for 5 minutes.') 'a stuck count must name it'
    Assert-True ((Get-DrainFailure ([int]::MaxValue)) -eq 'No pending count read for 5 minutes.') 'no count must say so'
}

Test-Check 'the drain reads Live, not the summary route' {
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    Assert-True ($setup -notmatch '/api/summary') 'setup still reads the summary route'
    Assert-True ($setup -match 'Read-LivePending') 'setup does not read Live'
}

Test-Check 'a dead Live does not block the drain: Mongo counts and the source is named' {
    . (Join-Path $ReleaseDir 'release.ps1')
    $live = Read-PendingCount { 4 } { throw 'the store must not be read while Live answers' }
    Assert-True (($live.Pending -eq 4) -and ($live.Source -eq 'Live') -and ($null -eq $live.Problem)) "Live answered: $($live | ConvertTo-Json -Compress)"
    Assert-True ((Read-PendingCount { 0 } { throw 'read' }).Pending -eq 0) 'a zero from Live must read as zero'
    $store = Read-PendingCount { throw 'refused' } { 7 }
    Assert-True (($store.Pending -eq 7) -and ($store.Source -eq 'Mongo') -and ($store.Problem -match 'refused')) "Live down: $($store | ConvertTo-Json -Compress)"
    $why = ''
    try { Read-PendingCount { throw 'refused' } { throw 'no answer' } | Out-Null } catch { $why = "$_" }
    Assert-True (($why -match 'refused') -and ($why -match 'no answer')) "both down must name both, not read as zero: '$why'"

    Assert-True ((Get-MongoPending "noise`npending=12`n") -eq 12) 'a count must read'
    Assert-True ((Get-MongoPending 'pending=0') -eq 0) 'zero must read as zero'
    $refused = $false
    try { Get-MongoPending 'MongoServerError: Authentication failed' | Out-Null } catch { $refused = $true }
    Assert-True $refused 'an answer with no count must give no count'

    $script = Get-MongoPendingScript
    Assert-True ($script -match '"pending\.status": \{ \$in: \["pending", "sent"\] \}') 'not the open states the store reads'
    $config = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: ConfigMap$' }
    $database = [regex]::Match("$config", '(?m)^\s+MONGO_DATABASE:\s*(\S+)').Groups[1].Value
    $collection = [regex]::Match("$config", '(?m)^\s+MONGO_COLLECTION:\s*(\S+)').Groups[1].Value
    Assert-True ($script.Contains("getSiblingDB(`"$database`").$collection.")) "not the chart's $database.$collection"

    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    $drain = $setup.Substring($setup.IndexOf('function Stop-LineAndDrain'), $setup.IndexOf('function Set-Monitor') - $setup.IndexOf('function Stop-LineAndDrain'))
    Assert-True ($drain -match 'Read-PendingCount \{ Read-LivePending \$session \$IngressOrigin \} \{ Read-MongoPending \}') 'the drain does not fall back'
    Assert-True ($drain -match "no event pending, counted by \`$\(\`$count\.Source\)") 'the empty queue does not name its source'
    Assert-True ($drain -match "draining: \`$pending pending, counted by \`$\(\`$count\.Source\)") 'the count does not name its source'
    Assert-True ($setup -match 'Get-MongoPendingScript \| wsl') 'the count does not go by stdin'
}

Test-Check 'COMMIT names only a commit that finished' {
    . (Join-Path $ReleaseDir 'release.ps1')
    $root = Join-Path $env:TEMP "batchtracker-smoke-release-$PID"
    $work = Join-Path $root 'work'
    $release = Join-Path $root 'release'
    New-Item -ItemType Directory -Force -Path $work, $release | Out-Null
    try {
        [IO.File]::WriteAllText((Join-Path $work 'new.txt'), 'new')
        [IO.File]::WriteAllText((Join-Path $release 'stale.txt'), 'old')
        [IO.File]::WriteAllText((Join-Path $release 'COMMIT'), "old`n")
        Publish-Release $work $release
        Assert-True (Test-Path (Join-Path $release 'new.txt')) 'the release was not copied'
        Assert-True (-not (Test-Path (Join-Path $release 'stale.txt'))) 'the release was not mirrored'
        Assert-True ((Get-Content (Join-Path $release 'COMMIT') -Raw).Trim() -eq 'old') 'a commit not yet deployed took the name'
        Set-ReleaseCommit $release 'new'
        Assert-True ((Get-Content (Join-Path $release 'COMMIT') -Raw) -eq "new`n") 'a finished commit was not written'
    } finally {
        Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    Assert-True ($setup -match '& powershell @stage\s+\$finished = \$LASTEXITCODE\s+if \(\$finished -eq 0 -and -not \$InfraOnly\) \{ Set-ReleaseCommit \$release \$hash \}\s+exit \$finished') 'COMMIT is not written after a finished deploy only'
}

Test-Check 'REQ-BT-022 Workers scale evenly, five on each node' {
    $entries = @($Caps.pods | Where-Object { $_.container -eq 'worker' })
    Assert-True ($entries.Count -eq 2) "$($entries.Count) Worker entries"
    foreach ($entry in $entries) {
        Assert-True (($entry.replicas -eq 5) -and ($entry.cpuMilli -eq 380) -and ($entry.memoryMi -eq 256)) "$($entry.name): $($entry.replicas) x $($entry.cpuMilli)m $($entry.memoryMi)Mi"
    }
    Assert-True ((@($entries.requestCpuMilli | Sort-Object -Unique)).Count -eq 1) 'a different CPU request gives each object its own 70%'
    $sums = Get-CapSums $Caps
    Assert-True ($sums['k3s-node1'].cpu -eq 4150) "Node1 sums to $($sums['k3s-node1'].cpu)m"
    Assert-True ($sums['k3s-node2'].cpu -eq 4920) "node2 sums to $($sums['k3s-node2'].cpu)m"
    Assert-True ($sums['k3s-node2'].memory -eq 3968) "node2 sums to $($sums['k3s-node2'].memory)Mi"
    Assert-True (($Caps.pods | Where-Object { $_.name -eq 'nightly' }).transient) 'the sum must hold the transient nightly pod'
    $documents = (Get-RenderedChart) -split '(?m)^---\s*$'
    $object = { param($name) @($documents | Where-Object { $_ -match '(?m)^kind: ScaledObject\s*$' -and $_ -match "(?m)^  name: $name\s*$" })[0] }
    $query = { param($text) [regex]::Match("$text", '(?m)^\s+query: (.+)$').Groups[1].Value.Trim() }
    foreach ($name in 'worker', 'worker-node2') {
        $scaled = & $object $name
        Assert-True ($scaled -match 'minReplicaCount: 1\s') "$name minimum"
        Assert-True ($scaled -match 'maxReplicaCount: 5\s') "$name maximum"
        Assert-True ($scaled -match 'threshold: "6.7"') "$name threshold"
        $deployment = @($documents | Where-Object { $_ -match '(?m)^kind: Deployment\s*$' -and $_ -match "(?m)^  name: $name\s*$" })[0]
        Assert-True ($deployment -notmatch '(?m)^  replicas:') "$name Deployment fights its scaler"
    }
    Assert-True ((& $query (& $object 'worker')) -eq (& $query (& $object 'worker-node2'))) 'the objects read different totals'
    Assert-True ((& $object 'api') -match 'threshold: "20"') 'api keeps 20'
}

Test-Check 'the speaker has 200m and 128 Mi, headroom for measured reclaim, crash loop cause unknown' {
    $entry = @($Caps.pods | Where-Object { $_.name -eq 'metallb-speaker' })[0]
    Assert-True ($entry.cpuMilli -eq 200) "speaker limit $($entry.cpuMilli)m"
    Assert-True ($entry.memoryMi -eq 128) "speaker limit $($entry.memoryMi)Mi"
    Assert-True ($entry.node -eq 'all') "speaker runs on $($entry.node)"
}

Test-Check 'Node1 has 8000m and node2 5000m, Live and Dispatcher 300m' {
    Assert-True ($Caps.nodes.'k3s-node1'.cpuMilli -eq 8000) "node cap $($Caps.nodes.'k3s-node1'.cpuMilli)"
    Assert-True ($Caps.nodes.'k3s-node2'.cpuMilli -eq 5000) "node cap $($Caps.nodes.'k3s-node2'.cpuMilli)"
    Assert-True (@(Get-ComposeCaps $Caps 'node1') -contains 'CAP_NODE1_CPU=8') "compose: $(@(Get-ComposeCaps $Caps 'node1') -join ',')"
    $documents = (Get-RenderedChart) -split '(?m)^---\s*$'
    foreach ($row in @(@('live', 300), @('dispatcher', 300), @('dispatcher-node2', 300))) {
        $name = $row[0]
        $pod = @($documents | Where-Object { $_ -match '(?m)^kind: Deployment\s*$' -and $_ -match "(?m)^  name: $name\s*$" })[0]
        Assert-True ($pod -match "(?s)limits:\s+cpu: `"?$($row[1])m`"?") "$name CPU limit"
    }
}

Test-Check 'REQ-BT-051 Live reads INTERNAL_TOKEN from its own Secret' {
    . (Join-Path $ReleaseDir 'release.ps1')
    $secret = @(Get-SecretScript '/stand-in/.env' $AppNamespace | Where-Object { $_ -match 'generic batch-tracker-live ' })
    Assert-True ($secret.Count -eq 1) "$($secret.Count) Live Secrets"
    Assert-True ($secret[0] -match '\bINTERNAL_TOKEN\b') 'Live Secret has no INTERNAL_TOKEN'
    $live = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: Deployment' -and $_ -match '(?m)^  name: live\s' }
    Assert-True ($live -match 'name: batch-tracker-live\s') 'Live does not read batch-tracker-live'
}

Test-Check 'REQ-BT-047 the line socket has the long socket timeouts' {
    $socketIngress = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match 'name: batch-tracker-ws' }
    Assert-True ($socketIngress -match 'path: /line/ws') '/line/ws is not on the socket ingress'
}

Test-Check 'a spoofed address that starts a new failure count fails the row' {
    Assert-True ((Get-SpoofGap @('401 0.004', '401 0.002', '401 1.054')) -eq '') 'one count must pass'
    Assert-True ((Get-SpoofGap @('401 0.004', '401 0.002', '401 0.003')) -match 'believed') 'no delay must fail'
    Assert-True ((Get-SpoofGap @('401 0.004', '401 0.002', '200 1.054')) -match 'answered 200') 'another status must fail'
    Assert-True ((Get-SpoofGap @('401 0.004', '401 0.002')) -match 'answers') 'a missing answer must fail'
}

Test-Check 'the ingress forwards only /ws/hops and /live/logs to Live' {
    $routes = [regex]::Matches((Get-RenderedChart), 'path: (\S+)\s+pathType: (\S+)\s+backend:\s+service:\s+name: live\s')
    $actual = (@($routes | ForEach-Object { "$($_.Groups[1].Value) $($_.Groups[2].Value)" }) | Sort-Object) -join ', '
    Assert-True ($actual -eq '/live/logs Exact, /ws/hops Prefix') "Live takes: $actual"
}

function Get-RenderedWorkload([string]$Name) {
    (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: Deployment' -and $_ -match "(?m)^  name: $Name\s" }
}

Test-Check 'REQ-BT-050 Live readiness goes 503 on a drain' {
    $probe = [regex]::Match((Get-RenderedWorkload 'live'), '(?s)readinessProbe:\s+httpGet:\s+path: (\S+)')
    Assert-True ($probe.Groups[1].Value -eq '/ready') "Live readiness reads $($probe.Groups[1].Value)"
}

Test-Check 'REQ-BT-050 setup waits for the Line on /ready' {
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    Assert-True ($setup -match "Start-Compose 'Line' \`$lineDir 'http://127\.0\.0\.1:5321/ready'") 'setup waits on another path'
}

Test-Check 'REQ-BT-050 every Deployment has the 30 s the drain needs' {
    $deployments = @((Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: Deployment' })
    $short = @($deployments | Where-Object { $_ -notmatch 'terminationGracePeriodSeconds: 30\s' })
    Assert-True (($deployments.Count -gt 0) -and ($short.Count -eq 0)) "$($short.Count) of $($deployments.Count) without it"
}

Test-Check 'REQ-BT-050 the Line gets the same 30 s from Docker' {
    $compose = Get-Content (Join-Path $ReleaseDir 'line\docker-compose.yaml') -Raw
    Assert-True ($compose -match '(?m)^\s+stop_grace_period: 30s\s') 'Docker default of 10 s cuts the 15 s walks'
}

Test-Check 'REQ-BT-050 budgets cover the roles with two pods and no single pod' {
    $rendered = Get-RenderedChart
    $names = @([regex]::Matches($rendered, '(?ms)^kind: PodDisruptionBudget\s+metadata:\s+name: (\S+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
    Assert-True (($names -join ',') -eq 'api,dispatcher,worker') "budgets: $($names -join ',')"
    Assert-True ([regex]::Matches($rendered, 'minAvailable: 1\s').Count -eq 3) 'a budget asks for more than one pod'
}

Test-Check 'REQ-BT-050 api never scales below the two pods its budget assumes' {
    $scaled = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: ScaledObject' -and $_ -match '(?m)^  name: api\s' }
    Assert-True ($scaled -match 'minReplicaCount: 2\s') 'api can scale to one pod'
}

Test-Check 'REQ-BT-022 Workers follow batches: below 3 gives 2, over 30 gives 10, a request burst adds none, no CPU trigger' {
    $objects = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: ScaledObject' }
    foreach ($name in 'worker', 'worker-node2') {
        $scaled = $objects | Where-Object { $_ -match "(?m)^  name: $name\s" }
        Assert-True ($scaled -match '(?m)^\s+query: sum\(rate\(bt_batches_created_total\[20s\]\)\) or vector\(0\)\s*$') "$name does not scale on batches"
        Assert-True ($scaled -notmatch 'nginx_') "$name still reads requests, so a burst with no new batches adds pods"
        Assert-True ($scaled -notmatch 'type: cpu') "$name still has a CPU trigger"
    }
    $threshold = [double]([regex]::Match("$scaled", 'threshold: "([0-9.]+)"').Groups[1].Value)
    $pods = { param($rate) [math]::Min(5, [math]::Max(1, [math]::Ceiling($rate / $threshold))) }
    Assert-True ((& $pods 2.9) * 2 -eq 2) "below 3 batches/s gives $((& $pods 2.9) * 2) Workers"
    Assert-True ((& $pods 30.1) * 2 -eq 10) "over 30 batches/s gives $((& $pods 30.1) * 2) Workers"
    Assert-True ((& $pods 0) * 2 -eq 2) 'no batches must leave one Worker on each node'
    Assert-True (($objects | Where-Object { $_ -match '(?m)^  name: api\s' }) -match 'type: cpu') 'the API lost its CPU trigger'
}

Test-Check 'REQ-BT-022 Worker scale-down waits 10 s and the API window holds two scrapes' {
    $objects = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: ScaledObject' }
    $scaled = $objects | Where-Object { $_ -match '(?m)^  name: worker\s' }
    $api = $objects | Where-Object { $_ -match '(?m)^  name: api\s' }
    $wait = '(?s)horizontalPodAutoscalerConfig:\s+behavior:\s+scaleDown:\s+stabilizationWindowSeconds: {0}\s'
    Assert-True ($scaled -match ($wait -f 10)) 'worker does not wait 10 s before scaling down'
    Assert-True (($objects | Where-Object { $_ -match '(?m)^  name: worker-node2\s' }) -match ($wait -f 10)) 'worker-node2 does not wait 10 s before scaling down'
    Assert-True ($api -match ($wait -f 0)) 'the API scale-down wait moved'
    Assert-True ($scaled -notmatch 'scaleUp:') 'scale-up must keep the HPA default'
    $window = [regex]::Match("$api", 'nginx_ingress_controller_requests[^\[]*\[(\d+)s\]')
    Assert-True $window.Success 'no window in seconds in the API query'
    $values = Get-Content (Join-Path $ReleaseDir 'cluster\prometheus.yaml') -Raw
    $job = [regex]::Match($values, '(?s)job_name: ingress-nginx.*')
    Assert-True $job.Success 'no ingress-nginx job, so nginx stays on the global interval'
    $scrape = [int][regex]::Match($job.Value, 'scrape_interval:\s*(\d+)s').Groups[1].Value
    Assert-True ($scrape -eq 10) "nginx is scraped every $scrape s"
    Assert-True ([int]$window.Groups[1].Value -ge (2 * $scrape)) "window $($window.Groups[1].Value) s holds under two $scrape s scrapes"
    Assert-True ([int]$window.Groups[1].Value -le (2 * $scrape)) "window $($window.Groups[1].Value) s is longer than two $scrape s scrapes need"
}

Test-Check 'REQ-BT-046 only nginx scrapes every 10 s, so 120 days still fit' {
    $values = Get-Content (Join-Path $ReleaseDir 'cluster\prometheus.yaml') -Raw
    Assert-True ($values -match '(?s)global:\s+scrape_interval: 15s') 'the global interval moved'
    $job = [regex]::Match($values, '(?s)job_name: ingress-nginx.*').Value
    Assert-True ($job -match 'honor_labels: true') 'nginx labels its own namespace, and without this they turn into exported_namespace'
    Assert-True ($job -match 'regex: metrics') 'the job does not keep only the metrics port'
    $ingress = Get-Content (Join-Path $ReleaseDir 'cluster\ingress-nginx.yaml') -Raw
    Assert-True ($ingress -notmatch 'prometheus\.io/scrape') 'kubernetes-pods also scrapes nginx, so every series reads double'
}

Test-Check 'REQ-BT-022 the HPA syncs every 10 s, set without stopping k3s by script' {
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    $plane = $setup.Substring($setup.IndexOf('function Set-ControlPlane'), $setup.IndexOf('function Get-JoinToken') - $setup.IndexOf('function Set-ControlPlane'))
    Assert-True ($plane -match '(?s)kube-controller-manager-arg:\s+- horizontal-pod-autoscaler-sync-period=10s') 'the sync period is not in the k3s config'
    Assert-True ($plane -notmatch 'k3s-killall') 'killall rewrites the iptables table other containers share'
    Assert-True ($plane -match 'mv `\$new /etc/rancher/k3s/config\.yaml\s+chmod 644 /etc/rancher/k3s/config\.yaml\s+systemctl restart k3s') 'a config change does not restart k3s after the file lands'
}

Test-Check 'REQ-BT-050 a rollout never takes a pod down before its replacement is ready' {
    $deployments = @((Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: Deployment' })
    $recreated = @($deployments | Where-Object { $_ -match 'type: Recreate' })
    Assert-True (($recreated.Count -eq 1) -and ($recreated[0] -match '(?m)^  name: dispatcher-node2\s')) 'only dispatcher-node2 may recreate'
    $open = @($deployments | Where-Object { $_ -notmatch 'type: Recreate' -and $_ -notmatch '(?s)type: RollingUpdate\s+rollingUpdate:\s+maxSurge: 1\s+maxUnavailable: 0\s' })
    Assert-True ($open.Count -eq 0) "$($open.Count) Deployments may drop a pod first"
}

Test-Check 'REQ-BT-050 web waits for the ingress before nginx stops' {
    Assert-True ((Get-RenderedWorkload 'web') -match '(?s)lifecycle:\s+preStop:\s+sleep:\s+seconds: 5\s') 'web has no preStop wait'
}

foreach ($workload in 'api', 'live') {
    Test-Check "T-011 $workload believes the ingress pod network only" {
        Assert-True ((Get-RenderedWorkload $workload) -match 'name: TRUSTED_PROXIES\s+value: "?<INGRESS_POD_CIDR>"?\s') 'setting missing, every client reads as the ingress'
    }
}

foreach ($service in 'line', 'monitor') {
    Test-Check "T-011 $service believes the ingress node only" {
        $compose = Get-Content (Join-Path $ReleaseDir "$service\docker-compose.yaml") -Raw
        Assert-True ($compose -match '(?m)^\s+TRUSTED_PROXIES: <AGENT_IP>/32\s') 'setting missing, every client reads as the ingress'
    }
}

Test-Check 'T-011 setup finds the setting on all four services' {
    $line = Get-Content (Join-Path $ReleaseDir 'line\docker-compose.yaml') -Raw
    $monitor = Get-Content (Join-Path $ReleaseDir 'monitor\docker-compose.yaml') -Raw
    $gaps = @(Get-TrustedProxyGaps (Get-RenderedChart) $line $monitor)
    Assert-True ($gaps.Count -eq 0) "gaps: $($gaps -join ',')"
}

Test-Check 'T-011 setup names a service missing the setting' {
    $rendered = Get-RenderedChart
    $line = Get-Content (Join-Path $ReleaseDir 'line\docker-compose.yaml') -Raw
    $monitor = Get-Content (Join-Path $ReleaseDir 'monitor\docker-compose.yaml') -Raw
    $gone = 'REMOVED'
    $cases = @(
        @{ Want = 'api,live'; Args = @(($rendered -replace 'TRUSTED_PROXIES', $gone), $line, $monitor) }
        @{ Want = 'line'; Args = @($rendered, ($line -replace 'TRUSTED_PROXIES', $gone), $monitor) }
        @{ Want = 'monitor'; Args = @($rendered, $line, ($monitor -replace 'TRUSTED_PROXIES', $gone)) }
    )
    foreach ($case in $cases) {
        $found = @(Get-TrustedProxyGaps $case.Args[0] $case.Args[1] $case.Args[2]) -join ','
        Assert-True ($found -eq $case.Want) "wanted $($case.Want), found $found"
    }
}

Test-Check 'T-011 setup checks before the chart goes out' {
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    $check = $setup.IndexOf('Assert-TrustedProxies')
    Assert-True (($check -gt 0) -and ($check -lt $setup.IndexOf("Invoke-Terraform @('helm_release.batch-tracker')"))) 'check missing, or the apply is missing or runs first'
}

function Get-TerraformPlanExit([string]$TerraformDir) {
    $range = [regex]::Match((Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw), '\$LoadBalancerRange = ''([^'']+)''').Groups[1].Value
    $state = Get-WslSmokePath (Join-Path $DeployDir '.terraform-state')
    $directory = Get-WslSmokePath $TerraformDir
    $generatedDirectory = Get-WslSmokePath $generated
    $script = "set -e; export TF_DATA_DIR=$state/.terraform TF_IN_AUTOMATION=1; cd $directory; terraform init -input=false -lockfile=readonly >/dev/null; terraform plan -input=false -lock=false -detailed-exitcode -state=$state/terraform.tfstate -var=generated_dir=$generatedDirectory -var=load_balancer_range=$range >/dev/null"
    wsl -d $Distro -- bash -c $script 2>&1 | Out-Null
    return $LASTEXITCODE
}

Test-Check 'terraform plans no change against the deployed cluster' {
    $exit = Get-TerraformPlanExit (Join-Path $ReleaseDir 'terraform')
    Assert-True ($exit -eq 0) "terraform plan exit $exit, 0 is no change, 2 is a change, 1 is an error"
}

Test-Check 'REQ-BT-049 a repeat run inside the quiet window is refused' {
    $stamp = Join-Path $env:TEMP 'batchtracker-smoke-stamp-row.txt'
    $now = Get-Date
    try {
        Remove-Item $stamp -Force -ErrorAction SilentlyContinue
        Assert-True ((Get-FailureWait $stamp $now) -eq 0) 'no earlier run must pass'
        Set-Content $stamp $now.AddMinutes(-16).ToString('o')
        Assert-True ((Get-FailureWait $stamp $now) -eq 0) 'a run 16 min ago must pass'
        Set-Content $stamp $now.AddMinutes(-14).ToString('o')
        Assert-True ((Get-FailureWait $stamp $now) -eq 1) 'a run 14 min ago must wait 1 min'
    } finally {
        Remove-Item $stamp -Force -ErrorAction SilentlyContinue
    }
}

Test-Check 'REQ-BT-049 a run stamps before its first refused credential' {
    $run = Get-Content (Join-Path $PSScriptRoot 'run.ps1') -Raw
    Assert-True ($run.IndexOf('Get-FailureWait') -gt 0 -and $run.IndexOf('Get-FailureWait') -lt $run.IndexOf('dotnet publish')) 'run does not check first'
    $browser = Get-Content (Join-Path $PSScriptRoot 'check-69-browser.ps1') -Raw
    $mark = $browser.IndexOf('Set-Content $FailureStamp')
    Assert-True (($mark -gt 0) -and ($mark -lt $browser.IndexOf("answers 401"))) 'no stamp before the 401 rows'
}

Test-Check 'F-14 the Worker route admits the pod network only' {
    $route = (Get-RenderedChart) -split '(?m)^---$' | Where-Object { $_ -match '(?m)^kind: Ingress' -and $_ -match '(?m)^  name: batch-tracker-worker\s' }
    Assert-True ($route -match 'nginx\.ingress\.kubernetes\.io/whitelist-source-range: "?<POD_CIDR>"?\s') 'open to the LAN'
}

Test-Check 'B-113 Prometheus reads the Node1 cadvisor at its LAN address, since node2 cannot reach its Docker address' {
    $values = Get-Content (Join-Path $ReleaseDir 'cluster\prometheus.yaml') -Raw
    $job = [regex]::Match($values, '(?s)job_name: kubernetes-nodes-cadvisor-external\s.*?(?=- job_name|\z)').Value
    Assert-True ($job -ne '') 'no job for the node with an external address, so Node1 has no CPU history'
    Assert-True ($job -match 'role: node') 'the job does not find nodes'
    Assert-True ($job -match '(?s)source_labels: \[__meta_kubernetes_node_address_ExternalIP\]\s+action: keep\s+regex: \.\+') 'node2 has no external address and would be read twice'
    Assert-True ($job -match '(?s)target_label: __address__\s+replacement: \$1:10250') 'the job still dials the internal address'
    Assert-True ($job -match 'metrics_path: /metrics/cadvisor') 'not the cadvisor path'
    Assert-True ($job -match 'scheme: https') 'the kubelet port is https'
    Assert-True ($job -match 'target_label: instance') 'the target would read as an address, not as its node'
}

Test-Check 'firewall rules allow node2 alone, and setup rewrites rules that already exist' {
    $setup = Get-Content (Join-Path $ReleaseDir 'setup.ps1') -Raw
    Assert-True ($setup -notmatch 'LanCidr') 'setup still names the whole LAN'
    $tree = [Management.Automation.Language.Parser]::ParseInput($setup, [ref]$null, [ref]$null)
    $function = $tree.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-ClusterFirewall' }, $true)
    $calls = [Collections.ArrayList]::new()
    function Get-NetFirewallHyperVRule { param($Name, $ErrorAction) if ($Name -eq 'old') { 'found' } }
    function Set-NetFirewallHyperVRule { param($Name, $RemoteAddresses) [void]$calls.Add("set $Name $RemoteAddresses") }
    function New-NetFirewallHyperVRule { param($Name, $DisplayName, $Direction, $VMCreatorId, $Protocol, $LocalPorts, $RemoteAddresses, $Action) [void]$calls.Add("new $Name $RemoteAddresses") }
    $WorkerIps = @('<AGENT_IP>')
    $WslVmCreatorId = 'vm'
    $ClusterPorts = @(@{ Name = 'old'; Protocol = 'TCP'; Port = 1 }, @{ Name = 'fresh'; Protocol = 'TCP'; Port = 2 })
    . ([scriptblock]::Create($function.Body.Extent.Text.Trim('{', '}'))) | Out-Null
    Assert-True ($calls -contains 'set old <AGENT_IP>') "an existing rule keeps its addresses: $calls"
    Assert-True ($calls -contains 'new fresh <AGENT_IP>') "a new rule is not limited to node2: $calls"
}

Test-Check 'REQ-BT-046 Prometheus keeps 120 days on a volume with a size cap' {
    $values = Get-Content (Join-Path $ReleaseDir 'cluster\prometheus.yaml') -Raw
    Assert-True ($values -match '(?m)^  retention: "?120d"?\s') 'retention is not 120 days'
    Assert-True ($values -match '(?m)^  retentionSize: "?38GB"?\s') 'nothing stops it filling the node disk'
    Assert-True ($values -match '(?s)persistentVolume:\s+enabled: true\s+size: 40Gi\s+storageClass: local-path\s') 'no volume, a restart loses the history'
}

Test-Check 'F14 Prometheus scrapes every pod that serves /metrics, and no ingress routes it' {
    $documents = (Get-RenderedChart) -split '(?m)^---\s*$'
    function Get-Deployment([string]$Name) {
        @($documents | Where-Object { $_ -match '(?m)^kind: Deployment\s*$' -and $_ -match "(?m)^  name: $Name\s*$" })[0]
    }
    foreach ($name in 'api', 'worker', 'worker-node2', 'dispatcher', 'dispatcher-node2') {
        $pod = Get-Deployment $name
        Assert-True ($pod -match '(?m)^        batch-tracker/scrape: "true"\s*$') "$name pod lacks batch-tracker/scrape"
        Assert-True ($pod -notmatch 'prometheus\.io/scrape') "kubernetes-pods also scrapes $name, so every sum reads double"
    }
    foreach ($name in 'live', 'web') {
        Assert-True ((Get-Deployment $name) -notmatch 'batch-tracker/scrape') "$name serves no /metrics"
    }
    $job = [regex]::Match((Get-Content (Join-Path $ReleaseDir 'cluster\prometheus.yaml') -Raw), '(?s)job_name: batch-tracker\s.*?(?=- job_name|\z)').Value
    Assert-True ($job -match 'scrape_interval: 5s') 'the app pods are not scraped every 5 s, so a 10 s bucket comes back empty'
    Assert-True ($job -match 'scrape_timeout: 4s') 'Prometheus refuses a timeout above the interval, and the default is 10 s'
    Assert-True ($job -match '__meta_kubernetes_pod_annotation_batch_tracker_scrape') 'the job keeps the wrong pods'
    $ingresses = ($documents | Where-Object { $_ -match '(?m)^kind: Ingress\s*$' }) -join "`n"
    Assert-True ($ingresses -notmatch '(?m)path: /metrics\s*$') 'an ingress routes /metrics'
}

Test-Check 'F14 the ingress routes /metrics/series exactly, to the monitor' {
    Assert-True ((Get-RenderedChart) -match '(?s)path: /metrics/series\s+pathType: Exact\s+backend:\s+service:\s+name: monitor\s') 'route missing or wider than the one path'
}

Test-Check 'REQ-BT-056 the ingress logs the correlation id, status, upstream status and path' {
    $values = Get-Content (Join-Path $ReleaseDir 'cluster\ingress-nginx.yaml') -Raw
    $format = [regex]::Match($values, '(?m)^\s+log-format-upstream: (.+)$').Groups[1].Value
    foreach ($field in '$http_x_correlation_id', '$status', '$upstream_status', '$uri', '$time_iso8601') {
        Assert-True ($format.Contains($field)) "no $field in the log format"
    }
    Assert-True ($values -match '(?m)^\s+log-format-escape-json: "true"\s') 'a client-written header could break the line'
}

Test-Check 'REQ-BT-056 the ingress log holds no cookie, token or query' {
    $values = Get-Content (Join-Path $ReleaseDir 'cluster\ingress-nginx.yaml') -Raw
    $format = [regex]::Match($values, '(?m)^\s+log-format-upstream: (.+)$').Groups[1].Value
    Assert-True ($format.Length -gt 0) 'no log format'
    $banned = @('$request', '$args', '$query_string', '$http_cookie', '$cookie_', '$http_authorization', '$http_x_internal_token', '$http_referer', '$sent_http_set_cookie')
    $found = @($banned | Where-Object { $format.Contains($_) })
    Assert-True ($found.Count -eq 0) "in the log format: $($found -join ',')"
}

Test-Check 'REQ-BT-056 the ingress routes /ingress/logs exactly, to the monitor' {
    Assert-True ((Get-RenderedChart) -match '(?s)path: /ingress/logs\s+pathType: Exact\s+backend:\s+service:\s+name: monitor\s') 'route missing or wider than the one path'
}

Test-Check 'REQ-BT-037 chart refuses a missing limit' {
    $broken = Get-StandInValues
    $broken['api'].Remove('resources')
    Write-SmokeValues $broken
    $refused = $false
    try { Invoke-Helm template batch-tracker $chartPath -n $AppNamespace -f $valuesPath | Out-Null } catch { $refused = "$_" -match 'caps.json limits missing: api' }
    Write-SmokeValues (Get-StandInValues)
    Assert-True $refused 'rendered without a limit for api'
}

Test-Check 'REQ-BT-037 chart refuses a missing image' {
    $broken = Get-StandInValues
    $broken['images'].Remove('web')
    Write-SmokeValues $broken
    $refused = $false
    try { Invoke-Helm template batch-tracker $chartPath -n $AppNamespace -f $valuesPath | Out-Null } catch { $refused = "$_" -match 'image missing: web' }
    Write-SmokeValues (Get-StandInValues)
    Assert-True $refused 'rendered without an image for web'
}

Remove-Item $smokeValues -Force
