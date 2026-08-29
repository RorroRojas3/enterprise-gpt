# Build order: Azure Infrastructure

A dependency-resolved execution sequence for the 45 stories in [`azure-infrastructure.md`](azure-infrastructure.md), for tracking progress against.

**The PRD is authoritative.** This file adds no requirements and changes no scope. It derives eight waves from §8's phase table plus each story's `Depends on` field, and assigns one global `Order` counter across all of them. Where this file and the PRD disagree, the PRD wins and this file is wrong.

A wave here is a **phase**, not a dependency depth: every wave below contains internal chains, so a story's `Depends on` may name another story in the same wave. What no story does is depend on one scheduled in a **later** wave — every row was checked against that rule by hand against the full dependency list. Within a wave, the `Parallel with` column names the stories that are neither an ancestor nor a descendant of it in the dependency graph — those can be taken concurrently by however many people are available. Dense waves use the shorthand *all of wave N* rather than enumerating every id.

Status here mirrors the `Status` field tracked against each PRD story (this PRD does not carry a `Status` line per story yet, since no work has started — add one when a story begins). Update both, or update the PRD and re-derive this.

**Progress: 0 / 45 done.**

**Critical path.** The deepest chain in the graph is ten stories long: `US-101 → US-102 → US-105 → US-201 → US-202 → US-403 → US-404 → US-801 → US-802 → US-803`. `US-101` (the idempotent state backend) gates everything — nothing in any later wave can be verified against real infrastructure until it closes. `US-105` (the Terraform skeleton) is the first true fan-out point: `US-201` (compute), `US-301`–`US-305` (data plane), `US-401`/`US-402` (Key Vault/identity foundations), and `US-902` (docs generation) all branch from it directly and could in principle run concurrently. `US-403` is the graph's real bottleneck — it is the only story that needs **all five** data-plane resources (`US-301`–`US-305`) *and* the API web app (`US-202`) *and* both identity prerequisites (`US-401`, `US-402`) closed before it can start; nothing in EP-4 through EP-9 that touches a Key Vault reference or the fully-booted API can begin before it. `US-801` is the second join: it needs both the `US-404` chain (secrets and identity) and the `US-704` chain (pipeline plus the security-scan gate) closed, and every EP-8 story sits behind it. Two EP-9 stories — `US-901` and `US-903` — also join at `US-403` directly, which is why EP-9 is scheduled last as a phase even though `US-902` and `US-904` have no technical reason to wait that long (see Wave 8's note). **Useful concurrency reaches at least 7** in Wave 1 alone — `US-102`, `US-103`, `US-104`, `US-501`, `US-502`, `US-503`, and `US-505` share no dependency edge with one another once `US-101` closes — against a build that opens with a strict two-story chain (`US-101` then the fan-out).

## Wave 1 — bootstrap and app-side fixes

Ten stories: EP-1's bootstrap chain in full, plus four of EP-5's five app-side stories (the fifth, `US-504`, needs a deployed environment and lands in Wave 5). Nothing here needs an Azure subscription beyond what the Operator's bootstrap run itself creates, and the four EP-5 stories need nothing at all beyond the existing codebases — this is why EP-5 is described as "parallel throughout" rather than owning its own phase.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 1 | US-101 `[enabler]` Idempotent Terraform state backend | — | P0 | US-501, US-502, US-503, US-505 | Not started |
| 1 | 2 | US-102 `[enabler]` Environment resource groups | US-101 | P0 | US-103, US-104, US-501, US-502, US-503, US-505 | Not started |
| 1 | 3 | US-103 `[enabler]` GitHub OIDC deploy identities | US-101 | P0 | US-102, US-104, US-501, US-502, US-503, US-505 | Not started |
| 1 | 4 | US-104 `[enabler]` Terraform state restore script and runbook | US-101 | P0 | US-102, US-103, US-105, US-106, US-501, US-502, US-503, US-505 | Not started |
| 1 | 5 | US-105 `[enabler]` Terraform platform skeleton | US-102, US-103 | P0 | US-104, US-501, US-502, US-503, US-505 | Not started |
| 1 | 6 | US-106 CI plan and apply reach remote state over OIDC | US-105 | P0 | US-104, US-501, US-502, US-503, US-505 | Not started |
| 1 | 7 | US-501 `[enabler]` SPA IIS fallback `web.config` | — | P0 | all of wave 1 | Not started |
| 1 | 8 | US-502 `[enabler]` Anonymous, dependency-free health endpoint | — | P0 | all of wave 1 | Not started |
| 1 | 9 | US-503 `[enabler]` API `web.config` disables dynamic compression | — | P0 | all of wave 1 | Not started |
| 1 | 10 | US-505 `[enabler]` `CorsOrigins` index-bound configuration | — | P0 | all of wave 1 | Not started |

