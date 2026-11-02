# Setup guide

How to start this project on your own machines. Read all of it once before you type anything. The scripts do a lot to your PC (firewall rules, scheduled tasks, a WSL setting) and to a second machine.

## Read this first

- `deploy\setup.ps1` runs `terraform apply -auto-approve`. It has no pause and no plan-only switch. Section 5 shows how to read `terraform plan` before a later run. On the first run the state is empty, so every line is a `+` (create).
- How to read a plan: `+` creates, `~` changes in place, `-` destroys, `-/+` destroys and creates again. For a Helm release, destroy means the chart is uninstalled with everything in it. For the namespace `batch-tracker`, destroy deletes the namespace and every Secret in it. If a plan shows a `-` on something you did not expect, do not run setup.
- The scripts deploy a git commit, not your working tree. Every edit you make must be committed in your local clone before you run setup. Never push the filled-in version anywhere public.
- Some steps depend on how your machines are set up. They are marked "Note" in this guide. Check each one against your setup.

## 1. What you are building

Two machines on one home network, running one k3s cluster.

- The PC (Windows 11). It runs WSL 2 with Ubuntu. Inside WSL: the k3s control plane, Docker, Terraform and Helm. Docker on that PC also runs "node1" (a k3s worker, as a container named `batchtracker-node1`), the Service Bus emulator, the Cosmos DB emulator, MongoDB, the Monitor and the Line.
- The second machine (Linux, named `k3s-node2`). It joins as a k3s worker over SSH. It runs the ingress, MetalLB, Prometheus, KEDA and some app pods.
- One free address on your network (`<INGRESS_IP>`). MetalLB gives it to the ingress. You open the app at that address.

Setup builds and tests the code, starts everything, builds the container images, copies them to both nodes, installs MetalLB, ingress-nginx, Prometheus and KEDA with Terraform, then installs the app with Helm.

## 2. What you need before starting

The PC:

- Windows 11, 22H2 or later, Pro or better. Setup uses the Hyper-V firewall cmdlets, which Windows Home does not have.
- An elevated Windows PowerShell (setup restarts itself with `powershell`, which is Windows PowerShell 5.1), `git`, the OpenSSH client (`ssh`, `scp`), `tar.exe` and `robocopy` (both ship with Windows).
- .NET SDK 9.0.320 or a later 9.0 feature band (`global.json`, roll forward `latestFeature`).
- Node.js and npm on Windows. The code pins no host version. The web image builds with Node 22, so use 22.
- WSL 2 with an Ubuntu 24.04 distro. The scripts call it `Ubuntu-24.04`. Check with `wsl -l -v`. Inside it:
  - systemd on. In `/etc/wsl.conf`: `[boot]` then `systemd=true`. Setup uses `systemctl`.
  - Docker Engine with the compose plugin and buildx. Not Docker Desktop.
  - Terraform 1.17.1 or later (`versions.tf`). Providers are pinned: helm 3.3.0, kubernetes 3.4.0. `deploy/terraform/.terraform.lock.hcl` ships with the repo and setup runs `terraform init -lockfile=readonly`.
  - Helm (any version that can install the pinned charts: MetalLB 0.16.1, ingress-nginx 4.15.1, Prometheus 29.40.1, KEDA 2.21.0).
  - `openssl`, `curl` and `base64`.
  - The k3s server, version `v1.36.5+k3s1`, installed as the systemd service `k3s`. The scripts do not install it. See the Note in section 3.
- `~\.wslconfig` must exist, with mirrored networking:
  ```
  [wsl2]
  networkingMode=mirrored
  ```
  The comments in `setup.ps1` and `deploy/node1/docker-compose.yaml` assume mirrored mode. Nothing in the scripts turns it on. Setup also appends `instanceIdleTimeout=-1` and runs `wsl --shutdown`, once.
- Internet access for image pulls (`mcr.microsoft.com`, Docker Hub), `get.k3s.io`, and the Helm chart repositories.

The second machine:

- Linux with systemd, `curl`, `sudo`, and internet access to `get.k3s.io`.
- Its hostname is exactly `k3s-node2` (lowercase). k3s names a node after its hostname, and `deploy/caps.json` and the chart pin pods to that name.
- An SSH login from the PC to `<WORKER_USER>@<AGENT_IP>` with a key and no password prompt, and `sudo` without a password. Setup uses `ssh -o BatchMode=yes` and `sudo -n`.
- CPU and memory that match its entry in `deploy/caps.json` (section 3).
- It is not optional. The ingress, MetalLB, Prometheus and KEDA are pinned to `k3s-node2`, and the firewall rules name its address.

