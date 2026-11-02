# k3s-demo: Batch Tracker on k3s

## Purpose

This is a working example for people who want to learn how a real multi-service app runs on Kubernetes, using k3s, Helm and Terraform. It is here to be read, run on your own machines and taken apart.

The app is Batch Tracker. It tracks batches of animal-health product. A batch moves through Created, Dose 1, Dose 2, Inspected, Shipped and Closed, and a failed inspection sends it to Rejected. Each step is queued as an event and gets a compliance record.

What you can learn from the repo:

- How one app splits into several services (API, Worker, Dispatcher, Live, Line) and a React web app, and how one Helm chart, `deploy/app/`, deploys them.
- How Terraform installs MetalLB, ingress-nginx, Prometheus and KEDA as Helm releases (`deploy/terraform/`).
- How KEDA scales the API and Worker pods from request rate and from new batches (`deploy/app/templates/scaling.yaml`).
- How a two-node cluster keeps pods on chosen nodes, and how `deploy/caps.json` sets CPU and memory limits in one place.
- How an outbox and a dispatcher with leader election pass events to a queue (`app/src/BatchTracker.Application/Dispatching/`).
- How the code is tested: unit, architecture, integration, smoke and browser tests, with each requirement in `docs/requirements.md` tied to a test level.
- How a monitor (`deploy/monitor/`) checks the cluster and the app.

A small animal-health batch tracker, built to learn how a real app runs on Kubernetes. A batch moves through Dose 1, Dose 2, Inspection, shipment and close. The app is .NET 9 with a React front end. It runs on a two-node k3s cluster that you build on your own machines, with Helm charts, Terraform and a monitor that watches the whole thing.

Everything here is a learning project. Read the code, run it at home, break it.

## What you need

- A Windows 11 PC with Hyper-V, an elevated PowerShell, and git.
- WSL 2 with an Ubuntu 24.04 distro. Inside it: Docker Engine, Helm and Terraform.
- .NET 9 SDK and Node.js on the Windows side. The deploy gate builds and tests the code before it ships.
- Optional second machine on the same network for the second node. It needs an SSH login with a key and passwordless sudo.
- A free block of addresses on your LAN, outside your router's DHCP range, for MetalLB.

## Layout

- `app/` the .NET services (API, Worker, Dispatcher, Live, Line) and the React web app in `app/web/`.
- `deploy/` Docker compose files, the Helm chart for the app in `deploy/app/`, values for MetalLB, ingress-nginx, Prometheus and KEDA in `deploy/cluster/`, the monitor, and `deploy/terraform/`.
- `tests/` smoke checks, monitor tests and browser tests.
- `docs/` architecture notes, the requirement list and a test matrix.

## Set the placeholders

Set your own values, see `setup.md`.

Addresses, network ranges and the worker SSH user are placeholders in angle brackets. There is one way to set them: the script below replaces each placeholder in every text file of your local clone. Do it in your clone only and never push the filled-in version.

| Placeholder | What to put there |
|---|---|
| `<HOST_IP>` | LAN address of the Windows PC that runs WSL, Docker and the control plane |
| `<INGRESS_IP>` | One free LAN address that MetalLB gives to the ingress |
| `<LB_RANGE_START>`, `<LB_RANGE_END>` | First and last address of the MetalLB pool. It must contain `<INGRESS_IP>` |
| `<AGENT_IP>` | LAN address of the second node |
| `<WORKER_USER>` | SSH user on the second node |
| `<POD_CIDR>` | Pod network of your k3s cluster. Use the k3s default unless you changed it |
| `<INGRESS_POD_CIDR>` | The /24 slice of the pod network that belongs to the node running the ingress. Read it with `kubectl get node <name> -o jsonpath="{.spec.podCIDR}"` |
| `<DHCP_RANGE>` | Only in `docs/runbook.md`. Replace for tidiness or ignore |

```powershell
$values = @{
  '<HOST_IP>'          = 'your value'
  '<INGRESS_IP>'       = 'your value'
  '<LB_RANGE_START>'   = 'your value'
  '<LB_RANGE_END>'     = 'your value'
  '<AGENT_IP>'         = 'your value'
  '<WORKER_USER>'      = 'your value'
  '<POD_CIDR>'         = 'your value'
  '<INGRESS_POD_CIDR>' = 'your value'
}
Get-ChildItem -Recurse -File |
  Where-Object { $_.FullName -notmatch '\\(\.git|node_modules|\.terraform)\\' -and $_.Name -ne 'package-lock.json' } |
  ForEach-Object {
    $old = [IO.File]::ReadAllText($_.FullName); $new = $old
    foreach ($k in $values.Keys) { $new = $new.Replace($k, $values[$k]) }
    if ($new -ne $old) { [IO.File]::WriteAllText($_.FullName, $new) }
  }
```

The node names `k3s-node1` and `k3s-node2` are plain names the scripts use. Keep them.

## Secrets

Real values live only in untracked `.env` files. The `.env.example` files list the variable names and no values:

- `deploy/app/.env.example`: JWT keys, internal and machine tokens, and the Cosmos DB, MongoDB and Service Bus connection strings.
- `deploy/mongo/.env.example`: MongoDB passwords.
- `deploy/servicebus/.env.example`: the SQL password the Service Bus emulator needs.

Copy each to `.env` next to it. For keys, tokens and passwords you can leave the value blank and `deploy/setup.ps1` generates it. Connection strings for the emulators point at your own machine.

The Cosmos DB and Service Bus emulator keys that appear in some tests are the fixed keys Microsoft publishes for its local emulators. They protect nothing.

## Deploy with Terraform and Helm

1. Fill in the placeholders as above.
2. Make the `.env` files from the examples.
3. Commit in your local clone. Setup deploys a commit (default `HEAD`), never the working tree.
4. In an elevated PowerShell run `deploy\setup.ps1`.

What setup does, in order: builds and tests an export of the commit, starts k3s (the main node runs as a Docker container in WSL, the second node joins over SSH), builds the images and copies them to the nodes, then runs Terraform from `deploy/terraform/`. Terraform installs MetalLB, ingress-nginx, Prometheus and KEDA as Helm releases, one at a time, and creates the MetalLB address pool. Last, the app itself goes in as the Helm release `batch-tracker` from `deploy/app/`.

Useful switches: `-InfraOnly` skips the app images and the chart, `-CapsOnly` only checks `deploy/caps.json` (the CPU and memory limits), `-GateOnly` stops after the build and test gate.

When it ends, open `http://<INGRESS_IP>` in a browser (use your real value).

To check the install later, run `tests\smoke\run.ps1` from an elevated PowerShell.

## Notes

- Terraform state and generated secrets stay out of git through `.gitignore`.