⛔ **`US-101` is a hard gate, not a phase.** Nothing else in this PRD — not even the app-side EP-5 stories, which are otherwise fully independent — should be treated as "done" until `New-TerraformBackend.ps1` has been run twice and the second run reports no changes; that is the entire proof the state backend is trustworthy. `US-105` is the wave's other join point: it needs both `US-102` and `US-103`, and everything in Wave 2 onward needs `US-105` specifically, not merely `US-101`.

## Wave 2 — compute and observability

Six stories, EP-2 in full. Scheduled after Wave 1 by product decision (bank a first working apply before the larger data-plane surface), not because any story here has a technical dependency on EP-3 or EP-4.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 2 | 11 | US-201 `[enabler]` Windows service plans | US-105 | P0 | US-204 | Not started |
| 2 | 12 | US-202 API and SPA web apps | US-201 | P0 | US-203, US-204 | Not started |
| 2 | 13 | US-203 `[enabler]` Function App placeholder | US-201 | P0 | US-202, US-204 | Not started |
| 2 | 14 | US-204 `[enabler]` Log Analytics and Application Insights per environment | US-105 | P0 | US-201, US-202, US-203 | Not started |
| 2 | 15 | US-205 `[enabler]` Wire Application Insights into the API and Function App | US-202, US-203, US-204 | P0 | — | Not started |
| 2 | 16 | US-206 Diagnostics on compute, and the first working apply | US-202, US-203, US-205 | P0 | — | Not started |

⛔ **`US-206` is EP-2's exit criterion and its own join point** — it needs both `US-203` (the Function App) and `US-205` (App Insights wiring) closed, and it is the story a reviewer should point at when asked "does dev actually work yet."

## Wave 3 — data plane

Six stories, EP-3 in full. Every one of `US-301`–`US-305` depends only on `US-105` from Wave 1 and could in principle run in parallel with all of Wave 2 — they are scheduled as their own phase to match the PRD's fixed epic order, not because of a technical blocker.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 3 | 17 | US-301 Azure SQL, sequenced ahead of the API | US-105 | P0 | US-302, US-303, US-304, US-305 | Not started |
| 3 | 18 | US-302 Cosmos DB for transcripts | US-105 | P0 | US-301, US-303, US-304, US-305 | Not started |
| 3 | 19 | US-303 Blob Storage for documents | US-105 | P0 | US-301, US-302, US-304, US-305 | Not started |
| 3 | 20 | US-304 Document Intelligence | US-105 | P0 | US-301, US-302, US-303, US-305 | Not started |
| 3 | 21 | US-305 Azure OpenAI account and deployments | US-105 | P0 | US-301, US-302, US-303, US-304 | Not started |
| 3 | 22 | US-306 `[enabler]` Data-plane diagnostics | US-301, US-302, US-303 | P1 | US-304, US-305 | Not started |

⚠ **`US-301`–`US-305` are the widest point in the whole build** — five mutually independent stories, each needing only `US-105`. Five people could take one each on the same day with zero coordination overhead.

## Wave 4 — Key Vault and identity

Four stories. This is where EP-2 and EP-3's outputs actually converge — `US-403` is the graph's single narrowest point.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 4 | 23 | US-401 `[enabler]` User-assigned identity, granted before any app depends on it | US-105 | P0 | US-402 | Not started |
| 4 | 24 | US-402 Key Vault provisioning | US-105 | P0 | US-401 | Not started |
| 4 | 25 | US-403 Seven Key Vault secrets, wired as versionless references | US-401, US-402, US-202, US-301, US-302, US-303, US-304, US-305 | P0 | — | Not started |
| 4 | 26 | US-404 Key Vault audit diagnostics and the first full boot | US-403 | P0 | — | Not started |

⛔ **`US-403` is the graph's real bottleneck.** It is the only story anywhere in this PRD that needs eight prior stories closed — both identity foundations, the API web app, and all five data-plane resources. Do not staff `US-401`/`US-402` and `US-403` to the same person concurrently; the join needs all of Waves 1–3's relevant branches finished, not raced. `US-404` is EP-4's exit criterion: the first point in the build where the deployed API actually boots end to end.

## Wave 5 — pipelines