Ports on the PC that must be free: 6443, 5682, 5310, 8091, 8092, 1244, 27018, 5320, 5321, 8472 (UDP), 10250. Setup opens 6443, 5682, 5310, 8091, 27018, 5320, 5321, 10250 and 8472/UDP in the Hyper-V firewall, for the address of the second machine only.

Disk and memory:

- Setup refuses to build when the drive of `C:`, the drive that holds the WSL disk, or the drive of `%TEMP%` has less than 18 GB free. The Monitor's host check raises an alert below 35 GB. Both numbers are in `deploy/watch.ps1`.
- Container limits on the PC add up to 14784 MiB of memory and 13000 millicores (all from `deploy/caps.json`). These are ceilings, not reservations, but the node1 container asks Docker for 8 CPUs, so WSL needs `processors=8` or more in `.wslconfig`, or you lower `CAP_NODE1` in `caps.json`.
- The second machine's limits in `caps.json`: 5000 millicores, 10240 MiB.

## 3. What to change before the first run

### 3.1 Placeholders

Every address or name of the original network is a placeholder in angle brackets.

| Placeholder | What it is | What to put |
|---|---|---|
| `<HOST_IP>` | LAN address of the PC | The IPv4 address `ipconfig` shows for your network adapter. With mirrored networking, WSL shares it. |
| `<INGRESS_IP>` | The address of the app | One free address on your LAN, inside the MetalLB pool. |
| `<LB_RANGE_START>`, `<LB_RANGE_END>` | MetalLB pool | First and last free address, outside your router's DHCP range. Both are in `deploy/setup.ps1` line 45. A single address is fine: use the same value twice. |
| `<AGENT_IP>` | LAN address of `k3s-node2` | Its IPv4 address. Give it a fixed address (a router reservation). |
| `<WORKER_USER>` | SSH user on `k3s-node2` | The user from section 2. |
| `<POD_CIDR>` | Pod network of the cluster | The k3s default cluster CIDR (see the k3s docs) unless you changed it. |
| `<INGRESS_POD_CIDR>` | The pod slice that belongs to `k3s-node2` | Read it after the node has joined: `k3s kubectl get node k3s-node2 -o jsonpath="{.spec.podCIDR}"` in WSL. |
| `<DHCP_RANGE>` | Only in `docs/runbook.md` | Your router's DHCP range, or ignore. |

Note: `<INGRESS_POD_CIDR>` cannot be known before the cluster exists, and the chart needs it (`ingressPodNetwork missing` stops the render). Do it in two passes:

1. Fill every placeholder except `<INGRESS_POD_CIDR>`. Commit. Run `deploy\setup.ps1 -InfraOnly`. It builds the cluster and installs the charts, but not the app.
2. Read the slice with the command above. Put it in `deploy/app/values.yaml` line 9 and in `tests/smoke/check-66-chart.ps1`. Commit. Run `deploy\setup.ps1` for the app.

The README has a PowerShell script that replaces the placeholders in every text file of the clone. For pass one, delete the `<INGRESS_POD_CIDR>` line from its `$values` table. Run it after you have read this guide, because it also rewrites the placeholders written in this file.

### 3.2 Where the placeholders sit

