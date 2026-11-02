# Sets up the local cluster and the app. Safe to re-run. Run from an elevated PowerShell.
param(
    [string]$CapsFile = (Join-Path $PSScriptRoot 'caps.json'),
    # Refuse or accept caps.json, change nothing.
    [switch]$CapsOnly,
    # Everything except the app images, the chart and the line.
    [switch]$InfraOnly,
    # What to deploy. The working tree is never read.
    [string]$Commit = 'HEAD',
    # Export and gate only, nothing outside the export changes.
    [switch]$GateOnly,
    # Set by the first stage: this copy is the release and already passed the gate.
    [switch]$Released
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'caps.ps1')
. (Join-Path $PSScriptRoot 'changes.ps1')
. (Join-Path $PSScriptRoot 'release.ps1')
. (Join-Path $PSScriptRoot 'watch.ps1')

$Distro = 'Ubuntu-24.04'
$ControlPlaneIp = '<HOST_IP>'
$K3sVersion = 'v1.36.5+k3s1'
$WorkerUser = '<WORKER_USER>'
$Node1Node = 'k3s-node1'
$Node1Container = 'batchtracker-node1'
$IngressOrigin = 'http://<INGRESS_IP>'
$LineOperator = 'ada'
$WorkerIps = @('<AGENT_IP>')
$Helm = 'helm'
$AppNamespace = 'batch-tracker'
$AppEnvFile = Join-Path $StateDir 'app\.env'
$RepoRoot = Split-Path $PSScriptRoot
$AppImages = @(
    @{ Name = 'api';        Context = 'app' }
    @{ Name = 'worker';     Context = 'app' }
    @{ Name = 'dispatcher'; Context = 'app' }
    @{ Name = 'live';       Context = 'app' }
    @{ Name = 'web';        Context = 'app/web' }
    @{ Name = 'nightly';    Context = 'deploy/app/images' }
    @{ Name = 'netprobe';   Context = 'deploy/netprobe' }
)

# Free LAN addresses for MetalLB, outside the DHCP pool, as first-last. Empty leaves LoadBalancer Services pending.
$LoadBalancerRange = '<LB_RANGE_START>-<LB_RANGE_END>'

$Charts = @(
    @{ Name = 'metallb';       Namespace = 'metallb-system' }
    @{ Name = 'ingress-nginx'; Namespace = 'ingress-nginx' }
    @{ Name = 'prometheus';    Namespace = 'prometheus' }
    @{ Name = 'keda';          Namespace = 'keda' }
)

# Mirrored WSL traffic is filtered by the Hyper-V firewall, which blocks inbound by default.
$WslVmCreatorId = '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}'

$ClusterPorts = @(
    @{ Name = 'BatchTracker-k3s-api';          Protocol = 'TCP'; Port = 6443 }
    @{ Name = 'BatchTracker-servicebus-amqp';  Protocol = 'TCP'; Port = 5682 }
    @{ Name = 'BatchTracker-servicebus-admin'; Protocol = 'TCP'; Port = 5310 }
    @{ Name = 'BatchTracker-cosmos-gateway';   Protocol = 'TCP'; Port = 8091 }
    @{ Name = 'BatchTracker-mongo';            Protocol = 'TCP'; Port = 27018 }
    @{ Name = 'BatchTracker-monitor';          Protocol = 'TCP'; Port = 5320 }
    @{ Name = 'BatchTracker-line';             Protocol = 'TCP'; Port = 5321 }
    @{ Name = 'BatchTracker-flannel-vxlan';    Protocol = 'UDP'; Port = 8472 }
    @{ Name = 'BatchTracker-kubelet';          Protocol = 'TCP'; Port = 10250 }
)

function Assert-Elevated {
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell.'
    }
}

