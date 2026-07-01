# Privátní Kubernetes lab — Rancher + k3s + GitOps + IaC + Observabilita

Hands-on lab, který na jednom Windows stroji (16 GB RAM) staví **věrnou zmenšeninu privátní on-prem
kontejnerové platformy**: dvě HA Kubernetes prostředí (staging + produkce), centrální správu přes
**Rancher**, GitOps přes **ArgoCD** (včetně multi-cluster promotion staging → prod), Infrastructure
as Code (**Terraform** + **Ansible**) a observabilitu (**Prometheus + Grafana + kube-state-metrics**).

Slouží jako podklad k pohovoru na DevOps / Platform Engineer pozici — pokrývá Kubernetes, Docker,
Rancher/OpenShift-like správu, RBAC, CI/CD (ArgoCD), IaC, Ansible, observabilitu, REST API i reálný
troubleshooting.

---

## 1. Zadání a mapování

Původní zadání (privátní virtualizace): *3 compute nody, storage, 2 switche, virtuální Kubernetes
klastr se staging a produkčním prostředím, minimálně 3 kontrolní (control-plane) nody, oddělení rolí,
správa přes Rancher.*

Na reálném železe by to byl **Proxmox VE / SUSE Harvester (KVM) + RKE2 + Longhorn + Rancher**, 3 fyzické
nody a 2 switche v LACP/MLAG. Tento lab používá **identické nástroje a koncepty**, jen nody běží jako
kontejnery přes **k3d** (k3s v Dockeru) místo 3 fyzických strojů — vše zdarma a open-source.

| Vrstva | Reálné železo | Tento lab |
|---|---|---|
| Virtualizace | Proxmox / Harvester (KVM) | Docker Desktop (WSL2) |
| Kubernetes distribuce | RKE2 | k3s (přes k3d) |
| Multi-cluster + RBAC | Rancher | Rancher (kontejner) |
| Persistent storage | Longhorn / Ceph | local-path (k3s default) |
| Síť / redundance | 2× switch, LACP, MLAG, VLAN | Docker bridge sítě |

---

## 2. Architektura

```
                         ┌─────────────────────────────────────────────┐
                         │  Rancher (kontejner rancher-mgmt)            │
                         │  https://localhost:8443  — cluster "local"  │
                         │  multi-cluster management + RBAC            │
                         └───────────────┬─────────────┬───────────────┘
                             spravuje    │             │  spravuje
                    ┌────────────────────▼───┐   ┌─────▼──────────────────┐
                    │  STAGING cluster (k3d) │   │  PROD cluster (k3d)    │
                    │  3× control-plane,etcd │   │  3× control-plane,etcd │
                    │  1× worker             │   │  1× worker             │
                    │                        │   │                        │
                    │  • demo/web (1 rep.)   │   │  • demo/web (3 rep.)   │
                    │  • ArgoCD  ────────────┼───┼──► guestbook-prod      │
                    │  • guestbook (HEAD)    │   │    (promotion, pinned) │
                    │  • terraform-demo      │   │  • monitoring:         │
                    │  • ansible-demo        │   │    Prometheus+Grafana  │
                    │  • RBAC: dev = write   │   │    +kube-state-metrics │
                    └────────────────────────┘   │  • RBAC: dev = read    │
                                                  └────────────────────────┘
```

- **3 clustery**: `local` (Rancher vlastní k3s) + `staging` + `prod`.
- **HA control plane**: každý cluster má **3 nody s rolí `control-plane,etcd`** (liché číslo kvůli etcd quorum) + 1 worker. Aplikace běží na workerech, control-plane odděleně.
- **GitOps**: ArgoCD běží na stagingu a spravuje **oba** clustery — staging (in-cluster, sleduje `HEAD`) i prod (registrovaný external cluster, napevno připnutá ověřená revize = řízené promotion).
- **Observabilita**: Prometheus scrapuje apiservery, node cadvisor, pody a kube-state-metrics; Grafana s naprovisionovaným dashboardem.