| File | Line | What it means | What to put |
|---|---|---|---|
| `deploy/setup.ps1` | 22 `$ControlPlaneIp` | Address workers use to reach k3s | `<HOST_IP>` |
| `deploy/setup.ps1` | 24 `$WorkerUser` | SSH user on the second machine | `<WORKER_USER>` |
| `deploy/setup.ps1` | 27 `$IngressOrigin` | Where setup signs in to stop the Line | `http://<INGRESS_IP>` |
| `deploy/setup.ps1` | 29 `$WorkerIps` | Address of the second machine; also the firewall source | `@('<AGENT_IP>')` |
| `deploy/setup.ps1` | 45 `$LoadBalancerRange` | MetalLB pool, `first-last` | `<LB_RANGE_START>-<LB_RANGE_END>` |
| `deploy/app/values.yaml` | 5 `hostIp` | Where Monitor and Line are reached from the cluster | `<HOST_IP>` |
| `deploy/app/values.yaml` | 7 `podNetwork` | Pod network allowed through the ingress | `<POD_CIDR>` |
| `deploy/app/values.yaml` | 9 `ingressPodNetwork` | Trusted proxy range for the API and Live | `<INGRESS_POD_CIDR>` |
| `deploy/app/values.yaml` | 29 `ALLOWED_ORIGINS` | Origin the browser sends | `http://<INGRESS_IP>` |
| `deploy/node1/docker-compose.yaml` | 28, 30, 32, 40 | Published ports, k3s server URL, node external address | `<HOST_IP>` |
| `deploy/cosmos/docker-compose.yaml` | 13, 15, 17, 23 | Published ports, address the emulator announces | `<HOST_IP>` |
| `deploy/mongo/docker-compose.yaml` | 15 | Published port | `<HOST_IP>` |
| `deploy/servicebus/docker-compose.yaml` | 13, 15 | Published ports | `<HOST_IP>` |
| `deploy/monitor/docker-compose.yaml` | 23, 25 | Published port, `HOST_IP` | `<HOST_IP>` |
| `deploy/monitor/docker-compose.yaml` | 26 | `INGRESS_ORIGIN` | `http://<INGRESS_IP>` |
| `deploy/monitor/docker-compose.yaml` | 28 `TRUSTED_PROXIES` | The ingress reaches Monitor from the second machine | `<AGENT_IP>/32` |
| `deploy/line/docker-compose.yaml` | 18 | Published port | `<HOST_IP>` |
| `deploy/line/docker-compose.yaml` | 21, 24 | `API_URL`, `ALLOWED_ORIGINS` | `http://<INGRESS_IP>` |
| `deploy/line/docker-compose.yaml` | 26 `TRUSTED_PROXIES` | Same as Monitor | `<AGENT_IP>/32` |
| `app/web/src/config.ts` | 21 | Address the dashboard shows for the load balancer | `<INGRESS_IP>` |
| `app/web/vite.config.ts` | 4 | Dev-server proxy target | `http://<INGRESS_IP>` |
| `tests/e2e/support/env.ts` | 2 | Browser tests base URL | `http://<INGRESS_IP>` |
| `tests/smoke/lib.ps1` | 3, 14 | Smoke test addresses | `<HOST_IP>`, `<INGRESS_IP>` |
| `tests/smoke/check-66-chart.ps1` | 453, 518, 542 to 547 | Smoke test expectations | `<INGRESS_POD_CIDR>`, `<POD_CIDR>`, `<AGENT_IP>` |
| `docs/*.md`, other tests | various | Text and test data | Replaced by the same script; no hand edit |

Note: `tests/smoke/check-66-chart.ps1` line 460 has `<AGENT_IP>` glued to a few characters of text in front of it, in the pattern for `TRUSTED_PROXIES`. After the placeholders are replaced the pattern does not match, so that check fails. Open the line and make the pattern read `TRUSTED_PROXIES: ` followed by your address with its dots escaped, then `/32`.

### 3.3 Things tied to one machine, not placeholders

