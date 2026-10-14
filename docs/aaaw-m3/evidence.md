# AAAW M3 qualification — 2026-10-14

M3 is complete for an experimental, exclusively controlled Git checkout on Windows x64. Within this profile, Penghou durably identifies and authorizes an immutable replacement, reconciles actual repository state after process restart, refuses unsafe replay, validates the exact resulting state and continues through an independently approved corrective revision without duplicating either mutation. Fencing a result never erases changed files.

| Area | Result | Evidence | Limits |
|---|---|---|---|
| Effect identity | Passed: workflow/revision/branch/execution/attempt/effect/scope and creation instant have one immutable digest | `RepositoryEffectContractsTests`; E1/E2 in [machine evidence](qualification.json) | One intended attempt in one fixed checkout; moving the checkout changes identity |
| Immutable patch | Passed: exact target paths and before/after bytes bind to base and intent; ID rebinding and tampering are rejected | Contract/journal tests; two independently identified source/README patches | One to four existing tracked regular replacements; at most 1 MiB total before+after bytes |
| Base-state binding | Passed: HEAD, logical index and all meaningful file bytes must match; stale state cannot rebase automatically | Changed-base executor and cutover tests; B base equals observed A post-state | Ignored build outputs excluded; no arbitrary dirty checkout admission |
| Authority binding | Passed: exact approved manifest and fresh current Cedar/Hufu checks bind each write target to the effect digest | Real Cedar/SQLite grant/revocation tests; persisted E1/E2 per-target evidence | Host authenticates context; no distributed grant/resource transaction guarantee |
| Intent-before-effect | Passed: Prepared cannot write; IntentCommitted/Applying must be durable before replacement | Durable-gate test; K1/K2/K9 journal and physical target receipts | Preparation is review metadata, not permission |
| Before/after state | Passed: full base/expected/observed snapshots and target hashes retain physical truth | Persistent effect journals and K4/K10 target receipts | Recognition proves consistency with the intent; no claim that Git itself provides effect semantics |
| Restart classification | Passed: definitely not applied, definitely applied, partial and unresolved are explicit deterministic outcomes | Reconciler conformance; K2/K3/K4/K10; changed-content executor test | Unreadable/out-of-profile checkout fails closed and must regain inspectability |
| Safe replay | Passed: only exact base plus current selection, approval and fresh authority may write; applied effects do not repeat | Nine successive kills in one workflow; first/second application counts both 1 | Keyed repository reconciliation protocol, not general exactly-once effects |
| Partial effect | Passed: killing after first replacement parks the patch and blocks later writes | K3; two fresh recovery processes keep identical target hashes and apply count 1; conflicting-candidate/guard tests | No automatic full-patch replay, partial repair or block-clearing command |
| Unresolved effect | Passed: unknown target/unrelated state and stale validation are parked; restored files cannot clear a durable block | Executor, validation and permanent-block integration tests; explicit catch-up decision | Inspection/discussion only; deliberate resolution is future work |
| Validation binding | Passed: before/after observation and target digest prevent validation for another state being adopted | Wrong-digest and source-change callback tests; actual source validation logs | Deterministic validator must confine generated changes to ignored outputs |
| Corrective revision | Passed: failed A remains historical; approved B binds its actual post-state, new identity and fresh authority | R2→R3 owner history; K8 exact pending proposal survives; K9/K10 | No rollback/compensation or mutation of A's record into B |
| Unaffected work | Passed: meaningful immutable-source inventory preserves one execution/result through R1/R2/R3 and runs once | Inventory ID/result and invocation log; immutable Git-tree source hashes | Preserved evidence describes the pinned original source, not the changing current checkout |
| Catch-up/drill-down | Passed: operator sees applied failed A, current B, physical state, validation, pending decisions and authoritative work | [captured view](catch-up.json); [R2→R3 drill-down](corrective-drill-down.json); real SQLite integration tests; K3/K8 | Authenticated host projection over pinned experimental owner schemas; views grant no replay permission |
| Kill barriers | Passed: all K1–K10 have killed-process IDs and frozen state/context receipts | [machine evidence](qualification.json); two .NET 10 process tests | Windows process-kill/restart; power loss and other operating systems are unqualified |
| End-to-end scenario | Passed: real A fails, separately approved B passes, inventory survives and final join completes | Final nine-kill fixture; all 219 .NET 10 consumer tests | Two-file checkout profile; no arbitrary external effects or general workflow graph |

