# AAAW M1 qualification — 2026-10-14

The bounded repository read/report loop is implemented and qualified on Windows x64. It uses actual Fuwen admission and execution mapping, durable Zhinu runs, Qingniao's public initial delegation lifecycle, current Hufu/Cedar authority, and Hongxian evidence. The planner and observable provider are deterministic fixtures. This is an experimental vertical slice, not a package graduation.

| Area | Result | Evidence |
|---|---|---|
| Workflow revision | Immutable plans/lineage; atomic durable activation | `AdaptivePlanAuthoringTests`, `AdaptiveRevisionTests`; two revisions in the demo |
| Change-set semantics | Conservative contract/input/authority/provenance/dependency comparison | Requirement-change, input-tampering, archive-dependency and reuse tests |
| Partial branch preservation | Same completed inventory and running README-review execution identities | P2 in `AdaptiveRevisionProcessTests`; one inventory invocation |
| Completed-work reuse | Explicit acceptance; unchanged completed work is not rerun | P3; missing-reuse rejection test |
| Fencing | Affected generations cannot finish authoritative steps; old provider completion stays historical | P4–P6; stale step-finish test; kill inside fencing transaction |
| Approval binding | Exact immutable proposal; authenticated durable receipt; fresh Hufu check | P7/P11/P12; stale-state, replacement-proposal, expiry and revoked-grant tests |
| Delegation lifecycle | Real initial Qingniao lifecycle; durable same-attempt provider recovery | Durable-provider tests and normal/recovered integration paths; P10 no-respawn |
| Restart recovery | All seven required barriers plus unknown start/capture gap pass | Separate killed/recovered processes in the final consumer suite |
| Evidence/history | Retained runtime revisions/outbox; idempotent Hongxian mirroring | Both revisions, approval and historical execution in the repository demonstration |
| End-to-end demo | Repository README/package review changes from JSON to Markdown and finishes | [Demonstration](demonstration.md), [catch-up view](catch-up.json) |

## Exact implementation sources

| Repository | Commit | Branch |
|---|---|---|
| Marang | `1820d44f7f65beef63fc922d3be8f6c9a8aab7ff` | `codex/aaaw-m1-marang` |
| Penghou.Zhinu | `07e1807cc4b867dd4f527445aa108f56135d37db` | `codex/aaaw-m1-zhinu` |
| Penghou.Qingniao | `f08971df33916be184fd1bebb5940ba2bd660a49` | `codex/aaaw-m1-qingniao` |

The follow-up Marang evidence commit adds this report and captured results; it does not change implementation. Original sibling checkouts and their pre-existing edits were preserved. No Hufu, Gagamba, Fuwen, Hongxian or stable-foundation source was changed.

## Validation

- Final package consumers: **119/119 .NET 8**, **149/149 .NET 10**, no failures or skips.
- Current committed runtime safety suite: **10/10 on each framework**, including lease-gated joins and witness-based staleness.
- Full SQLite source suite: **469/469 on each framework** before two test-only additions; the final focused ten include those two additions. Implementation was unchanged between those runs.
- Qingniao source suite: **433/433 on .NET 8 and .NET 10**. The .NET 10 TRX is retained; .NET 8 passed in the source verification console.
- Qingniao Hufu consumer: **9/9 .NET 10**. Codex consumer: **69 passed, 3 skipped, 0 failed**. These verify the preserved runtime constructor signature.
- Full Marang solution build and formatting gate passed. A final bounded authoring check passed **3/3 on each framework** after the complete consumer run.

The final package consumers use real NuGet packages, not source project references. Package metadata names the exact committed producer sources:

| Package | Unpublished qualification version | SHA-256 |
|---|---|---|
| Penghou.Zhinu | `0.2.0-preview.4-aaaw-m1.3` | `8c850fc2f63cf73c586209aaffb2db29277d1c8240e46fe9734b7da479efa875` |
| Penghou.Zhinu.Sqlite | `0.2.0-preview.4-aaaw-m1.3` | `5b18324a395b71c36043546d2580ac6c76739fb8f45323ca4b1d1ee19ca1fe39` |
| Penghou.Qingniao.Abstractions | `0.1.0-preview.5-aaaw-m1.4` | `7f111991ff208ad573f8af8cdab7b45b9a451825b4e2de1189db949584f75870` |
| Penghou.Qingniao | `0.1.0-preview.5-aaaw-m1.4` | `1a5a268ed65073c3b78b1af8cf69688c7d2f633cffe5d5da8d1e04ed1f2b7d37` |

[Machine-readable evidence](qualification.json) records TRX counters/checksums, package hashes and native process identities. Raw local artifacts remain under `artifacts/aaaw-m1/`; CI uploads that directory. Reproduce with [the qualification script](../../eng/qualify-aaaw-m1.ps1) and its exact source lock. Nothing was published to NuGet and no release/tag was created.

The process suite kills after initial activation, while delegation is running, after proposal creation, after approval/before commit, inside fencing, after commit/before replacement dispatch, and while replacement work is running. Each fresh process finishes revision 2 with two retained revisions, one reused inventory, preserved review identity, and exactly one original plus one replacement provider start. A separate kill after physical provider start but before handle capture yields `UnknownAfterRecovery`, original revision 1, and **one** provider start.

Initial qualification found and corrected archive/manifest dependency mismatch, missing delegated-progress staleness, proposer-controlled review requirements and an unexpired dead-process lease dispatch error. The final results above follow those corrections. Earlier failed local artifacts are retained and are not counted as successful qualification.

## Contracts and decisions

Marang ADR 0016 introduces the bounded manifest/host boundary and resolves the minimum recovery ownership previously open in the M5 design note. Qingniao ADR 0022 introduces the optional durable provider journal/decorator. Runtime APIs and SQL capability tables are explicitly experimental; existing single-generation semantics remain intact. Public API baselines include the additive producer surfaces. The shipped `DelegationRuntime` constructor remains unchanged.

The host supports 2–16 total work items, at least two independent branches, and at most one join of all branches. Automatic admission uses Continue/AllowToFinish for equivalent preserved work and Fence for affected work. Other disposition names remain reserved and are rejected. The console exposes Approve, Reject, Cancel and Discuss; general server workflow-editing UI is outside M1.

## Supported claim and limits

The evidence supports this qualified claim:

> Penghou can durably adapt a running workflow in the bounded repository read/report profile after new user input, retain unaffected execution identities and completed results, and prevent superseded local or delegated results from becoming authoritative.

Within a surviving process the unaffected running branch continues. After process death, idempotent local work re-enters the same durable execution and known provider work reconnects to the same operation. This is not a promise that local process code runs exactly once across crashes.

Qingniao recovery restores the provider-attempt binding and observation/result path; it does not rehydrate the full in-memory supervisory runtime, intervention state or candidate-review loop. The proof does not establish reconnectability of arbitrary live Codex operations. Unknown operations stop for explicit resolution.

Fencing prevents current-result adoption; it cannot undo arbitrary external effects. Hufu is freshly checked before commit, but no general distributed transaction with authority revocation or external resource mutation is claimed. Other OS targets, power-loss/storage corruption and expanded graph/provider profiles remain unqualified. See [deferred work](deferred.md). The broad unqualified claim for arbitrary Penghou workflows is therefore not established by M1.