| File | Setting | What it means | What to do |
|---|---|---|---|
| `deploy/setup.ps1` | 21 `$Distro = 'Ubuntu-24.04'` | WSL distro name | Keep it, or change it here, in `deploy/watch.ps1` line 13 (`$WatchDistro`) and in `tests/smoke/lib.ps1` line 2. |
| `deploy/setup.ps1`, `deploy/node1/docker-compose.yaml` | `K3sVersion` `v1.36.5+k3s1`, image `rancher/k3s:v1.36.5-k3s1` | k3s version on every node | Keep. If you change one, change both and the k3s server in WSL. |
| `deploy/setup.ps1` | 25 `k3s-node1`, `deploy/caps.json`, the chart | Node names | Keep. The second machine's hostname must be `k3s-node2`. |
| `deploy/setup.ps1` | 28 `$LineOperator = 'ada'` | Demo user setup signs in as, to stop the Line | Keep. |
| `deploy/caps.json` | `nodes` | CPU (millicores) and memory (MiB) ceiling per node: `k3s-node1` 8000 / 8192, `k3s-node2` 5000 / 10240 | Set to what your hardware gives. The sum of all limits placed on a node must stay under its ceiling, and requests must fit the node's allocatable memory. |
| `deploy/caps.json` | `containers` | Limits of the Docker containers on the PC | Lower them if the PC is small. The numbers feed the `CAP_*` lines in the `.env` files. |
| `deploy/caps.json` | `pods`, `reserved` | Limits, requests, replica counts, node of every pod | Change only with the ceilings above. |
| `deploy/watch.ps1` | 9 `$BuildFreeGB = 18`, 11 `$AlarmFreeGB = 35` | Free disk needed to build, and the alert level | Set to what your disks allow. The file marks both as interim figures from the original machine. |
| `~\.wslconfig` | `processors`, `memory` | WSL size | `processors` at least 8 unless you lower `CAP_NODE1` in `caps.json`. |
| Task Scheduler | `BatchTracker-wsl-start`, `BatchTracker-watch` | Created by setup. The first starts WSL at logon. The second runs `deploy/watch.ps1` every minute as your user and writes a change line when a check fails. | Nothing to edit. They exist until you delete them (section 7). |
| `deploy/app/values.yaml` | `DEV_LOGIN_ENABLED: "true"`, `COOKIE_SECURE: "false"` | Demo sign-in with no password, cookie over plain HTTP | Keep for a home test. Anyone who can reach `<INGRESS_IP>` can sign in as an admin. Do not expose it. |

Check the limits without changing anything: `powershell -File deploy\setup.ps1 -CapsOnly`. On a machine with no cluster it checks the limits against each other only.

Note: the scripts do not install the k3s server in WSL. Setup runs `systemctl restart k3s` and `k3s kubectl` there and then rewrites `/etc/rancher/k3s/config.yaml`. The Monitor container runs as a non-root user and mounts `/etc/rancher/k3s/k3s.yaml`, so that file must be readable by others. Setup's config does not set that. From the code, a command that should work is, in WSL as root:

```
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION='v1.36.5+k3s1' K3S_KUBECONFIG_MODE=644 sh -s - server
```

Note: a missing `~\.wslconfig` stops setup with a "path not found" error, because it reads the file before it appends to it. Create the file first (section 2).

Note: the first `ssh` to the second machine has to be done by hand once, so the host key is known. Setup uses `BatchMode`, which refuses to ask.

## 4. The .env files

`.sample.env` in the repository root holds every variable, in one block for each folder. Copy a block into the file named in its header. Real `.env` files are ignored by git. The three `deploy/*/.env.example` files list the same names for the tests.

| File | Variables | Who fills them |
|---|---|---|
| `deploy/app/.env` | `JWT_PRIVATE_KEY`, `JWT_PUBLIC_KEY`, `INTERNAL_TOKEN`, `LINE_MACHINE_TOKEN`, `MONITOR_MACHINE_TOKEN`, `COSMOS_CONNECTION`, `MONGO_CONNECTION`, `SERVICEBUS_CONNECTION` | You, except `MONGO_CONNECTION`. |
| `deploy/mongo/.env` | `MONGO_PASSWORD`, `MONGO_MONITOR_PASSWORD`, `MONGO_MONITOR_CONNECTION` | Setup fills blanks. |
| `deploy/servicebus/.env` | `MSSQL_SA_PASSWORD` | You, or setup if the file does not exist. |

Other `.env` files (`cosmos`, `node1`, `line`, `monitor`) are written by setup: the `CAP_*` limits from `caps.json`, plus a few values it copies. Do not make them.

Important: setup makes `deploy/app/.env` and `deploy/servicebus/.env` only when the file is missing. A file that exists with blank values is not filled and stops setup. For these two, either do not create the file, or fill every value. (For `deploy/mongo/.env` blanks are fine.)

What each one is and how to make it. Run these in WSL, in the folder of the clone (`cd /mnt/c/path/to/clone`).

