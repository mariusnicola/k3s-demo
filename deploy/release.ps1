# Turns a git commit into the only thing setup deploys. The gate runs on a private export before
# anything outside it changes, so a broken commit or a half-edited working tree cannot reach the cluster.

function Get-ReleaseCommit([string]$Repo, [string]$Commit) {
    $hash = git -C $Repo rev-parse --verify "$Commit^{commit}"
    if ($LASTEXITCODE -ne 0) { throw "Not a commit: $Commit" }
    return $hash.Trim()
}

function Export-Commit([string]$Repo, [string]$Hash) {
    $work = Join-Path $env:TEMP "batchtracker-$($Hash.Substring(0, 12))-$(Get-Random)"
    New-Item -ItemType Directory -Path $work | Out-Null
    $archive = "$work.tar"
    git -C $Repo archive --format=tar -o $archive $Hash
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed.' }
    & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $work
    if ($LASTEXITCODE -ne 0) { throw 'Export failed.' }
    Remove-Item $archive -Force
    return $work
}

function Invoke-GateStep([string]$Name, [string]$Dir, [string]$Command, [hashtable]$Environment = @{}) {
    $saved = @{}
    foreach ($key in $Environment.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $Environment[$key])
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Push-Location $Dir
    try {
        # cmd keeps stderr out of PowerShell, which would turn the first warning into a stop.
        $output = @(& cmd /c "$Command 2>&1")
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
        foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
    $seconds = [int]$clock.Elapsed.TotalSeconds
    if ($code -ne 0) {
        Write-Output "[FAIL] gate: $Name, ${seconds} s"
        $log = Join-Path $env:TEMP 'batchtracker-gate-failure.log'
        [IO.File]::WriteAllLines($log, [string[]]$output)
        Write-Output "  full output: $log"
        # The tail below holds only the last stack traces, so names are kept apart from it.
        $output | Where-Object { $_ -cmatch '^\s*(FAIL|Failed)\s' } | ForEach-Object { Write-Output "  $_" }
        $output | Select-Object -Last 30 | ForEach-Object { Write-Output "  $_" }
        throw "Gate stopped at: $Name. Nothing outside the export changed."
    }
    Write-Output "[OK] gate: $Name, ${seconds} s"
}

# The same text setup runs, so a preflight pass proves the command setup will use.
$Node1Images = 'ctr -n k8s.io images'

function Invoke-Preflight([string]$Distro, [string]$Container, [string]$Images = $Node1Images) {
    # Read-only commands inside the real container. A first install has none, so nothing to check.
    $running = (wsl -d $Distro -u root -- docker ps --filter "name=^$Container$" --format '{{.Names}}') -join ''
    if ($running -ne $Container) {
        Write-Output "[SKIP] preflight: $Container not running"
        return
    }
    $commands = @{ 'image list' = "$Images ls -q"; 'image import' = "$Images import --help"; 'offload setting' = 'ethtool --version' }
    foreach ($name in $commands.Keys) {
        Invoke-GateStep "preflight $name" $env:TEMP "wsl -d $Distro -u root -- docker exec $Container $($commands[$name])"
    }
}

function Invoke-ImageGate([string]$Work, [string]$Distro, [object[]]$Images) {
    # cacheonly writes no image and no tag, and the real build reuses every layer.
    $root = (& wsl -d $Distro -- wslpath -a ($Work -replace '\\', '/')) -join ''
    foreach ($image in $Images) {
        Invoke-GateStep "image $($image.Name)" $Work "wsl -d $Distro --cd $root -- docker build --progress=plain --output=type=cacheonly -f $($image.File) $($image.Context)"
    }
}

function Invoke-Gate([string]$Work, [string]$Distro, [object[]]$Images, [string]$Container) {
    Invoke-Preflight $Distro $Container
    $app = Join-Path $Work 'app'
    $web = Join-Path $app 'web'
    Invoke-GateStep 'dotnet build' $app 'dotnet build BatchTracker.sln -c Release --nologo -v q'
    Invoke-GateStep 'unit tests' $app 'dotnet test tests\BatchTracker.UnitTests -c Release --no-build --nologo -v q'
    Invoke-GateStep 'architecture tests' $app 'dotnet test tests\BatchTracker.ArchTests -c Release --no-build --nologo -v q'
    Invoke-GateStep 'monitor tests' $Work 'dotnet test tests\monitor -c Release --nologo -v q'
    # Before npm ci: the web image context has no ignore file, and node_modules would be sent to Docker.
    # These builds fill the cache, so the real space cost shows here and the ones in setup reuse it.
    Write-BuildSpace 'before gate builds'
    Invoke-ImageGate $Work $Distro $Images
    Write-BuildSpace 'after gate builds'
    Invoke-GateStep 'npm ci' $web 'npm ci --include=dev --no-audit --no-fund'
    Invoke-GateStep 'tsc' $web 'npx tsc --noEmit'
    Invoke-GateStep 'lint' $web 'npm run lint'
    Invoke-GateStep 'web tests' $web 'npm test' @{ NODE_ENV = 'test' }
    Invoke-GateStep 'production build' $web 'npm run build' @{ NODE_ENV = 'production' }
}

# Which service reads which key. Only Get-SecretScript and the chart's bt.secret name a Secret.
$ServiceSecrets = [ordered]@{
    api        = 'JWT_PRIVATE_KEY', 'JWT_PUBLIC_KEY', 'COSMOS_CONNECTION', 'MONGO_CONNECTION', 'SERVICEBUS_CONNECTION', 'INTERNAL_TOKEN', 'LINE_MACHINE_TOKEN', 'MONITOR_MACHINE_TOKEN'
    worker     = 'COSMOS_CONNECTION', 'MONGO_CONNECTION', 'INTERNAL_TOKEN'
    dispatcher = 'COSMOS_CONNECTION', 'MONGO_CONNECTION', 'SERVICEBUS_CONNECTION', 'INTERNAL_TOKEN'
    live       = 'JWT_PUBLIC_KEY', 'COSMOS_CONNECTION', 'MONGO_CONNECTION', 'INTERNAL_TOKEN'
    nightly    = , 'INTERNAL_TOKEN'
}

function Read-EnvFile([string]$File) {
    $values = @{}
    foreach ($line in Get-Content $File) {
        if ($line -notmatch '^[A-Z]') { continue }
        $name, $value = $line -split '=', 2
        $values[$name] = $value
    }
    return $values
}

function Assert-AppEnv([string]$File, [string]$Example) {
    $keys = @((Read-EnvFile $Example).Keys | Sort-Object)
    if (-not (Test-Path $File)) { throw "Missing $File. It needs: $($keys -join ', ')." }
    $values = Read-EnvFile $File
    $missing = @($keys | Where-Object { -not "$($values[$_])".Trim() })
    if ($missing.Count -gt 0) { throw "$File has no value for: $($missing -join ', ')." }
}

function Set-EnvLine([string]$File, [string]$Key, [string]$Value) {
    $kept = if (Test-Path $File) { @(Get-Content $File | Where-Object { $_ -and ($_ -notmatch "^$Key=") }) } else { @() }
    [IO.File]::WriteAllText($File, ((@($kept) + "$Key=$Value") -join "`n") + "`n")
}

function Set-MongoUser([string]$MongoFile, [string]$AppFile, [string]$PasswordKey, [string]$ConnectionKey, [string]$Connection) {
    # Values stay inside this function and the two files: no argument, no output.
    $password = if (Test-Path $MongoFile) { (Read-EnvFile $MongoFile)[$PasswordKey] }
    $generated = -not "$password".Trim()
    if ($generated) {
        $bytes = New-Object byte[] 24
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $password = -join ($bytes | ForEach-Object { $_.ToString('x2') })
        Set-EnvLine $MongoFile $PasswordKey $password
    }
    # A new password has to replace the connection too, or its user would sign in with the old one.
    if ($generated -or -not "$((Read-EnvFile $AppFile)[$ConnectionKey])".Trim()) {
        Set-EnvLine $AppFile $ConnectionKey ($Connection -f $password)
    }
}

function Set-MongoEnv([string]$MongoFile, [string]$AppFile, [string]$HostAddress) {
    $options = 'serverSelectionTimeoutMS=3000&retryWrites=false'
    Set-MongoUser $MongoFile $AppFile 'MONGO_PASSWORD' 'MONGO_CONNECTION' "mongodb://admin:{0}@${HostAddress}:27018/?authSource=admin&$options"
    # In the Mongo file: every key of the app file reaches a service Secret, and this one must not.
    Set-MongoUser $MongoFile $MongoFile 'MONGO_MONITOR_PASSWORD' 'MONGO_MONITOR_CONNECTION' "mongodb://monitor:{0}@${HostAddress}:27018/monitor?authSource=monitor&$options"
}

function Get-MongoMonitorUserScript([string]$Password) {
    return @"
const monitor = db.getSiblingDB("monitor");
const roles = [{ role: "readWrite", db: "monitor" }];
if (monitor.getUser("monitor")) { monitor.updateUser("monitor", { pwd: "$Password", roles: roles }); } else { monitor.createUser({ user: "monitor", pwd: "$Password", roles: roles }); } print("monitor=set");
"@
}

function New-AppEnv([string]$File, [string]$Distro) {
    # Values stay inside this script and the file: no argument, no output.
    $target = (& wsl -d $Distro -- wslpath -a ($File -replace '\\', '/')) -join ''
    $script = @"
set -eo pipefail
pem=`$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt)
private=`$(printf '%s\n' "`$pem" | openssl pkcs8 -topk8 -nocrypt -outform DER | base64 -w0)
public=`$(printf '%s\n' "`$pem" | openssl pkey -pubout -outform DER | base64 -w0)
printf 'JWT_PRIVATE_KEY=%s\nJWT_PUBLIC_KEY=%s\nINTERNAL_TOKEN=%s\nLINE_MACHINE_TOKEN=%s\nMONITOR_MACHINE_TOKEN=%s\n' "`$private" "`$public" "`$(openssl rand -hex 32)" "`$(openssl rand -hex 32)" "`$(openssl rand -hex 32)" > '$target'
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($script.Replace("`r`n", "`n")))
    wsl -d $Distro -- bash -c "echo $encoded | base64 -d | bash"
    if ($LASTEXITCODE -ne 0) { throw 'Key generation failed.' }
}