function Set-ClusterFirewall {
    foreach ($rule in $ClusterPorts) {
        if (Get-NetFirewallHyperVRule -Name $rule.Name -ErrorAction SilentlyContinue) {
            Set-NetFirewallHyperVRule -Name $rule.Name -RemoteAddresses $WorkerIps
            Write-Output "[OK] firewall rule limited to node2: $($rule.Name)"
            continue
        }
        New-NetFirewallHyperVRule -Name $rule.Name -DisplayName $rule.Name -Direction Inbound `
            -VMCreatorId $WslVmCreatorId -Protocol $rule.Protocol -LocalPorts $rule.Port `
            -RemoteAddresses $WorkerIps -Action Allow | Out-Null
        Write-Output "[OK] firewall rule added: $($rule.Name)"
    }
}

function Set-WslIdleTimeout {
    # WSL stops when no session is open, which takes the control plane down with it.
    $config = "$env:USERPROFILE\.wslconfig"
    if (Select-String -Path $config -Pattern '^instanceIdleTimeout=-1$' -Quiet) {
        Write-Output '[OK] WSL idle shutdown already off'
        return
    }
    Add-Content -Path $config -Encoding ascii -Value "`r`n[general]`r`ninstanceIdleTimeout=-1"
    # WSL reads this file only when the VM starts.
    wsl --shutdown
    Write-Output '[OK] WSL idle shutdown turned off, WSL restarted'
}

function Set-WslLogonStart {
    # WSL stays down after a reboot until something starts it. This runs once at logon, it is not a keep-alive.
    schtasks /create /tn 'BatchTracker-wsl-start' /tr "wsl.exe -d $Distro --exec true" /sc onlogon /ru $env:USERNAME /it /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'WSL logon task failed.' }
    schtasks /run /tn 'BatchTracker-wsl-start' | Out-Null
    Write-Output '[OK] WSL starts at logon'
}

function Set-WatchTask {
    # Runs as the owner in his session, because it reads his own WSL registry entries.
    $command = Get-WatchTaskCommand (Join-Path $PSScriptRoot 'watch.ps1')
    schtasks /create /tn 'BatchTracker-watch' /tr $command /sc minute /mo 1 /ru $env:USERNAME /it /f | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Watch task failed.' }
    Write-Output '[OK] machine watch runs every minute'
}

function Set-WslReport {
    New-Item -ItemType Directory -Force -Path $ChangesDir | Out-Null
    $target = "$(Get-WslPath $ChangesDir)/wsl.json"
    Invoke-ControlPlane @"
set -e
cat > /usr/local/bin/batchtracker-wsl-report <<'SCRIPT'
$(Get-WslReportScript)
SCRIPT
chmod 755 /usr/local/bin/batchtracker-wsl-report
cat > /etc/systemd/system/batchtracker-wsl-report.service <<EOF
[Unit]
Description=Writes what only the inside of WSL can see for the machine watch
[Service]
Type=oneshot
ExecStart=/usr/local/bin/batchtracker-wsl-report $target
EOF
cat > /etc/systemd/system/batchtracker-wsl-report.timer <<EOF
[Timer]
OnBootSec=30s
OnUnitActiveSec=30s
AccuracySec=5s
[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now batchtracker-wsl-report.timer >/dev/null
echo '[OK] WSL report timer on'
"@
}

function Get-RemoteBashArgument([string]$Script) {
    # Base64 survives PowerShell quoting and the BOM it adds when piping to native commands.
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Script -replace "`r`n", "`n"))
    return [Convert]::ToBase64String($bytes)
}

function Invoke-ControlPlane([string]$Script) {
    $encoded = Get-RemoteBashArgument $Script
    wsl -d $Distro -u root -- bash -c "echo $encoded | base64 -d | bash"
    if ($LASTEXITCODE -ne 0) { throw 'Control plane step failed.' }
}

function Invoke-Worker([string]$Ip, [string]$Script) {
    $encoded = Get-RemoteBashArgument $Script
    ssh -o BatchMode=yes -o ConnectTimeout=10 "$WorkerUser@$Ip" "echo $encoded | base64 -d | sudo -n bash"
    if ($LASTEXITCODE -ne 0) { throw "Worker step failed on $Ip." }
}