| Variable | What it is | Command |
|---|---|---|
| `JWT_PRIVATE_KEY`, `JWT_PUBLIC_KEY` | P-256 key pair that signs and checks the sign-in cookie, base64 of DER | the block below |
| `INTERNAL_TOKEN` | Secret between services | `openssl rand -hex 32` |
| `LINE_MACHINE_TOKEN` | Secret the Line signs in to the API with | `openssl rand -hex 32` |
| `MONITOR_MACHINE_TOKEN` | Secret the Monitor signs in with | `openssl rand -hex 32` |
| `COSMOS_CONNECTION` | Cosmos DB emulator. The key is the fixed one Microsoft publishes for the emulator. | `AccountEndpoint=https://<HOST_IP>:8091/;AccountKey=<COSMOS_EMULATOR_KEY>;DisableServerCertificateValidation=True;` |
| `SERVICEBUS_CONNECTION` | Service Bus emulator, fixed development key | `Endpoint=sb://<HOST_IP>:5682;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;` |
| `MONGO_CONNECTION` | Built by setup from `MONGO_PASSWORD`. Leave blank. | none |
| `MONGO_PASSWORD`, `MONGO_MONITOR_PASSWORD` | Passwords of the MongoDB users `admin` and `monitor` | `openssl rand -hex 24`, or leave blank |
| `MONGO_MONITOR_CONNECTION` | Built by setup. Leave blank. | none |
| `MSSQL_SA_PASSWORD` | SQL Server password under the Service Bus emulator. It must be strong. | `printf 'Zp1!%s' "$(openssl rand -hex 16)"` |

Copy the Cosmos emulator key from https://learn.microsoft.com/en-us/azure/cosmos-db/emulator .

Keys, tokens and the app file in one go (the same commands setup uses when the file is missing):

```
pem=$(openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt)
private=$(printf '%s\n' "$pem" | openssl pkcs8 -topk8 -nocrypt -outform DER | base64 -w0)
public=$(printf '%s\n' "$pem" | openssl pkey -pubout -outform DER | base64 -w0)
printf 'JWT_PRIVATE_KEY=%s\nJWT_PUBLIC_KEY=%s\nINTERNAL_TOKEN=%s\nLINE_MACHINE_TOKEN=%s\nMONITOR_MACHINE_TOKEN=%s\n' \
  "$private" "$public" "$(openssl rand -hex 32)" "$(openssl rand -hex 32)" "$(openssl rand -hex 32)" > deploy/app/.env
printf 'COSMOS_CONNECTION=%s\nSERVICEBUS_CONNECTION=%s\n' \
  'AccountEndpoint=https://<HOST_IP>:8091/;AccountKey=<COSMOS_EMULATOR_KEY>;DisableServerCertificateValidation=True;' \
  'Endpoint=sb://<HOST_IP>:5682;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;' >> deploy/app/.env
```

Shorter way, from the code: do not make `deploy/app/.env`. Run setup. It writes the five keys and tokens, fills `MONGO_CONNECTION`, then stops with `has no value for: COSMOS_CONNECTION, SERVICEBUS_CONNECTION`. Append those two lines (with your `<HOST_IP>`) to `deploy/app/.env` and run setup again.

A new JWT key ends every open session.

## 5. Deploy, step by step

1. Fill the placeholders (3.1, 3.2), the machine settings (3.3) and the `.env` files (4).
2. Check the limits: `powershell -NoProfile -ExecutionPolicy Bypass -File deploy\setup.ps1 -CapsOnly`. Expect `[OK] caps` for each node.
3. Commit in your local clone: `git add -A` then `git commit -m "local settings"`. Make sure no `.env` file is in the commit: `git status` must not list one.
4. Optional, to test the build alone: `powershell -NoProfile -ExecutionPolicy Bypass -File deploy\setup.ps1 -GateOnly`, in an elevated PowerShell. It builds and tests an export of the commit and changes nothing else. Failures show `[FAIL] gate: <step>` and a log path in `%TEMP%\batchtracker-gate-failure.log`.
5. Run the infrastructure pass: `powershell -NoProfile -ExecutionPolicy Bypass -File deploy\setup.ps1 -InfraOnly`. It stops the Line if one runs (skipped on a first run), sets the firewall rules, the scheduled tasks and the WSL settings, configures the control plane, joins `k3s-node2`, starts node1, starts the Service Bus, Cosmos and MongoDB containers, installs the four charts, then starts the Monitor.
6. Read the new pod slice for the ingress node (3.1), put it in `values.yaml` and the smoke test, and commit.
7. Run the full deploy: `powershell -NoProfile -ExecutionPolicy Bypass -File deploy\setup.ps1`. It builds the images, copies them to both nodes, installs the `batch-tracker` release, starts the Line, and starts the Monitor. A run that ends with exit code 0 writes the commit hash to `deploy\.release\COMMIT`.
8. Setup is safe to run again. An unchanged image keeps its tag, so its pods are not restarted.