Six stories: EP-6 in full, plus `US-504` (EP-5's SSE verification), which needed a real deployment and could not run any earlier.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 5 | 27 | US-601 `[enabler]` `infra-cd.yml` — plan on PR, apply on merge | US-106 | P0 | — | Not started |
| 5 | 28 | US-602 `[enabler]` `api-cd.yml` deploys the API to dev | US-202, US-503, US-601 | P0 | US-603, US-605 | Not started |
| 5 | 29 | US-603 `[enabler]` `ui-cd.yml` builds once, generates `config.json` per environment | US-202, US-501, US-601 | P0 | US-602, US-605 | Not started |
| 5 | 30 | US-604 Merge to master deploys dev unattended | US-602, US-603 | P0 | US-605 | Not started |
| 5 | 31 | US-605 `deploy-prod` gated by required reviewers | US-601 | P0 | US-602, US-603, US-604 | Not started |
| 5 | 32 | US-504 SSE keep-alive survives the platform's idle timeout | US-503, US-604 | P1 | US-605 | Not started |

⛔ **`US-601` is this wave's fan-out point** — `US-602`, `US-603`, and `US-605` all depend on it directly and nothing else, so once it closes, three people can take one each. `US-604` is the wave's join and EP-6's exit criterion; `US-504` is the last story in the entire PRD that only becomes testable once `US-604` closes, since it needs a real, currently-deployed dev environment to stream against.

## Wave 6 — hardening

Four stories, EP-7 in full.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 6 | 33 | US-701 `[enabler]` Wire `health_check_path` | US-502, US-202 | P1 | US-702, US-703, US-704 | Not started |
| 6 | 34 | US-702 Tighten firewalls to explicit outbound IPs | US-301, US-303 | P1 | US-701, US-703, US-704 | Not started |
| 6 | 35 | US-703 Prod purge protection and a dev ingestion cap | US-402, US-204 | P2 | US-701, US-702, US-704 | Not started |
| 6 | 36 | US-704 `[enabler]` `tflint` and a security scanner in CI | US-601 | P0 | US-701, US-702, US-703 | Not started |

All four stories in this wave are mutually independent — each depends only on resources from earlier waves, none on each other — so this is a second wide point in the build, smaller than Wave 3 but just as parallelizable.

## Wave 7 — production cutover

Four stories, EP-8 in full, and the second-deepest chain in the PRD.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 7 | 37 | US-801 Prod tfvars and the prod-tier resources | US-404, US-704 | P0 | — | Not started |
| 7 | 38 | US-802 `[enabler]` Prod Entra redirect URIs | US-801 | P0 | US-804 | Not started |
| 7 | 39 | US-803 First prod apply and end-to-end smoke test | US-801, US-802, US-605 | P0 | US-804 | Not started |
| 7 | 40 | US-804 `[enabler]` Record the custom-domain and DNS decision | US-801 | P2 | US-802, US-803 | Not started |

⛔ **`US-801` is this wave's join** — it needs both the `US-404` chain (secrets/identity, the deep branch) and the `US-704` chain (the security-scan gate, the shallow branch) closed; it is also the deepest story that is not itself `US-803`. `US-803` is the PRD's overall exit criterion: the whole document is not done until this story's five acceptance criteria all pass against live prod URLs.

## Wave 8 — tests and runbooks

Five stories, EP-9 in full, closed last as a phase even though two of its five stories have no technical reason to wait.

| Wave | Order | Story | Depends on | Pri | Parallel with | Status |
| --- | --- | --- | --- | --- | --- | --- |
| 8 | 41 | US-901 `[enabler]` `.tftest.hcl` suite for the three named invariants | US-403, US-202 | P0 | US-903, US-904, US-905 | Not started |
| 8 | 42 | US-902 `[enabler]` `terraform-docs` READMEs | US-105 | P2 | US-901, US-903, US-904, US-905 | Not started |
| 8 | 43 | US-903 Secret rotation and add-a-setting runbooks, rehearsed | US-403 | P1 | US-901, US-902, US-904, US-905 | Not started |
| 8 | 44 | US-904 Key Vault recovery and state restore runbooks, rehearsed | US-104, US-402 | P0 | US-901, US-902, US-903, US-905 | Not started |
| 8 | 45 | US-905 Deploy rollback runbook, rehearsed against dev | US-604 | P0 | US-901, US-902, US-903, US-904 | Not started |

⚠ **`US-902` and `US-904` are not actually bottlenecked on Wave 4/5** — `US-902` needs only `US-105` (Wave 1) and `US-904` needs only `US-104` (Wave 1) and `US-402` (Wave 4). Both could be pulled forward and written well before this wave opens, exactly the way the PRD's own §8 phrasing anticipates ("individual stories are written and exercised alongside every earlier phase"). What genuinely cannot happen early is **closing** EP-9 — `US-901` and `US-903` are hard-gated on `US-403` (Wave 4's bottleneck), and `US-905`'s rehearsal needs `US-604` (Wave 5) to have something real to roll back. Treat this wave's five stories as a checklist to close out, not five stories to start simultaneously on day one.