---

## 3. Předpoklady

| Nástroj | Verze v labu | Instalace |
|---|---|---|
| Docker Desktop | 29.5.3 (WSL2 backend) | ruční |
| k3d | v5.9.0 | `winget install k3d.k3d` |
| Helm | v4.2.2 | `winget install Helm.Helm` |
| Terraform | 1.15.7 | `winget install Hashicorp.Terraform` |
| kubectl | součást Docker Desktop | — |

`.wslconfig` (kvůli monitoringu navýšená paměť; host má jen 16 GB):

```ini
[wsl2]
memory=10GB
processors=6
swap=4GB
```

Po změně `.wslconfig`: `wsl --shutdown` a restart Docker Desktopu (**pozor** — viz gotcha §6.1).

---

## 4. Instalace krok za krokem

> Pozn.: `registries.yaml` (viz §6.2) je klíčový kvůli AVG — bez něj k3s uzly nestáhnou image.
> Ideálně vytvářet clustery rovnou s `--registry-config registries.yaml`.

### 4.1 Nástroje
```powershell
winget install k3d.k3d Helm.Helm Hashicorp.Terraform --source winget `
  --accept-package-agreements --accept-source-agreements
```

### 4.2 Clustery (3 control-plane + worker)
```powershell
k3d cluster create staging --servers 3 --agents 1 --registry-config registries.yaml --wait
k3d cluster create prod    --servers 3 --agents 1 --registry-config registries.yaml --wait
# ověření
docker exec k3d-staging-server-0 kubectl get nodes   # 3× control-plane,etcd + 1 worker
```

Kubeconfig pro host (self-signed → insecure):
```powershell
k3d kubeconfig get staging  # + odstranit certificate-authority-data, přidat insecure-skip-tls-verify: true
```

### 4.3 Rancher (management + RBAC)
```powershell
docker run -d --privileged --restart=unless-stopped --name rancher-mgmt `
  -p 8443:443 -p 8081:80 -e CATTLE_BOOTSTRAP_PASSWORD=<RANCHER_PASSWORD> rancher/rancher:latest
# UI: https://localhost:8443  (admin / <RANCHER_PASSWORD>)
```

Import clusterů do Rancheru (přes REST API): login → nastavit `server-url` →
`POST /v3/clusters` (import) → `POST /v3/clusterregistrationtokens` → aplikovat registrační manifest
na cílový cluster. Kvůli AVG viz gotcha §6.3 (server-url = `https://rancher-mgmt:443`, Rancher
připojen do k3d sítí).

### 4.4 Aplikace do staging/prod + RBAC
```powershell
kubectl --kubeconfig staging.yaml apply -f manifests/app-staging.yaml   # 1 replika
kubectl --kubeconfig prod.yaml    apply -f manifests/app-prod.yaml      # 3 repliky (redundance)
kubectl --kubeconfig staging.yaml apply -f manifests/rbac-staging.yaml  # dev = write v ns team-a
kubectl --kubeconfig prod.yaml    apply -f manifests/rbac-prod.yaml     # dev = read-only (view)
# ověření RBAC
kubectl --kubeconfig staging.yaml auth can-i create deployments --as=dev -n team-a   # yes
kubectl --kubeconfig prod.yaml    auth can-i create deployments --as=dev -n demo      # no
```

### 4.5 ArgoCD (GitOps)
```powershell
kubectl -n argocd create namespace argocd
kubectl -n argocd apply -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml
# redis image z public.ecr.aws (mimo skip-list) → přesměrovat na docker.io (gotcha §6.4):
kubectl -n argocd set image deployment/argocd-redis redis=redis:8.2.3-alpine
# admin heslo:
kubectl -n argocd get secret argocd-initial-admin-secret -o jsonpath='{.data.password}' | base64 -d
# repo insecure kvůli AVG (go-git ignoruje GIT_SSL_NO_VERIFY, gotcha §6.5):
kubectl apply -f manifests/argocd-repo-insecure.yaml
kubectl apply -f manifests/argocd-app-guestbook.yaml   # Application -> staging (HEAD, auto-sync)
```