Reading `terraform plan` before a later run. Setup applies from a release copy and keeps state in `deploy\.terraform-state`. This shows how the cluster differs from that state. Run it in WSL as root (`wsl -d Ubuntu-24.04 -u root`); replace the Windows path:

```
root='C:\path\to\clone\deploy'
cd "$(wslpath -a "$root\\.release\\deploy\\terraform")"
state="$(wslpath -a "$root\\.terraform-state")"
export TF_DATA_DIR="$state/.terraform"
terraform plan -input=false -state="$state/terraform.tfstate" \
  -var=generated_dir="$(wslpath -a "$root\\.generated")" \
  -var=load_balancer_range=<LB_RANGE_START>-<LB_RANGE_END>
```

Read every line that starts with `-`. It shows the plan for the release that was deployed last, not for the commit you are about to deploy.

### Check that it worked

- Browser: open `http://<INGRESS_IP>/`. It should show the sign-in page. Then `http://<INGRESS_IP>/dashboard` after you sign in (section 6).
- Monitor, from WSL: `curl -s http://127.0.0.1:5320/health`. It answers 200 when healthy and 503 otherwise. `curl -s http://127.0.0.1:5320/alive` is the short check.
- Nodes, in WSL: `k3s kubectl get nodes` shows `k3s-node1` and `k3s-node2` as Ready. `k3s kubectl get pods -A` shows the pods.
- Smoke, elevated PowerShell, from the clone: `powershell -NoProfile -ExecutionPolicy Bypass -File tests\smoke\run.ps1`. Exit code 0 means every check passed. It needs the `.NET` SDK, because it builds a small probe. It refuses to start for up to 15 minutes after a run that sent refused credentials. Expect the check from the Note in 3.2 to fail until you fix its pattern.

## 6. Use the app

1. Open `http://<INGRESS_IP>/signin`. Pick a demo user. Use `ada` for everything below: it holds all permissions. The others are `olivia`, `ian` and `viewer`, with fewer rights.
2. Batch Tracker tracks batches through Created, Dose 1, Dose 2, Inspected, Shipped and Closed. A failed inspection sends a batch to Rejected.
3. The Line is a program that makes batches for you and walks them through the steps. Open `/dashboard` as `ada`. The Line panel has a rate control, a slider from 0 to 35 batches per second in steps of 0.2. Set a rate and press Start line. Drag the slider to change the rate while it runs. Stop line stops it. The server refuses a rate above 35.
4. While the Line runs, the dashboard map shows the pods, and KEDA adds API and Worker pods as the request rate and the number of new batches rise. Start low.

## 7. Stop and clean up

Everything below is by name. Do the parts you need. Run the Windows lines in an elevated PowerShell and the others in WSL as root.

1. Stop the Line first, from the dashboard (Stop line). Wait for the pending count to reach 0.
2. Containers on the PC:
   ```
   docker stop batchtracker-line batchtracker-monitor batchtracker-mongo batchtracker-servicebus batchtracker-servicebus-sql batchtracker-cosmos batchtracker-node1
   docker rm batchtracker-line batchtracker-monitor batchtracker-mongo batchtracker-servicebus batchtracker-servicebus-sql batchtracker-cosmos batchtracker-node1
   ```
3. Data volumes (this deletes the stored data). List first: `docker volume ls --filter name=batchtracker`. Then remove what the list shows, for example `batchtracker-node1`, `batchtracker-node1-identity`, and the Cosmos and Mongo volumes.
4. Helm releases, before you stop the cluster:
   ```
   export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
   helm uninstall batch-tracker -n batch-tracker
   helm uninstall keda -n keda
   helm uninstall prometheus -n prometheus
   helm uninstall ingress-nginx -n ingress-nginx
   helm uninstall metallb -n metallb-system
   ```
   Terraform state no longer matches after this. Delete `deploy\.terraform-state` before you install again.
5. The second machine: `ssh <WORKER_USER>@<AGENT_IP> "sudo /usr/local/bin/k3s-agent-uninstall.sh"`.
6. The control plane in WSL, if you installed it with the official installer: `/usr/local/bin/k3s-uninstall.sh`.
7. Scheduled tasks:
   ```
   schtasks /delete /tn BatchTracker-watch /f
   schtasks /delete /tn BatchTracker-wsl-start /f
   ```
