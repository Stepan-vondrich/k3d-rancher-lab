# Nasazení na železo — návrh privátní platformy (BOM, síť, VLANy)

Produkční varianta labu na reálném hardwaru dle zadání: **3 compute nody, storage, 2 switche,
HA Kubernetes se staging + produkčním prostředím, min. 3 control-plane nody, oddělení rolí, redundance,
správa přes Rancher.** Vše na bezplatných/open-source nástrojích.

---

## 1. Doporučený stack (vše zdarma)

| Vrstva | Volba | Poznámka |
|---|---|---|
| Virtualizace (hypervisor) | **Proxmox VE** (nebo SUSE **Harvester**) | KVM; Harvester = HCI nativně integrovaný s Rancherem |
| Kubernetes distribuce | **RKE2** | CIS-hardened, HA embedded etcd, plně zdarma |
| Multi-cluster + RBAC | **Rancher** | jeden panel na staging i prod, projekty/role |
| Persistent storage (K8s) | **Longhorn** nebo **Ceph (Rook / Proxmox)** | replikace 3× |
| Load balancer / Ingress | **MetalLB + ingress-nginx** + **keepalived (VRRP)** | API VIP a ingress VIP |
| Automatizace / IaC | **Terraform + Ansible** | provisioning VM + konfigurace |

---

## 2. Fyzická vrstva — 3 compute nody

Doporučená specifikace jednoho nodu (mid-range, s rezervou pro HCI storage):

| Komponenta | Specifikace (na 1 nod) |
|---|---|
| CPU | 2× Intel Xeon Gold 6338 (2× 32 jader / 64 vláken) = **64 c / 128 t** |
| RAM | **256 GB** DDR4 ECC |
| Disk – OS | 2× 480 GB NVMe (boot, ZFS mirror pro Proxmox) |
| Disk – data | 4× 3,84 TB NVMe/SSD (Ceph OSD / Longhorn) |
| Síť – data | 2× 25 GbE SFP28 (jedna do každého switche, LACP bond) |
| Síť – mgmt | 1× 1 GbE + dedikovaný **IPMI/iDRAC/iLO** port |
| Napájení | 2× redundantní PSU (do 2 nezávislých PDU/přívodů) |

**Součet přes 3 nody:** 192 vláken · 768 GB RAM · ~46 TB raw SSD (replikace 3× → **~15 TB usable**).

---

## 3. BOM — rozpad na virtuální stroje (VM)

3 logické clustery: **management (Rancher)**, **staging**, **production**. VM jsou rozprostřené tak,
aby **každá ze 3 control-plane replik každého clusteru ležela na jiném fyzickém nodu** — výpadek jednoho
fyzického nodu neshodí quorum žádného clusteru.

| VM | Role | Počet | vCPU | RAM | Disk | Umístění (1 replika / nod) |
|---|---|---|---|---|---|---|
| `rancher-cp-{1,2,3}` | Rancher mgmt cluster (RKE2, HA) | 3 | 4 | 8 GB | 60 GB | nod 1 / 2 / 3 |
| `stg-cp-{1,2,3}` | Staging control-plane + etcd | 3 | 4 | 8 GB | 60 GB | nod 1 / 2 / 3 |
| `stg-worker-{1,2,3}` | Staging worker | 3 | 8 | 24 GB | 100 GB | nod 1 / 2 / 3 |
| `prod-cp-{1,2,3}` | Production control-plane + etcd | 3 | 4 | 8 GB | 80 GB | nod 1 / 2 / 3 |
| `prod-worker-{1,2,3}` | Production worker | 3 | 16 | 64 GB | 200 GB | nod 1 / 2 / 3 |
| `lb-{1,2}` | HAProxy + keepalived (API/ingress VIP) | 2 | 2 | 4 GB | 40 GB | nod 1 / 2 |

**Celkem: 17 VM.** Kontrolní nody jsou tainted (`NoSchedule`) — běží na nich jen etcd + apiserver +
scheduler, žádné aplikace. Aplikace běží výhradně na workerech.

### Kontrola kapacity (na 1 fyzický nod)

| Nod | VM na nodu | Σ vCPU | Σ RAM |
|---|---|---|---|
| Nod 1 | rancher-cp-1, stg-cp-1, stg-worker-1, prod-cp-1, prod-worker-1, lb-1 | **38** | **116 GB** |
| Nod 2 | rancher-cp-2, stg-cp-2, stg-worker-2, prod-cp-2, prod-worker-2, lb-2 | **38** | **116 GB** |
| Nod 3 | rancher-cp-3, stg-cp-3, stg-worker-3, prod-cp-3, prod-worker-3 | **36** | **112 GB** |

- **vCPU:** ~38 vCPU z 128 vláken → poměr overcommitu ~0,6:1 (velká rezerva pro špičky a Ceph).
- **RAM:** ~116 GB z 256 GB → zbývá ~140 GB na Proxmox overhead, Ceph OSD (~4 GB/OSD × 4) a rezervu pro
  **N+1** (aby se při výpadku jednoho nodu jeho VM vešly na zbývající dva).

> N+1 pravidlo: součet RAM VM libovolných dvou nodů (~232 GB) se musí vejít do RAM zbylých dvou nodů
> (2× 256 = 512 GB). Splněno s velkou rezervou → cluster přežije výpadek celého fyzického nodu.

---

## 4. Storage

Dvě varianty (lze i kombinovat):