### 4.6 Multi-cluster + promotion staging → prod
```powershell
# 1) v prod: SA argocd-manager s cluster-admin + token
kubectl --kubeconfig prod.yaml apply -f manifests/argocd-prod-sa.yaml
# 2) zpřístupnit prod API ArgoCD podům na stagingu (jiná Docker síť)
docker network connect k3d-staging k3d-prod-serverlb
# 3) registrovat prod jako cluster v ArgoCD (secret s tokenem + insecure TLS)
kubectl --kubeconfig staging.yaml apply -f manifests/argocd-cluster-prod.yaml
# 4) Application pro prod: napevno připnutá revize ověřená ve stagingu = promotion
kubectl --kubeconfig staging.yaml apply -f manifests/argocd-app-guestbook-prod.yaml
# 5) promotion = manuální sync (vědomý krok)
kubectl -n argocd patch application guestbook-prod --type merge `
  -p '{"operation":{"sync":{"revision":"<SHA>","syncOptions":["CreateNamespace=true"]}}}'
```

### 4.7 Observabilita (Prometheus + Grafana + kube-state-metrics) — na prod
```powershell
kubectl --kubeconfig prod.yaml apply -f manifests/monitoring-prod.yaml       # Prometheus + Grafana
kubectl --kubeconfig prod.yaml apply -f manifests/kube-state-metrics.yaml     # cluster-state metriky
kubectl --kubeconfig prod.yaml apply -f manifests/grafana-dashboards.yaml     # dashboard provisioning
# Grafana: admin / <GRAFANA_PASSWORD>, dashboard "Kubernetes - Lab Overview (prod)"
```

### 4.8 Terraform (IaC) — přes Linux kontejner (gotcha §6.6)
```powershell
# provider stažený mimo AVG do lokálního filesystem mirroru, terraform běží v kontejneru
docker run --rm --network k3d-staging `
  -v "<tfc>:/work" -v "<tfmirror>:/mirror" -e TF_CLI_CONFIG_FILE=/mirror/terraformrc `
  -w /work hashicorp/terraform:latest init
docker run --rm --network k3d-staging -v "<tfc>:/work" -v "<tfmirror>:/mirror" `
  -e TF_CLI_CONFIG_FILE=/mirror/terraformrc -w /work hashicorp/terraform:latest apply -auto-approve
# vytvoří ns terraform-demo + deployment tf-web (label managedby=terraform)
```

### 4.9 Ansible (automatizace) — přes Linux kontejner
```powershell
docker run --rm --network k3d-staging -v "<ansible>:/work" -e K8S_AUTH_KUBECONFIG=/work/kube.yaml `
  -w /work python:3.12-slim sh -c "pip install -q --trusted-host pypi.org --trusted-host files.pythonhosted.org ansible-core kubernetes && ansible-galaxy collection install kubernetes.core --ignore-certs && ansible-playbook -i 'localhost,' playbook.yml"
