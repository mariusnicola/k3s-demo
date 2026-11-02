# Runbook - how to run this project


## 1. Project bootstrap
- App in `app/`, design in `docs/architecture/`. Run steps added as they are built.

## 2. Operational tasks
- Set up infrastructure and app, safe to re-run, elevated PowerShell: `deploy\setup.ps1`. Infrastructure only: `-InfraOnly`. Check `deploy\caps.json`, change nothing: `-CapsOnly`.
- Deploy: commit, then `deploy\setup.ps1`, default `-Commit HEAD`. Setup exports the commit with `git archive`, runs the gate on the export, then copies it to `deploy\.release`, the only source of images, chart, compose files and `caps.json`. The working tree is never read. Gate, in order: dotnet build, unit tests, architecture tests, monitor tests, every Docker image built to cache only, npm ci, tsc, lint, web tests, production build. The real run then stops the Line and waits for no pending event, before any other step. A gate failure stops before anything outside the export changes. `deploy\.release\COMMIT` names the last commit whose deploy finished; setup writes it after exit code 0 of the cluster steps, and `-InfraOnly` leaves it. An unchanged image keeps its tag, so its pods stay up. `-GateOnly` stops after the gate. Secrets and generated values stay in `deploy\<service>\.env` and `deploy\.generated`.
- Charts, MetalLB address pool and the `batch-tracker` namespace: Terraform in `deploy\terraform`, run by `deploy\setup.ps1` one release at a time with `terraform apply -target`, the rollout check after each chart. Chart versions and repositories live only in `deploy\terraform\main.tf`. State and provider cache: `deploy\.terraform-state`, gitignored, never commit. Adopt a cluster that already runs the releases: `terraform import helm_release.<name> <namespace>/<name>` for each release, `kubernetes_namespace_v1.batch-tracker batch-tracker`, and both MetalLB objects as `apiVersion=metallb.io/v1beta1,kind=<IPAddressPool|L2Advertisement>,namespace=metallb-system,name=batchtracker`; the first apply after an import upgrades each release once with unchanged values.
- Stays in `deploy\setup.ps1`: image builds and copies, k3s, `schtasks`, the Secrets (piped from `deploy\app\.env`, so no secret reaches Terraform state), the metrics-server patch, the rollout checks.
- Change any limit: edit `deploy\caps.json`, commit, run setup. An uncommitted edit has no effect: the monitor mounts the deployed copy. Why each limit: `docs/architecture/monitoring.md`.
- Roll the app back: `helm history batch-tracker -n batch-tracker`, `helm rollback batch-tracker <revision> -n batch-tracker`. Remove it: `helm uninstall batch-tracker -n batch-tracker`, `docker compose down` in deploy/line. Terraform then sees a different revision; the next setup run upgrades it again from the committed chart.
- Run the nightly job now: `k3s kubectl create job nightly-now --from=cronjob/nightly -n batch-tracker`.
- MetalLB address pool: `$LoadBalancerRange` in `deploy\setup.ps1`, must stay outside the DHCP pool <DHCP_RANGE>.
- Client addresses: the ingress Service has `externalTrafficPolicy: Local`, so apps see the real LAN address. The controller stays pinned to the node in `caps.json`; the address is announced only from the node holding it.
- After a rollout, from a LAN machine that is not k3s-node2: `for i in 1 2 3 4 5; do curl -s -o /dev/null -w '%{http_code} %{time_total}\n' -H "X-Forwarded-For: 203.0.113.$i" http://<HOST_IP>:5321/line; done`. Five 401, the last three held about 1, 2 and 4 s. All five fast means the Line believes that machine's header. The same count stands for that machine for 15 minutes.
- Service Bus queue added to deploy\servicebus\config.json: `docker compose restart emulator` in deploy/servicebus. Emulator reads it at start.
- Show a script's work on the dashboard: dot-source `deploy\changes.ps1`, then `Start-Operation`, `Set-OperationStep`, `Write-Change`, `Stop-Operation`. Setup already does.
- Smoke test infrastructure, elevated PowerShell: `tests\smoke\run.ps1`. Exit 0 = all passed. It refuses to start within 15 minutes of the last run's 401 rows, because refused credentials count until 15 quiet minutes pass. New area = new `tests\smoke\check-*.ps1` file.

## 3. Standing / permanent approvals
- None.

## 4. Dev process
- Model/effort: global default, no project override.

## 5. DBs / services / ports / paths
<!-- Only THIS project's OWN db names, ports, service names, data paths go in the table; credentials by location reference only, never a value. -->
| service | how to reach | credentials location | notes |
|---|---|---|---|
| k3s API | <HOST_IP>:6443 | /etc/rancher/k3s/k3s.yaml in WSL | control plane in WSL Ubuntu-24.04 |
| Service Bus emulator | AMQP <HOST_IP>:5682, admin and health :5310 | emulator's fixed connection string | Docker project `batchtracker` |
| Service Bus SQL | internal to Docker project | deploy/servicebus/.env | never commit `.env` |
| MongoDB | 127.0.0.1:27018 and <HOST_IP>:27018, password in `deploy/mongo/.env`, generated once by setup; same file holds user `monitor` (readWrite on database `monitor` only) and its connection | |
| Cosmos emulator | gateway https://<HOST_IP>:8091, ready :8092/ready, Data Explorer :1244 | emulator's published well-known key | Docker project `batchtracker-cosmos`, data on volume `cosmos-data`, no authentication enforced |
| Monitor | from WSL: http://127.0.0.1:5320/health, 200 or 503. Browser: socket ws://<INGRESS_IP>/ws only. `docker logs batchtracker-monitor` | mounts /etc/rancher/k3s/k3s.yaml read-only | Docker project `batchtracker-monitor`, checks every 30 s, reads deploy/caps.json |
| MetalLB, ingress-nginx, Prometheus, KEDA | Helm releases, namespaces `metallb-system`, `ingress-nginx`, `prometheus`, `keda` | kubeconfig | values in deploy/cluster, limits and node in deploy/caps.json, versions in deploy/terraform/main.tf |
| Batch Tracker | http://<INGRESS_IP>, Helm release `batch-tracker`, namespace `batch-tracker` | deploy/app/.env (key names in deploy/app/.env.example), one Kubernetes Secret per service, `batch-tracker-<service>` | never commit `.env`; a missing file gets generated JWT keys and tokens, then setup stops naming COSMOS_CONNECTION and SERVICEBUS_CONNECTION; a new JWT key ends open sessions; images imported to both workers, pull policy Never |
| Line | browser: http://<INGRESS_IP>/line. From WSL: http://127.0.0.1:5321/healthz. `docker logs batchtracker-line` | JWT public key and machine token in deploy/line/.env, copied by setup | Docker project `batchtracker-line` |