- **Ceph (hyperkonvergovaně v Proxmoxu)** — 4 SSD z každého nodu jako OSD, **replikace 3× (size=3,
  min_size=2)**. Poskytuje sdílené blokové úložiště pro VM (živá migrace, VM HA) i pro K8s přes Ceph-CSI.
- **Longhorn (uvnitř K8s)** — replikovaný block storage na discích workerů, replikace 3×. Jednodušší pro
  K8s PV, nativní v Rancher/SUSE ekosystému.

**Doporučení:** Ceph na úrovni virtualizace (kvůli VM HA) + Longhorn nebo Ceph-CSI pro K8s Persistent
Volumes. Při Harvesteru je Longhorn součástí distribuce.

---

## 5. Síť — zapojení 2 switchů

**2× manageable L3 switch** (např. 25 GbE, s podporou **MLAG / stacking**), propojené mezi sebou
(peer-link / ISL). Každý fyzický nod má **2× 25 GbE, jednu linku do každého switche**, spojené do
**LACP bondu (802.3ad)**. Switche tvoří vůči nodu jeden logický kanal (MLAG) → **výpadek jednoho switche
neznamená výpadek služby**.

```
        ┌───────────────┐   peer-link / ISL   ┌───────────────┐
        │   Switch A    │◄───────────────────►│   Switch B    │   (MLAG / stack)
        └──┬────┬────┬──┘                     └──┬────┬────┬──┘
           │    │    │        LACP bond          │    │    │
        ┌──▼─┐┌─▼──┐┌▼───┐  (1 linka do každého  ┌▼─┐ ...
        │Nod1││Nod2││Nod3│   switche na nod)
        └────┘└────┘└────┘
```

- Napájení switchů z **různých okruhů**; nody 2× PSU do 2 PDU.
- Management/IPMI: dedikovaná 1 GbE do obou switchů (nebo malý out-of-band mgmt switch).

### VLANy (trunk na bondu)

| VLAN | Název | Subnet | Účel | Pozn. |
|---|---|---|---|---|
| 10 | mgmt | 10.10.10.0/24 | Proxmox/Harvester UI, SSH, **IPMI/iLO**, Rancher UI | out-of-band |
| 20 | storage | 10.10.20.0/24 | Ceph/Longhorn replikace | **jumbo frames MTU 9000**, ideálně izolovaná |
| 30 | k8s-nodes | 10.10.30.0/24 | node-to-node, etcd, CNI (pod/service) | |
| 40 | ingress/DMZ | 10.10.40.0/24 | MetalLB pool, ingress VIP, veřejné služby | za firewallem |

- **API VIP + ingress VIP:** keepalived (VRRP) na `lb-1/lb-2`, nebo MetalLB v L2/BGP režimu.
- **L3 routing** mezi VLANami na switchích (SVI) nebo na firewallu; per-VLAN default gateway.
- Storage VLAN 20 držet oddělenou (výkon replikace, bezpečnost).

---

## 6. Redundance — shrnutí

| Vrstva | Ochrana |
|---|---|
| Výpadek fyzického nodu | 3 control-plane (quorum 2/3), VM HA přesune VM, Ceph replika 3, N+1 kapacita |
| Výpadek switche | LACP bond do 2 switchů + MLAG |
| Výpadek disku | Ceph/Longhorn replikace 3× (size=3, min_size=2) |
| Výpadek napájení | 2× PSU na nod, 2 nezávislé PDU/přívody |
| Výpadek etcd membera | liché 3 nody → toleruje výpadek 1 |

---

## 7. Oddělení rolí a RBAC

- **Control-plane VM tainted** — jen etcd + apiserver + scheduler; aplikace pouze na workerech.
- **Rancher na dedikovaném mgmt clusteru** (nikdy ne uvnitř clusteru, který spravuje).
- **RKE2 CIS-hardened** profil.
- **RBAC přes Rancher projekty**: vývojář = `project-member` ve staging namespace, `read-only` v produkci;
  ops = `cluster-owner`. Napojení na **AD/LDAP nebo OIDC** (SSO).

---

## 8. Provisioning (IaC)

- **Terraform** (`telmate/proxmox` nebo Harvester provider) — vytvoření VM z šablon dle BOM výše.
- **Ansible** — příprava OS, instalace RKE2 (server/agent role), registrace clusterů do Rancheru.
- **Rancher / Fleet** — GitOps nasazení aplikací a konfigurace do staging a prod (viz [README.md](README.md)).

---

## 9. Mapování zmenšeniny (lab) → železo

| Lab (k3d) | Železo |
|---|---|
| `local` cluster (Rancher kontejner) | 3× `rancher-cp` VM (RKE2 HA) |
| `staging` k3d (3 server + agent) | `stg-cp-{1,2,3}` + `stg-worker-{1,2,3}` |
| `prod` k3d (3 server + agent) | `prod-cp-{1,2,3}` + `prod-worker-{1,2,3}` |
| Docker bridge sítě | VLANy 10/20/30/40 na 2 switchích |
| local-path storage | Ceph / Longhorn (replika 3×) |
| host port-forward | keepalived VIP + MetalLB + ingress-nginx |

Koncepty (HA 3 control-plane, oddělení control/worker, staging vs prod, Rancher RBAC, GitOps) jsou
**identické** — lab je ověřuje v malém, tento návrh je přenáší na produkční železo.