# vytvoří ns ansible-demo + deployment ansible-web (kubernetes.core.k8s)
```

---

## 5. Přístupy a hesla

| Služba | URL | Login |
|---|---|---|
| Rancher (multi-cluster, RBAC) | https://localhost:8443 | `admin` / `<RANCHER_PASSWORD>` |
| ArgoCD (GitOps) | ns `argocd` na staging (port-forward / Rancher proxy) | `admin` / `<ARGOCD_PASSWORD>` |
| Grafana (dashboardy) | ns `monitoring` na prod | `admin` / `<GRAFANA_PASSWORD>` |
| RBAC demo uživatel | Rancher | `dev` / `<DEV_PASSWORD>` |

> Hesla jsou lab-only. Kubeconfigy: `%USERPROFILE%\.kube\k3d-lab\{staging,prod}.yaml`.

---

## 6. Gotchas — s čím jsem se musel vypořádat

Tohle je nejcennější část pro pohovor — reálné problémy a jak je vyřešit.

### 6.1 k3d HA cluster nepřežije `wsl --shutdown`
**Problém:** po restartu WSL2 dostaly k3d kontejnery nové IP adresy, ale etcd má v member listu
napevno staré peer URL → `connection refused` na portu 2380, quorum se neobnoví
(`starting a new election` donekonečna).
**Fix:** s běžícími HA clustery **nerestartovat WSL**. Když se to stane, clustery smazat a vytvořit
znovu (`k3d cluster delete ...` + `create`). Individuální `docker restart` uzlu IP zachová (etcd přežije),
`wsl --shutdown` ne.

### 6.2 AVG antivirus dělá HTTPS MITM → rozbíjí image pull v containerd
**Problém:** k3s uzly nestáhnou žádný image (`x509: certificate signed by unknown authority`), protože
AVG podvrhuje vlastní certifikát. Docker daemon to zvládá (systémové CA), ale containerd uvnitř k3d ne.
**Fix:** `registries.yaml` s `insecure_skip_verify: true` pro docker.io, registry-1.docker.io, quay.io,
ghcr.io, registry.k8s.io, gcr.io → `docker cp` do `/etc/rancher/k3s/registries.yaml` v každém uzlu →
`docker restart` uzlů (zachová IP). Ideálně `k3d cluster create --registry-config registries.yaml`.
```powershell
$nodes = docker ps --format "{{.Names}}" | Select-String "k3d-(staging|prod)-(server|agent)-\d"
foreach($n in $nodes){ docker cp registries.yaml "${n}:/etc/rancher/k3s/registries.yaml" }
docker restart $nodes
```
> Pully jsou i tak pomalé (~1,5 min i malý image) kvůli AVG inspekci.

### 6.3 Rancher agent se přes AVG nepřipojí (CA checksum nesedí)
**Problém:** cattle-cluster-agent connect na Rancher přes `host:8443` prochází AVG MITM → cert nesedí
s CA checksumem → `CrashLoopBackOff`. Navíc po restartu uzlů CoreDNS ztratil `host.k3d.internal`.
**Fix:** vést spojení **interní Docker cestou mimo AVG**:
```powershell
docker network connect k3d-staging rancher-mgmt
docker network connect k3d-prod    rancher-mgmt
# server-url na jméno kontejneru (Docker DNS ho na každé síti přeloží na interní IP)
# PUT /v3/settings/server-url -> https://rancher-mgmt:443
# znovu aplikovat registrační manifest (přegeneruje cattle-credentials s novou url)
```
> Go binárka agenta bere URL z `cattle-credentials` secretu, ne z `CATTLE_SERVER` env — proto re-import.

### 6.4 ArgoCD redis z `public.ecr.aws` (mimo skip-list)
**Problém:** `argocd-redis` v `ImagePullBackOff` — image `public.ecr.aws/docker/library/redis`, který
nebyl ve skip-listu.
**Fix:** přesměrovat na stejný image z docker.io (v skip-listu):
`kubectl -n argocd set image deployment/argocd-redis redis=redis:8.2.3-alpine`.

### 6.5 ArgoCD git clone přes AVG (go-git ignoruje GIT_SSL_NO_VERIFY)
**Problém:** `failed to list refs ... x509: certificate signed by unknown authority` při klonování
z github.com. ArgoCD používá go-git, ne git CLI, takže `GIT_SSL_NO_VERIFY` nezabere.
**Fix:** zaregistrovat repo jako secret s `insecure: "true"`:
```yaml
apiVersion: v1
kind: Secret
metadata:
  labels: { argocd.argoproj.io/secret-type: repository }
stringData:
  url: https://github.com/argoproj/argocd-example-apps.git
  insecure: "true"
