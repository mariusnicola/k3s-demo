# Architecture overview

Scope and owner decisions: `docs/decisions/0002-batch-tracker-scope.md`.

## Services
| Service | Kind | Where | Replicas |
|---|---|---|---|
| api | Deployment | k3s-node1 | KEDA 1-2 |
| worker | Deployment | k3s-node1 | KEDA 1-5 |
| worker-node2 | Deployment | k3s-node2 | KEDA 1-5 |
| dispatcher | Deployment | k3s-node1 | 2 |
| dispatcher-node2 | Deployment, Recreate | k3s-node2 | 1, same Lease |
| live | Deployment | k3s-node1 | 1 |
| web | Deployment, nginx | k3s-node1 | 1 |
| nightly | CronJob, curl to worker | k3s-node1 | 02:00 |
| line | Docker in WSL | this PC | 1 |
| monitor | Docker in WSL | this PC | 1 |

Rule: on node2 the app runs only worker-node2 and dispatcher-node2. Every other app pod has a node selector for k3s-node1.

## Layout
```
app/BatchTracker.sln
app/src/BatchTracker.Domain          states, rules, hash chain, no dependencies
app/src/BatchTracker.Application     use cases, ports
app/src/BatchTracker.Infrastructure  Cosmos, Service Bus, hop client
app/src/BatchTracker.Api             minimal API
app/src/BatchTracker.Worker          HTTP event handler, nightly endpoint
app/src/BatchTracker.Dispatcher      leader election, outbox, delivery, dead letters
app/src/BatchTracker.Live            hop hub, WebSocket /ws/hops
app/src/BatchTracker.Line            production line, outside the cluster
app/tests/*.UnitTests, *.ArchTests, *.IntegrationTests
app/web                              React, design system in src/ds
deploy/caps.json                     every cap, one file
deploy/app/                          Helm chart `batch-tracker`
deploy/terraform/                    Helm releases of MetalLB, ingress-nginx, Prometheus, KEDA and the app, the MetalLB pool, the app namespace
tests/e2e                            Playwright
tests/chaos                          failover, restart, load scripts
```

## Network
- Ingress `http://<INGRESS_IP>`: `/` web, `/api` api, `/ws/hops` and `/live/logs` live, `/ws` monitor, `/line` line.
- Worker scaling: `sum(rate(bt_batches_created_total[20s]))` against 6.7 batches/s a pod, on `worker` and `worker-node2`, min 1, max 5 each, no CPU trigger. Below 3 batches/s that is 2 Workers, over 30 it is 10, and a burst of requests with no new batches adds none. Metrics scrape every 5 s, the HPA syncs every 10 s, and scale-down waits 10 s.
- API scaling: nginx request rate over 20 s, two 10 s scrapes, target 20, with a CPU trigger, and no scale-down wait. Metrics-server resolves CPU every 15 s, and the HPA follows the higher of the two. Prometheus has a job of its own for nginx; every other job stays on the global 15 s, so 120 days still fit the 38 GB.
- Worker route: Host `worker.batch-tracker.local` through the ingress Service, so calls spread per request and count in `nginx_ingress_controller_requests`.
- Monitor and Line run in Docker on this PC. The browser reaches them only through the ingress, same origin: `/ws` Exact to Monitor, `/line` Prefix to Line. No other path of either is exposed. Windows cannot reach WSL services on its own LAN address in mirrored networking, so `<HOST_IP>` is never a browser address.
- Inside the chart, Services without a selector and Endpoints point at `hostIp`: Monitor 5320, Line 5321.
- Hyper-V firewall rules on this PC: 5320, 5321, for the ingress controller. Every rule allows node2's address alone, <AGENT_IP>, and setup rewrites rules that already exist.

## Cluster provisioning
- `deploy/setup.ps1` builds images, copies them to the nodes, installs k3s and makes the `schtasks` tasks. It hands the Helm releases, the MetalLB pool and the `batch-tracker` namespace to Terraform (`deploy/terraform`, Helm and Kubernetes providers, versions pinned in `versions.tf`, lock file committed).
- Setup runs `terraform apply -target` once per release, in the order MetalLB, ingress-nginx, Prometheus, KEDA, pool, namespace, app, and checks the rollout after each chart, so the order and the checks are those of the Helm calls they replace.
- Values reach Terraform as the JSON files setup writes to `deploy/.generated`, after the committed `deploy/cluster/<chart>.yaml` where one exists.
- State and the provider cache sit in `deploy/.terraform-state`, outside the release copy that every deploy replaces. Both are gitignored.
- The Kubernetes Secrets stay outside Terraform: setup pipes them from `deploy/app/.env` so no secret value enters Terraform state.