## Exact sources and consumers

| Repository | Exact commit | Branch / use |
|---|---|---|
| Marang M3 | `83f5d49f55fa650a8c158aa39ea958c3854fc555` | Local `codex/aaaw-m3-marang`; qualified implementation |
| Marang M2 | `a0916534d0e82e499cffb2dea526b64357eb32e2` | Immutable source of the disposable mutation/validation checkout |
| Penghou.Zhinu | `07e1807cc4b867dd4f527445aa108f56135d37db` | Unchanged M1 core/SQLite `0.2.0-preview.4-aaaw-m1.3` |
| Penghou.Qingniao | `f08971df33916be184fd1bebb5940ba2bd660a49` | Unchanged M1 abstractions/core `0.1.0-preview.5-aaaw-m1.4` |
| Penghou.Fuwen | `603b2e78d3f3ff33e2152641ab0ca016a120552e` | Existing Fuwen/Zhinu adapter `0.1.0-preview.13` |
| Penghou.Hufu | `c0ef75afda4ba2562e3252d4e54cd536e37f428d` | Existing core/SQLite `0.1.0-preview.6`; no producer implementation changes |
| Penghou.Hufu.Cedar | `3206c4447add6ef0821fb70f118b638598d9aac0` | Existing `0.1.0-preview.3`; no producer implementation changes |
| Penghou.Hongxian | `325d8011d4731092ced0cb5acab6301444840ab0` | Existing `0.1.0-preview.5`; old M1/M2 mirroring tests remain passing |

The implementation was recorded locally before final qualification. This documentation follow-up contains captured evidence and does not change executable source. Original user checkouts and prior milestone branches are untouched. No Siming, Workflow.Abstractions, Cangjie, IO.Identity/Protocols, Hufu or Gagamba producer implementation was changed. No package, tag, PR, push or remote deployment was produced by M3.

## Qualification and physical lineage

`./eng/qualify-aaaw-m3.ps1 -ReuseQualifiedPackages` verified all four original M1 package SHA-256 hashes and NuGet producer commits, restored the actual consumer graph, built the complete solution with **zero warnings/errors**, passed formatting verification and ran every consumer test. SDK: **10.0.401**, runtimes .NET **8.0.31 / 10.0.12**.

- .NET 8: **177 passed, 0 failed, 0 skipped**.
- .NET 10: **219 passed, 0 failed, 0 skipped**.
- All 143/183 existing M1/M2 cases remain included and pass.
- M3 adds 34 semantic/conformance cases per framework and two .NET 10 process cases covering all ten required barriers.

TRX identities/checksums and native process IDs are in [qualification.json](qualification.json). Only fixtures created during this final qualification are selected. Failed exploratory fixtures remain in ignored local artifacts and are excluded. CI is configured to upload M3 artifacts; a remote CI run is not claimed.

A separate read-only package consumer called the public effect `DrillDownAsync` against this same qualified durable fixture. It verified R2→R3, both physical effects, A's applied-then-corrected status, B's current passed validation and preserved inventory; its captured [corrective view](corrective-drill-down.json) retains the logical and physical transition together. No implementation or repository state was changed for this capture.

The fixture branch is `m3-effect-qualification`. HEAD and logical index remain pinned to M2. Patch A replaces `src/Marang/WorkflowPlanReference.cs` and `README.md`: a tentative extraction uses fingerprint length 63. Existing real `WorkflowPlanResolutionTests` fail; an additional probe compiles the actual checkout source and checks canonical 64-character acceptance plus rejection of 63-character and uppercase inputs. B targets the exact observed post-A snapshot and changes the constant/note to 64. Both validation outcomes retain command, exact target state, output SHA-256 and full stdout/stderr location.

| Revision / effect | Base | Observed post | Validation / physical writes |
|---|---|---|---|
| R2 / E1 `5271bf9e-2940-4ed0-a88a-17d15b8e468a` | `00eefbff43a7…` (S1) | `f24421b437dc…` (S2) | Failed; one application round |
| R3 / E2 `df1b88a1-1993-44df-9afd-6c0c472464d0` | `f24421b437dc…` (S2) | `413b52f3bc8f…` (S3) | Passed; one application round |