function Get-SecretScript([string]$EnvPath, [string]$Namespace) {
    # The values travel by pipe from the file, so none reaches a command line.
    foreach ($service in $ServiceSecrets.Keys) {
        "grep -E '^($($ServiceSecrets[$service] -join '|'))=' '$EnvPath' | k3s kubectl -n $Namespace create secret generic batch-tracker-$service --from-env-file=/dev/stdin --dry-run=client -o yaml | k3s kubectl apply -f - >/dev/null"
    }
}

function Get-SnapshotPending([string]$Frame) {
    # A zero from a Live that has not read the store, or holds only part of the open rows, says nothing about the queue.
    $snapshot = $Frame | ConvertFrom-Json
    if ($snapshot.type -ne 'snapshot') { throw "not a snapshot: $($snapshot.type)" }
    if ($null -eq $snapshot.reconciledAt) { throw 'Live has not read the store yet: not read yet' }
    if ($snapshot.capped) { throw 'Live holds only part of the open rows: capped' }
    return [int]$snapshot.summary.pending
}

function Read-LivePending($Session, [string]$Origin) {
    # Every socket starts with a snapshot, and Live answers it from memory when the store is too busy for a read.
    $socket = New-Object System.Net.WebSockets.ClientWebSocket
    try {
        $socket.Options.Cookies = $Session.Cookies
        $socket.Options.SetRequestHeader('Origin', $Origin)
        $limit = New-Object System.Threading.CancellationTokenSource 15000
        $socket.ConnectAsync([uri]($Origin.Replace('http://', 'ws://') + '/ws/hops'), $limit.Token).GetAwaiter().GetResult() | Out-Null
        $buffer = New-Object byte[] 65536
        $frame = New-Object System.IO.MemoryStream
        do {
            $part = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $limit.Token).GetAwaiter().GetResult()
            $frame.Write($buffer, 0, $part.Count)
        } while (-not $part.EndOfMessage)
        return Get-SnapshotPending ([Text.Encoding]::UTF8.GetString($frame.ToArray()))
    } finally {
        $socket.Dispose()
    }
}