```

### 6.6 AVG zabíjí Terraform provider plugin (Windows)
**Problém:** `terraform apply` padá na `Failed to load plugin schemas ... plugin process exited:
exit status 1` — AVG ukončuje loopback mTLS RPC proces pluginu. (Defender má realtime OFF, viník je AVG.)
**Fix:** spustit Terraform v **Linux kontejneru** (AVG je Windows AV, do WSL2 kontejnerů nezasahuje) a
obejít i stažení provideru — stáhnout ho `curl -k` do **filesystem mirroru** a nastavit `terraformrc`:
```hcl
provider_installation {
  filesystem_mirror { path = "/mirror"  include = ["registry.terraform.io/hashicorp/kubernetes"] }
  direct            { exclude = ["registry.terraform.io/hashicorp/kubernetes"] }
}
```

### 6.8 Screenshot Rancher UI (self-signed HTTPS + headless browser)
**Problém:** Rancher běží na self-signed HTTPS (`:8443`), který headless/preview prohlížeč odmítá
(`ERR_CERT_AUTHORITY_INVALID`). Přímé otevření ani přes `insecure` nešlo.
**Fix:** malý reverzní proxy (`tools/rancher-proxy.js`, Node bez závislostí) přemostí Rancher na plain
HTTP `:8900`. Klíčové úpravy hlaviček:
- **Set-Cookie**: odstranit `Secure`, přepsat `SameSite=None` → `Lax` (jinak prohlížeč session cookie na
  http zahodí).
- **NEpřepisovat `Host`** (nechat `localhost:8900`) + přidat `X-Forwarded-Host/Proto` — jinak Rancher
  vygeneruje UI s absolutními `https://localhost:8443` URL (cert error).
- **WebSocket** (`/v1/subscribe`) proxovat přes `upgrade` event — dashboard bez něj jede na fail-whale.
- **`/v1/uiplugins`** vrátit prázdný seznam (blokoval loading spinner).
- **HSTS** hlavičku strippnout, `Location` redirecty přepsat na proxy.

### 6.7 Obecný AVG pattern
Cokoliv, co stahuje přes TLS uvnitř **nested kontejneru / pluginu**, selže na MITM cert. Fixy per-vrstva:

| Vrstva | Fix |
|---|---|
| containerd (k3s) | `registries.yaml` → `insecure_skip_verify` |
| ArgoCD go-git | repo secret `insecure: true` |
| pip | `--trusted-host pypi.org --trusted-host files.pythonhosted.org` |
| ansible-galaxy | `--ignore-certs` |
| terraform | filesystem mirror (provider stažený `curl -k`) |
| Rancher agent | interní Docker síť (mimo host/AVG) |

> Host Docker daemon a host `curl`/`winget` AVG certu důvěřují (systémové CA), takže fungují — proto se
> obtížné stahování dělá na hostu a výsledek se dostane do kontejneru (image import / mirror / volume).

---

## 7. Teardown

```powershell
k3d cluster delete staging prod
docker rm -f rancher-mgmt
```

---

## 8. Co dál (nedokončené / rozšíření)

- **OpenShift**: dodat **OKD** / OpenShift Local (CRC) — single-node, náročné na RAM (~9 GB).
- **Longhorn / Ceph**: replikovaný persistent storage místo local-path.
- **VMware/KVM vrstva**: reálná varianta na Proxmox / Harvester.
- **CI část**: doplnit k GitOps (ArgoCD = CD) i CI pipeline (GitLab CI / GitHub Actions / Jenkins).
- **Alerting**: Alertmanager + pravidla nad Prometheem.

---

## Adresářová struktura

```
k3d-rancher-lab/
├── README.md                 tento dokument
├── manifests/                všechny k8s manifesty (app, rbac, monitoring, argocd, ksm, registries)
├── terraform/                main.tf (kubernetes provider) + terraformrc (mirror)
├── ansible/                  playbook.yml (kubernetes.core.k8s)
└── tools/                    rancher-proxy.js (reverzní proxy pro screenshot UI, viz §6.8)
```