function Set-ControlPlane {
    # Mirrored WSL does not deliver inbound UDP to flannel's kernel socket, so the
    # control plane stays out of the pod network and reaches pods through agent tunnels.
    Invoke-ControlPlane @"
set -e
mkdir -p /etc/rancher/k3s
new=`$(mktemp)
cat > `$new <<EOF
advertise-address: $ControlPlaneIp
tls-san:
  - $ControlPlaneIp
disable-agent: true
egress-selector-mode: cluster
flannel-external-ip: true
kube-controller-manager-arg:
  - horizontal-pod-autoscaler-sync-period=10s
EOF
if ! cmp -s `$new /etc/rancher/k3s/config.yaml; then
  # A plain restart: the kill-all script rewrites the iptables table other containers share, and this server has no pods to clear.
  mv `$new /etc/rancher/k3s/config.yaml
  chmod 644 /etc/rancher/k3s/config.yaml
  systemctl restart k3s
  echo '[OK] control plane config changed, k3s restarted'
else
  rm -f `$new
  echo '[OK] control plane config unchanged'
fi
for i in `$(seq 1 60); do k3s kubectl get --raw /readyz >/dev/null 2>&1 && break; sleep 2; done
k3s kubectl get --raw /readyz >/dev/null
k3s kubectl delete node `$(hostname | tr '[:upper:]' '[:lower:]') --ignore-not-found
"@
}

function Get-JoinToken {
    $token = wsl -d $Distro -u root -- cat /var/lib/rancher/k3s/server/node-token
    if (-not $token) { throw 'Join token not found.' }
    return $token.Trim()
}

function Join-Worker([string]$Ip, [string]$Token) {
    Invoke-Worker $Ip @"
set -e
url=https://${ControlPlaneIp}:6443
env=/etc/systemd/system/k3s-agent.service.env
if grep -q "`$url" `$env 2>/dev/null && k3s --version | grep -q '$($K3sVersion -replace '\+k3s1','')'; then
  echo '[OK] worker already joined: '`$(hostname)
  exit 0
fi
[ -x /usr/local/bin/k3s-agent-uninstall.sh ] && /usr/local/bin/k3s-agent-uninstall.sh >/dev/null 2>&1 || true
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION='$K3sVersion' K3S_URL="`$url" K3S_TOKEN='$Token' sh -s - agent >/dev/null
echo '[OK] worker joined: '`$(hostname)
"@
}

function Set-ServiceBus {
    $dir = Join-Path $PSScriptRoot 'servicebus'
    $envFile = Get-ComposeEnvFile $dir
    if (-not (Test-Path $envFile)) {
        $password = 'Zp1!' + [guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText($envFile, "MSSQL_SA_PASSWORD=$password`n")
        Write-Output '[OK] Service Bus SQL password created'
    }
    Start-Compose 'Service Bus emulator' $dir 'http://127.0.0.1:5310/health'
}

function Get-WslPath([string]$Path) {
    return wsl -d $Distro -- wslpath -a ($Path -replace '\\', '/')
}

function Get-Allocatable {
    # Empty on a first run: the requests check then waits for the workers to join.
    $result = @{}
    $json = wsl -d $Distro -- k3s kubectl get nodes -o json
    if ($LASTEXITCODE -ne 0) { return $result }
    foreach ($node in ($json -join "`n" | ConvertFrom-Json).items) {
        $result[$node.metadata.name] = ConvertTo-MemoryMi $node.status.allocatable.memory
    }
    return $result
}

function Write-GeneratedValues([string]$Name, [hashtable]$Values) {
    $dir = Join-Path $StateDir '.generated'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir "$Name.json"
    [IO.File]::WriteAllText($path, ($Values | ConvertTo-Json -Depth 20))
    return Get-WslPath $path
}

function Get-ComposeEnvFile([string]$Dir) {
    # Secrets and limits outlive a release, which is replaced on every deploy.
    $folder = Join-Path $StateDir (Split-Path -Leaf $Dir)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    return Join-Path $folder '.env'
}