function Get-MongoPendingScript {
    # The same open states the store reads, and Live's own count comes from them.
    return 'print("pending=" + db.getSiblingDB("batch-tracker").batches.countDocuments({ "pending.status": { $in: ["pending", "sent"] } }));'
}

function Get-MongoPending([string]$Output) {
    $found = [regex]::Match($Output, 'pending=(\d+)')
    if (-not $found.Success) { throw 'Mongo gave no pending count.' }
    return [int]$found.Groups[1].Value
}

function Read-PendingCount([scriptblock]$Live, [scriptblock]$Store) {
    # A Live that is down cannot say the queue is empty, so its failure moves the question to the store itself.
    try {
        return [pscustomobject]@{ Pending = [int](& $Live); Source = 'Live'; Problem = $null }
    } catch {
        $problem = $_.Exception.Message
    }
    try {
        return [pscustomobject]@{ Pending = [int](& $Store); Source = 'Mongo'; Problem = $problem }
    } catch {
        throw "Live: $problem Mongo: $($_.Exception.Message)"
    }
}

function Get-DrainFailure([int]$Lowest) {
    if ($Lowest -eq [int]::MaxValue) { return 'No pending count read for 5 minutes.' }
    return "Pending stuck at $Lowest for 5 minutes."
}

function Publish-Release([string]$Work, [string]$Release) {
    # Same path every run: compose compares bind paths, and a new path would recreate containers.
    # COMMIT stays: until this run finishes, the cluster still runs the commit it names.
    robocopy $Work $Release /MIR /NFL /NDL /NJH /NJS /NP /XD node_modules bin obj dist /XF COMMIT | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Release copy failed, robocopy code $LASTEXITCODE." }
    $global:LASTEXITCODE = 0
}

function Set-ReleaseCommit([string]$Release, [string]$Hash) {
    [IO.File]::WriteAllText((Join-Path $Release 'COMMIT'), "$Hash`n")
}