8. WSL report timer, in WSL:
   ```
   systemctl disable --now batchtracker-wsl-report.timer
   rm /etc/systemd/system/batchtracker-wsl-report.timer /etc/systemd/system/batchtracker-wsl-report.service /usr/local/bin/batchtracker-wsl-report
   ```
9. Firewall rules, elevated PowerShell:
   ```
   'k3s-api','servicebus-amqp','servicebus-admin','cosmos-gateway','mongo','monitor','line','flannel-vxlan','kubelet' | ForEach-Object { Remove-NetFirewallHyperVRule -Name "BatchTracker-$_" }
   ```
10. `~\.wslconfig`: remove the `[general]` and `instanceIdleTimeout=-1` lines that setup added.
11. Files in the clone, all ignored by git: `deploy\.release`, `deploy\.generated`, `deploy\.changes`, `deploy\.terraform-state`, and the `.env` files you made.
12. Images: `docker image ls "batch-tracker/*"`, then `docker image rm` the tags you see.

## 8. Common failures

Each one comes from a message in the scripts or a note in `docs/runbook.md`.

| What you see | What it means | What to do |
|---|---|---|
| `Run this script from an elevated PowerShell.` | Not run as administrator. | Open PowerShell as administrator. |
| `Not a commit: HEAD` | No commit in the clone. | Commit first. |
| `Build refused: <drive> has N GB free, under 18 GB` | Not enough disk for the build. | Free space, or lower `$BuildFreeGB` in `deploy/watch.ps1`. |
| `<file> has no value for: ...` or `Missing <file>` | A key in `deploy/app/.env` is blank or missing. | See section 4. |
| `Gate stopped at: <step>. Nothing outside the export changed.` | Build, tests, lint or an image build failed. | Read `%TEMP%\batchtracker-gate-failure.log`. |
| `caps: ... exceed cap ...` or `caps: <node> memory requests ... exceed allocatable ...` | `caps.json` does not fit the node. | Edit `deploy/caps.json`, commit, run again. |
| `caps: <name> on unknown node ...` | A node name in `caps.json` does not exist. | The second machine's hostname must be `k3s-node2`. |
| `Worker step failed on <ip>.` | SSH or `sudo -n` failed. | Test `ssh -o BatchMode=yes <WORKER_USER>@<AGENT_IP> "sudo -n true"` by hand. |
| `Join token not found.` or `Control plane step failed.` | The k3s server is not running as the `k3s` service in WSL. | See the Note in 3.3. |
| `Image copy failed` or `Image import failed` | `scp` or `k3s ctr` failed on the second machine. | Check SSH and free disk there. |
| `Service Bus emulator not healthy.`, `Cosmos emulator not healthy.`, `MongoDB did not answer a ping.` | A container did not start. | `docker logs batchtracker-servicebus`, `batchtracker-cosmos`, `batchtracker-mongo`. |
| `ingressPodNetwork missing`, `hostIp missing`, `podNetwork missing` | A value in `deploy/app/values.yaml` is empty. | Fill it (3.1). |
| `TRUSTED_PROXIES unset on: ...` | A compose file or the chart has no trusted range. | Check `<AGENT_IP>/32` in the Line and Monitor compose files and `ingressPodNetwork`. |
| `Pending stuck at N for 5 minutes.` | Setup waits for the queue to drain before it touches the Service Bus, and it does not move. | Check the Dispatcher and Service Bus. |
| Pods wait with a LoadBalancer Service `<pending>` | No MetalLB pool. | Set `$LoadBalancerRange` (line 45). |
| `the last run's refused credentials still count. Run again in N min.` | The smoke test waits for the failure count to clear. | Wait. |
| The ingress answers, but the Line or the Monitor does not | Hyper-V firewall rules. | Setup rewrites them on every run. List them with `Get-NetFirewallHyperVRule -Name "BatchTracker-*"`. |
| After a PC reboot nothing runs | WSL stays down until something starts it. | The task `BatchTracker-wsl-start` does it at logon. |
| Roll back the app | A bad release. | `helm history batch-tracker -n batch-tracker`, then `helm rollback batch-tracker <revision> -n batch-tracker`. |