function Set-ComposeEnv([string]$Dir, [hashtable]$Extra = @{}) {
    # Compose reads .env on every command, so a manual restart keeps the same limits.
    $envFile = Get-ComposeEnvFile $Dir
    $kept = @()
    if (Test-Path $envFile) {
        $kept = @(Get-Content $envFile | Where-Object { $_ -and ($_ -notmatch '^CAP_') -and ($Extra.Keys -notcontains ($_ -split '=', 2)[0]) })
    }
    $lines = $kept + @(Get-ComposeCaps $Caps (Split-Path -Leaf $Dir)) + @($Extra.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    [IO.File]::WriteAllText($envFile, (($lines -join "`n") + "`n"))
}

function Update-Compose([string]$Name, [string]$Dir) {
    Set-ComposeEnv $Dir
    $envFile = Get-WslPath (Get-ComposeEnvFile $Dir)
    $before = (wsl -d $Distro --cd $Dir -- docker compose --env-file $envFile ps -q) -join ','
    wsl -d $Distro --cd $Dir -- docker compose --env-file $envFile up -d --build --quiet-pull
    if ($LASTEXITCODE -ne 0) { throw "$Name start failed." }
    $after = (wsl -d $Distro --cd $Dir -- docker compose --env-file $envFile ps -q) -join ','
    if ($before -ne $after) { Write-Change 'container recreated' $Name $before $after }
}

function Start-Compose([string]$Name, [string]$Dir, [string]$HealthUrl) {
    Update-Compose $Name $Dir
    foreach ($attempt in 1..60) {
        try {
            Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 3 | Out-Null
            Write-Output "[OK] $Name healthy"
            return
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    throw "$Name not healthy."
}

function Set-Cosmos {
    Start-Compose 'Cosmos emulator' (Join-Path $PSScriptRoot 'cosmos') 'http://127.0.0.1:8092/ready'
}

function Set-Mongo {
    Update-Compose 'MongoDB' (Join-Path $PSScriptRoot 'mongo')
    # -e runs no shell, which would read the brackets. Ping needs no sign-in.
    foreach ($attempt in 1..60) {
        $answer = wsl -d $Distro -e docker exec batchtracker-mongo mongosh --quiet --eval 'db.runCommand({ping:1}).ok'
        if ($LASTEXITCODE -eq 0 -and "$answer".Trim() -eq '1') {
            # By stdin, so the password is on no command line. The admin password stays inside the container.
            $password = (Read-EnvFile (Get-ComposeEnvFile (Join-Path $PSScriptRoot 'mongo'))).MONGO_MONITOR_PASSWORD
            $result = Get-MongoMonitorUserScript $password | wsl -d $Distro -e docker exec -i batchtracker-mongo sh -c 'mongosh --quiet -u admin -p $MONGO_INITDB_ROOT_PASSWORD --authenticationDatabase admin'
            if ("$result" -notmatch 'monitor=set') { throw 'MongoDB monitor user not set.' }
            Write-Output '[OK] MongoDB healthy'
            return
        }
        Start-Sleep -Seconds 2
    }
    throw 'MongoDB did not answer a ping.'
}

function Invoke-Terraform([string[]]$Targets) {
    # State and the provider cache sit beside the release, which is replaced on every deploy.
    $state = Join-Path $StateDir '.terraform-state'
    New-Item -ItemType Directory -Force -Path $state | Out-Null
    $stateWsl = Get-WslPath $state
    $generated = Get-WslPath (Join-Path $StateDir '.generated')
    $directory = Get-WslPath (Join-Path $PSScriptRoot 'terraform')
    $targetArguments = ($Targets | ForEach-Object { "-target=$_" }) -join ' '
    Invoke-ControlPlane @"
set -e
export TF_DATA_DIR=$stateWsl/.terraform TF_IN_AUTOMATION=1
cd $directory
terraform init -input=false -lockfile=readonly >/dev/null
terraform apply -input=false -auto-approve -state=$stateWsl/terraform.tfstate -var=generated_dir=$generated -var=load_balancer_range=$LoadBalancerRange $targetArguments >/dev/null
"@
}

function Install-Chart($Chart) {
    $name = $Chart.Name
    $namespace = $Chart.Namespace
    Write-GeneratedValues $name (Get-ChartValues $Caps $name) | Out-Null
    Invoke-Terraform @("helm_release.$name")
    # Rollout status, not a Helm wait: a LoadBalancer Service with no pool would never finish waiting.
    Invoke-ControlPlane @"
set -e
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
for workload in `$(k3s kubectl -n $namespace get deploy,ds,sts -o name); do
  k3s kubectl -n $namespace rollout status `$workload --timeout=300s >/dev/null
done
echo '[OK] chart ready: $name'
"@
}

function Set-LoadBalancerPool {
    if (-not $LoadBalancerRange) {
        Write-Output '[SKIP] MetalLB address pool: LoadBalancerRange not set'
        return
    }
    Invoke-Terraform @('kubernetes_manifest.pool', 'kubernetes_manifest.advertisement')
}

function Set-AppSecret {
    Invoke-Terraform @('kubernetes_namespace_v1.batch-tracker')
    $secrets = (Get-SecretScript (Get-WslPath $AppEnvFile) $AppNamespace) -join "`n"
    Invoke-ControlPlane @"
set -eo pipefail
$secrets
echo '[OK] app secrets applied'
"@
}

function Build-AppImage([string]$Name, [string]$Context) {
    # Attestations carry a timestamp, which would give identical content a new ID on every build.
    $log = "/tmp/batchtracker-build-$Name.log"
    wsl -d $Distro --cd $RepoRoot -- bash -c "docker build --progress=plain --provenance=false --sbom=false --iidfile $log.id -f deploy/app/images/$Name.Dockerfile -t batch-tracker/${Name}:build $Context > $log 2>&1"
    if ($LASTEXITCODE -ne 0) {
        wsl -d $Distro -- tail -n 40 $log
        throw "Image build failed: $Name."
    }
    $id = wsl -d $Distro -- cat "$log.id"
    # The image ID is the hash of the content, so an unchanged build keeps its tag and no pod restarts.
    $tag = "batch-tracker/${Name}:$((($id | Select-Object -Last 1) -replace '^sha256:', '').Substring(0, 12))"
    wsl -d $Distro -- docker tag "batch-tracker/${Name}:build" $tag
    if ($LASTEXITCODE -ne 0) { throw "Image tag failed: $Name." }
    Write-Output "[OK] image built: $tag" | Out-Host
    return $tag
}

function Get-WorkerImages([string]$Ip) {
    $list = ssh -o BatchMode=yes -o ConnectTimeout=10 "$WorkerUser@$Ip" 'sudo -n k3s ctr -n k8s.io images ls -q'
    if ($LASTEXITCODE -ne 0) { throw "Image list failed on $Ip." }
    return @($list)
}

function Import-Images([string[]]$Tags) {
    # Each remote step is one call, so a failure names the step and nothing runs after it.
    $archive = Join-Path $env:TEMP 'batch-tracker-images.tar'
    $remote = '/tmp/batch-tracker-images.tar'
    foreach ($ip in $WorkerIps) {
        $present = Get-WorkerImages $ip
        $missing = @($Tags | Where-Object { $present -notcontains "docker.io/$_" })
        if ($missing.Count -eq 0) {
            Write-Output "[OK] images already on $ip"
            continue
        }
        $saveArgs = @('-d', $Distro, '--', 'docker', 'save', '-o', (Get-WslPath $archive)) + $missing
        wsl @saveArgs
        if ($LASTEXITCODE -ne 0) { throw 'Image save failed.' }
        scp -q -o BatchMode=yes -o ConnectTimeout=10 $archive "${WorkerUser}@${Ip}:$remote"
        if ($LASTEXITCODE -ne 0) { throw "Image copy failed on $ip." }
        ssh -o BatchMode=yes -o ConnectTimeout=10 "$WorkerUser@$ip" "sudo -n k3s ctr -n k8s.io images import $remote" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Image import failed on $ip." }
        ssh -o BatchMode=yes -o ConnectTimeout=10 "$WorkerUser@$ip" "rm -f $remote"
        if ($LASTEXITCODE -ne 0) { throw "Image cleanup failed on $ip." }
        Remove-Item $archive -Force
        $imported = Get-WorkerImages $ip
        $absent = @($Tags | Where-Object { $imported -notcontains "docker.io/$_" })
        if ($absent.Count -gt 0) { throw "Images not on ${ip}: $($absent -join ', ')" }
        Write-Output "[OK] images imported on $ip"
        Write-Change 'image import' $ip $null ($missing -join ',')
    }
}

function Set-Node1 {
    $dir = Join-Path $PSScriptRoot 'node1'
    Set-ComposeEnv $dir
    # Docker's own config points at Docker Desktop's credential helper, absent here, and the image is public.
    $anonymous = Join-Path $StateDir '.generated\docker-anonymous'
    New-Item -ItemType Directory -Path $anonymous -Force | Out-Null
    wsl -d $Distro --cd $dir -- env "DOCKER_CONFIG=$(Get-WslPath $anonymous)" docker compose --env-file (Get-WslPath (Get-ComposeEnvFile $dir)) up -d --quiet-pull
    if ($LASTEXITCODE -ne 0) { throw 'Node1 start failed.' }
    Invoke-ControlPlane @"
for i in `$(seq 1 90); do
  k3s kubectl taint node $Node1Node batchtracker/node1- >/dev/null 2>&1
  [ "`$(k3s kubectl get node $Node1Node -o jsonpath='{.status.conditions[?(@.type=="Ready")].status}' 2>/dev/null)" = True ] && { echo '[OK] node ready: $Node1Node'; exit 0; }
  sleep 2
done
echo 'Node not ready: $Node1Node' >&2
exit 1
"@
}

function Set-MetricsServer {
    # A k3s add-on has no chart value for this. The patch survives a server restart, and setup repeats it
    # because a k3s upgrade rewrites the add-on's manifest.
    $node = @($Caps.reserved | Where-Object { $_.name -eq 'metrics-server' })[0].node
    Invoke-ControlPlane @"
k3s kubectl -n kube-system patch deployment metrics-server --type merge -p '{"spec":{"template":{"spec":{"nodeSelector":{"kubernetes.io/hostname":"$node"}}}}}'
"@
}

function Import-Node1Images([string[]]$Tags) {
    # Node1 is a container on this machine, so it takes the images directly, without ssh.
    $wanted = $Tags -join ' '
    Invoke-ControlPlane @"
set -e
present=`$(docker exec $Node1Container $Node1Images ls -q)
missing=''
for tag in $wanted; do
  echo "`$present" | grep -qx "docker.io/`$tag" || missing="`$missing `$tag"
done
if [ -z "`$missing" ]; then echo '[OK] images already on $Node1Node'; exit 0; fi
docker save `$missing | docker exec -i $Node1Container $Node1Images import - >/dev/null
echo "[OK] images imported on ${Node1Node}:`$missing"
"@
    Write-Change 'image import' $Node1Node $null $wanted
}

function Read-MongoPending {
    $output = Get-MongoPendingScript | wsl -d $Distro -e docker exec -i batchtracker-mongo sh -c 'mongosh --quiet -u admin -p $MONGO_INITDB_ROOT_PASSWORD --authenticationDatabase admin'
    return Get-MongoPending ($output -join "`n")
}

function Stop-LineAndDrain {
    # Service Bus may be recreated by this run, and a message in flight would be lost.
    try {
        $body = @{ user = $LineOperator } | ConvertTo-Json
        $login = Invoke-RestMethod -Method Post -Uri "$IngressOrigin/api/auth/dev-login" -Body $body -ContentType 'application/json' -TimeoutSec 15 -SessionVariable session
    } catch {
        Write-Output '[SKIP] no app answering, nothing to drain'
        return
    }
    $headers = @{ Origin = $IngressOrigin }
    # An API from before the session cookie answers with a token and sets no cookie.
    if ($login.token) { $headers.Authorization = "Bearer $($login.token)" }
    try {
        Invoke-RestMethod -Method Delete -Uri "$IngressOrigin/line" -WebSession $session -Headers $headers -TimeoutSec 15 | Out-Null
        Write-Output '[OK] line stopped'
    } catch {
        Write-Output "[SKIP] line not answering: $($_.Exception.Message)"
    }
    # A long queue is normal after load, so only a queue that stopped moving ends the wait.
    $lowest = [int]::MaxValue
    $moved = Get-Date
    while (((Get-Date) - $moved).TotalMinutes -lt 5) {
        try {
            $count = Read-PendingCount { Read-LivePending $session $IngressOrigin } { Read-MongoPending }
        } catch {
            Write-Output "[WARN] no pending count: $($_.Exception.Message)"
            Start-Sleep -Seconds 5
            continue
        }
        if ($count.Problem) { Write-Output "[WARN] live count: $($count.Problem)" }
        $pending = $count.Pending
        if ($pending -eq 0) {
            Write-Output "[OK] no event pending, counted by $($count.Source)"
            return
        }
        if ($pending -lt $lowest) {
            $lowest = $pending
            $moved = Get-Date
            Write-Output "[OK] draining: $pending pending, counted by $($count.Source)"
        }
        Start-Sleep -Seconds 5
    }
    throw (Get-DrainFailure $lowest)
}

function Set-Monitor($Secrets) {
    $dir = Join-Path $PSScriptRoot 'monitor'
    $mongo = Read-EnvFile (Get-ComposeEnvFile (Join-Path $PSScriptRoot 'mongo'))
    # A new commit always recreates the container, so it compares against that commit's caps.json.
    Set-ComposeEnv $dir @{ DEPLOYED_COMMIT = $Commit; CHANGES_DIR = (Get-WslPath $ChangesDir); MONITOR_MACHINE_TOKEN = $Secrets.MONITOR_MACHINE_TOKEN; MONGO_CONNECTION = $mongo.MONGO_MONITOR_CONNECTION; JWT_PUBLIC_KEY = $Secrets.JWT_PUBLIC_KEY }
    Start-Compose 'Monitor' $dir 'http://127.0.0.1:5320/alive'
}

function Install-App($Secrets) {
    $tags = @{}
    Write-BuildSpace 'before image builds'
    foreach ($image in $AppImages) { $tags[$image.Name] = Build-AppImage $image.Name $image.Context }
    $lineTag = Build-AppImage 'line' 'app'
    Write-BuildSpace 'after image builds'
    Import-Images @($tags.Values)
    Import-Node1Images @($tags.Values)
    Set-AppSecret

    $values = Get-ChartValues $Caps 'batch-tracker'
    $values['images'] = $tags
    # Pods hold a Secret from their start, so a changed value needs a rollout.
    $values['secretRevision'] = (Get-FileHash $AppEnvFile -Algorithm SHA256).Hash
    $valuesPath = Write-GeneratedValues 'batch-tracker' $values
    $chart = Get-WslPath (Join-Path $PSScriptRoot 'app')
    $rendered = Invoke-ControlPlane "KUBECONFIG=/etc/rancher/k3s/k3s.yaml $Helm template batch-tracker $chart -n $AppNamespace -f $valuesPath"
    Assert-TrustedProxies ($rendered -join "`n") $PSScriptRoot
    Invoke-Terraform @('helm_release.batch-tracker')
    Write-Output '[OK] chart ready: batch-tracker'
    $lineDir = Join-Path $PSScriptRoot 'line'
    Set-ComposeEnv $lineDir @{ LINE_IMAGE = $lineTag; JWT_PUBLIC_KEY = $Secrets.JWT_PUBLIC_KEY; LINE_MACHINE_TOKEN = $Secrets.LINE_MACHINE_TOKEN }
    Start-Compose 'Line' $lineDir 'http://127.0.0.1:5321/ready'
}

if (-not $CapsOnly -and -not $Released) {
    Assert-Elevated
    Assert-BuildSpace (Get-BuildDrives)
    $hash = Get-ReleaseCommit $RepoRoot $Commit
    Write-Output "[OK] deploying commit $hash"
    $work = Export-Commit $RepoRoot $hash
    try {
        $images = @($AppImages + @{ Name = 'line'; Context = 'app' } | ForEach-Object { @{ Name = $_.Name; Context = $_.Context; File = "deploy/app/images/$($_.Name).Dockerfile" } })
        $images += @{ Name = 'monitor'; Context = 'deploy/monitor'; File = 'deploy/monitor/Dockerfile' }
        if (-not (Test-Path $AppEnvFile)) {
            New-AppEnv $AppEnvFile $Distro
            Write-Output '[OK] app keys generated'
        }
        Set-MongoEnv (Join-Path $StateDir 'mongo\.env') $AppEnvFile $ControlPlaneIp
        Assert-AppEnv $AppEnvFile (Join-Path $work 'deploy\app\.env.example')
        Invoke-Gate $work $Distro $images $Node1Container
        if ($GateOnly) { exit 0 }
        $release = Join-Path $PSScriptRoot '.release'
        Publish-Release $work $release
    } finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    $stage = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $release 'deploy\setup.ps1'), '-Released', '-Commit', $hash)
    if ($InfraOnly) { $stage += '-InfraOnly' }
    & powershell @stage
    $finished = $LASTEXITCODE
    if ($finished -eq 0 -and -not $InfraOnly) { Set-ReleaseCommit $release $hash }
    exit $finished
}

$Caps = Read-Caps $CapsFile
Test-Caps $Caps (Get-Allocatable)
if ($CapsOnly) { exit 0 }

Assert-Elevated
$DeploySteps = @('Stop the line, drain events', 'Firewall and WSL settings', 'Control plane', 'Workers', 'Service Bus, Cosmos and MongoDB', 'Charts', 'App images, chart and line', 'Monitor')
$steps = if ($InfraOnly) { $DeploySteps -ne 'App images, chart and line' } else { $DeploySteps }
$operation = Start-Operation "Deploy $($Commit.Substring(0, 12))" 'deploy' $steps
try {
    Set-OperationStep $operation 'Stop the line, drain events'
    Stop-LineAndDrain
    Set-OperationStep $operation 'Firewall and WSL settings'
    Set-ClusterFirewall
    Set-WslIdleTimeout
    Set-WslLogonStart
    Set-WatchTask
    Set-WslReport
    Set-OperationStep $operation 'Control plane'
    Set-ControlPlane
    $token = Get-JoinToken
    Set-OperationStep $operation 'Workers'
    foreach ($ip in $WorkerIps) {
        $joined = Join-Worker $ip $token
        Write-Output $joined
        if ($joined -match 'worker joined') { Write-Change 'node joined' $ip $null $K3sVersion }
    }
    Set-Node1
    Set-MetricsServer
    Test-Caps $Caps (Get-Allocatable)
    Set-OperationStep $operation 'Service Bus, Cosmos and MongoDB'
    Set-ServiceBus
    Set-Cosmos
    Set-Mongo
    Set-OperationStep $operation 'Charts'
    foreach ($chart in $Charts) { Install-Chart $chart }
    Set-LoadBalancerPool
    if ($InfraOnly) {
        Write-Output '[SKIP] app and line: -InfraOnly'
    } else {
        Set-OperationStep $operation 'App images, chart and line'
        Install-App (Read-EnvFile $AppEnvFile)
    }
    Set-OperationStep $operation 'Monitor'
    Set-Monitor (Read-EnvFile $AppEnvFile)
    Stop-Operation $operation $true
} catch {
    Stop-Operation $operation $false
    throw
}