Full state, patch, intent, repository and authority identities are retained in machine evidence. E1's recorded S2 receipt remains distinct from current S3. Its result is historical after R3; physical A is described as `AppliedThenCorrected`. Inventory execution `c39d3bb2-3a12-45f4-8b84-1ed80886be80` runs once. R2's final result is fenced/cancelled; only the validated R3 final join completes. Journal JSON enum codes are lifecycle 0 Prepared, 1 IntentCommitted, 2 Applying, 3 AppliedObserved, 4 ValidationPassed, 5 ValidationFailed, 6 Blocked; reconciliation 0 DefinitelyNotApplied, 1 DefinitelyApplied, 2 PartiallyApplied, 3 Unresolved.

| Barrier | Physical/owner boundary and verified recovery |
|---|---|
| K1 | Before durable intent commitment: prepared metadata, unchanged files |
| K2 | Intent committed before write: exact base; fresh gated replay |
| K3 | First replacement complete: recognized partial, parked across two fresh recoveries |
| K4 | All A writes before applied receipt: post-state recognized, no repeat |
| K5 | Applied receipt before validation: exact-state validation resumes |
| K6 | Real validation command running in a separate native child: host/tree killed; validation re-runs, A does not |
| K7 | Failed validation durable before corrective proposal: A's failed applied state retained |
| K8 | Corrective proposal awaiting approval: exact proposal ID/candidate/digest and pending decision survive |
| K9 | B intent committed before B writes: B independently reconciles and passes fresh gates |
| K10 | All B writes before its applied receipt: B recognized without repeat; validation/final join complete |

K1/K2/K4–K10 are successive interruptions of **one continuing workflow**, not nine unrelated successful runs. K3 is separate because its correct outcome is permanent parking within M3.

## Required probe mapping

| Probe | Passing evidence |
|---|---|
| P1 Immutable binding | Intent/hash/tamper/rebinding tests; E1/E2 immutable bytes and manifest input |
| P2 Base mismatch | Actual executor changed-base test and stale cutover guard test |
| P3 Pre-effect intent | Writer durable-gate test; K1/K2/K9 |
| P4 Not-applied recovery | K2/K9; original preconditions and new authority checked before writes |
| P5 Applied recovery | K4/K10; post recognition without mutation |
| P6 Partial application | K3; unchanged target hashes over two recovered hosts; conflicting write refusal |
| P7 Unresolved state | Unexpected content, validation mutation/digest mismatch, and durable parked-state tests |
| P8 Validation binding | Exact before/after state tests and actual checkout validation records |
| P9 Adaptive correction | Normal R3 proposal/approval/commit after actual A validation failure |
| P10 Corrective authority | E2 fresh per-target Cedar/Hufu evidence; real revocation/expiry/current-selection tests |
| P11 No duplicate | One A and one B application through nine kills; partial never replayed |
| P12 Preservation | Original immutable inventory result/identity retained; one invocation |
| P13 Catch-up | Durable M2 intent/proposal plus effect projection; K3/K8 and captured view |
| P14 Drill-down | Historical S2/failed E1 remains visible while validated S3/E2 is current |
| P15 Completion | B validation passes; exact-state final dispatch gate; current final join completes |

## Durable contracts and deliberate boundary

[ADR 0018](../decisions/0018-aaaw-m3-repository-effect-reconciliation.md), [contract/migration guidance](contracts.md) and [operator guidance](operator.md) define immutable intent/state identities, additive append-only effect tables, semantic execution witnesses, exact manifest binding, a checkout-scoped writer/cutover lease and authenticated physical catch-up. These belong to the non-packable experimental host. No foundation graduation or package version change is needed.

The Fuwen effect descriptor declares a write; its safe retry/idempotency contract applies to the **keyed reconcile-before-replay wrapper**. It cannot be substituted with an unkeyed writer. The original read/report descriptor and routing remain unchanged. M1 ownership, exact approval, conservative preservation and fencing are preserved; M2 intent, stale decision detection, catch-up and delegation recovery are preserved. Mutation completion cannot release the final join without current exact-state validation.

Deferred: arbitrary external effects, generic compensation, rollback, merge/rebase, Git publication/history changes, distributed authority/effect transactions, power-loss guarantees, automatic partial/unresolved resolution, arbitrary dirty trees, relocation, broader graphs and sandbox qualification. External editors must not race the controlled checkout. Hufu's existing normalized path contract is honored by the new host adapter; Hufu and Gagamba remain unchanged.

The supported result is bounded repository reconciliation. Penghou does not infer “no effect happened” from a missing completion record, and does not claim general exactly-once external mutation.
