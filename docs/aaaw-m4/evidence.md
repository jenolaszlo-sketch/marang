# AAAW M4 qualification — 2026-10-15

M4 is complete for the experimental, exclusively controlled Windows repository profile. The qualified workflow applies A→B→C, retains their physical history after full validation fails, explicitly preserves A, and executes an independently approved A→B2→C2 corrective graph against the actual checkout. All five effects apply once; the independent inventory runs once. The final repository passes validation and the current final branch completes.

The evidence supports this bounded claim: **Penghou can coordinate dependent durable repository effects across process restart, preserve compatible completed effects, invalidate downstream effects when causal assumptions change, and execute an approved corrective graph against actual physical state without duplicate mutation.** Qualification covers a dependent chain and its correction, rather than arbitrary effect topologies or external systems.

| Area | Result | Evidence | Limits |
|---|---|---|---|
| Effect dependency identity | Passed: immutable intents bind explicit prerequisite effect IDs, causal/result digests and requirement evidence | P1; `RepositoryEffectGraphContractsTests`; both archived graphs in [machine evidence](qualification.json) | Experimental host schema; 1–8 selected effects |
| Repository state lineage | Passed: every base/post transition is exact; B2 starts at C's actual S3 | P2/P9; native `ExactStateLineage`; five base/observed receipts | One checkout; HEAD, logical index and meaningful file hashes; ignored outputs excluded |
| Sequential multi-effect execution | Passed: A→B→C and then B2→C2 execute in their required order | P3; five application counts of 1; local validation receipts | Serial physical lineage; no concurrent-write qualification |
| Prerequisite validation | Passed: incomplete validation, wrong base, foreign causal identity and revoked prerequisite authority refuse writes | P4; nine `RepositoryEffectGraphRuntimeTests`; fresh transitive prerequisite authority ledger | Existing Hufu semantics; authenticated host context |
| Restart between effects | Passed: validated upstream receipts survive fresh hosts without replay | P5; K3/K5/K10 record `ValidationPassed` before killing the host | Native kill/restart proof uses .NET 10 on Windows |
| Upstream preservation | Passed: A retains its original effect, causal and execution identities; inventory remains completed | P6; R3 explicit reuse; inventory invocation count 1; [catch-up](catch-up.json) | Adoption requires compatible outputs, exact contract and recognized lineage |
| Downstream invalidation | Passed: B is replaced; C is invalidated because it depended on B | P7; explicit B→B2/C→C2 mappings; impact tests; [corrective drill-down](corrective-drill-down.json) | Conservative deterministic rules; no inferred semantic repair |
| Physical-state reconciliation | Passed: R3 activation retains physical S3 containing original A/B/C | P8; K9 checkout snapshot; B2 base equals C's observed post-state | Logical invalidation never rolls files back |
| Stale prepared effects | Passed: prepared old B cannot write after approved cutover selects B2 | P11; `Prepared_old_B_is_not_replayed_after_approved_cutover_preserves_validated_A_and_selects_B2` | Old identity remains historical; replacement gets a fresh identity |
| Applied invalidated effects | Passed: B/C keep applied, locally validated receipts while losing active acceptance | P12; historical rows and replacement IDs in both captured views | Historical validation belongs to its original state and graph |
| Partial/unresolved blocking | Passed: partial B blocks named C with zero C writes; genuinely unknown prerequisite state also blocks descendants | P13/P14; K4 [partial catch-up](partial-catch-up.json); real reconciliation runtime tests | Scope remains parked; no automatic repair or block clearing |
| Corrective effect graph | Passed: local success/full failure remain distinct; approved fresh B2/C2 target S3/S4 and reach passed S5 | P10/P15/P19; [failed R2 validation](original-workflow-validation.json), [passed R3 validation](corrective-workflow-validation.json) | Validation is exact-state bound; callbacks may not change meaningful files |
| Catch-up | Passed: authenticated view separates current acceptance, physical history, local validation and workflow validation | P16; [final view](catch-up.json); supervisor tests | View grants no replay authority; unapproved prepared graphs do not supersede active work |
| Drill-down | Passed: R2→R3 explains preserved A, replaced B and invalidated C, with actual base and causal reasons | P17; [captured transition](corrective-drill-down.json); authenticated effect explanation tests | Preserves original revision and state bindings on historical evidence |
| Kill barriers | Passed: all K1–K12 have killed-process IDs and frozen context/state receipts | Twelve entries in [machine evidence](qualification.json); K12 includes a live validator child PID | Process kills, not power loss or distributed transactions |
| M1–M3 regression | Passed: all previous 177/219 consumer cases remain included | P20; complete 201/245 final TRX totals/checksums; strengthened M1 captured-handle barrier | No producer API, package version or stable foundation change |
| End-to-end demonstration | Passed: A→B→C fails the broader goal; A→B2→C2 passes; five writes and one inventory invocation | P3/P18/P19; two native M4 tests; current accepted set exactly A/B2/C2 | Four allowed existing tracked paths in one disposable pinned repository |

## Exact repositories and commits

| Repository | Exact commit | Use |
|---|---|---|
| Marang M4 | `65906df15dc8b698f26327f6f8576346987c02a1` | Clean qualified source on local `codex/aaaw-m4-marang` |
| Marang M4 implementation | `d38c9e136c9a87679384b296797123d0f332334d` | Causal graph/runtime, supervisor, native scenario, guidance and qualification scripts |
| Marang M3 | `e91516adcee018ee5e10bbd6bf8eeae25aca1c6e` | Parent evidence head; M3 executable implementation is `83f5d49f55fa650a8c158aa39ea958c3854fc555` |
| Marang M2 | `a0916534d0e82e499cffb2dea526b64357eb32e2` | Immutable source for disposable branch `m4-multi-effect-qualification` |
| Penghou.Zhinu | `07e1807cc4b867dd4f527445aa108f56135d37db` | Unchanged core/SQLite `0.2.0-preview.4-aaaw-m1.3` |
| Penghou.Qingniao | `f08971df33916be184fd1bebb5940ba2bd660a49` | Unchanged abstractions/core `0.1.0-preview.5-aaaw-m1.4` |
| Penghou.Fuwen | `603b2e78d3f3ff33e2152641ab0ca016a120552e` | Existing Fuwen/Zhinu adapter `0.1.0-preview.13` |
| Penghou.Hufu | `c0ef75afda4ba2562e3252d4e54cd536e37f428d` | Existing core/SQLite `0.1.0-preview.6` |
| Penghou.Hufu.Cedar | `3206c4447add6ef0821fb70f118b638598d9aac0` | Existing `0.1.0-preview.3` |
| Penghou.Hongxian | `325d8011d4731092ced0cb5acab6301444840ab0` | Existing `0.1.0-preview.5`; earlier mirroring consumers remain passing |

The final qualification consumed the four original M1 package files with their locked SHA-256 hashes and producer metadata verified. No source dependency was substituted for those packages. The evidence follow-up changes documentation only. Original user checkouts and prior milestone branches remain untouched.

## New durable contracts and ownership

`RepositoryCausalEffect` adds explicit prerequisite identities/result digests and canonical requirement evidence around an existing immutable M3 intent. `RepositoryEffectGraph` binds the actual physical base, selected causal effects, explicit reuse, optional `ReplacesEffectId` mappings and deterministic prior-effect dispositions. Renaming a logical step cannot lose its replacement relationship or silently preserve an old causal assumption.

Four additive SQLite tables archive graphs, causal effects, full workflow validations and prerequisite-authority receipts. The graph journal has no independent active flag: the existing atomic Zhinu revision owner and its verified immutable archive select the graph. Preparation alone grants no execution authority. Idempotent preparation repairs missing intent preparation after interruption; activation requires every selected identity to match. M1–M3 archives omit the optional graph extension and retain their original admission profiles.

`AuthorEffectGraphAsync` admits the distinct keyed `aaaw.repository-causal-effect` descriptor through the existing Fuwen/Zhinu factory. Its runtime and revision guard share the existing checkout lease with the M3 writer. They check exact approval, selection, validated prerequisites, compatible outputs and physical base before mutation. New effects use current per-target Hufu checks; retained prerequisites receive fresh transitive authorization bound to graph, dependent effect, causal identity, current physical base and target. No raw writer is exposed as an ordinary activity.

Local validation remains on each physical effect receipt. Broader validation is an immutable graph/state ledger and projects meaningful M2 evidence and owner witnesses. Authority-check timestamps do not manufacture semantic changes. Read-only supervisor projections use committed owner history, preserve historical receipts and distinguish selected work from accepted prerequisites. A pending proposal cannot make an active effect appear already superseded.

See [ADR 0019](../decisions/0019-aaaw-m4-causal-effect-coordination.md), [contracts and migration](contracts.md) and [operator guidance](operator.md). These remain experimental host contracts in a non-packable project. No SemVer package release or new stable compatibility guarantee is introduced; incompatible future identity/schema changes require explicit versioning and migration.

## Qualification

`eng/qualify-aaaw-m4.ps1 -ReuseQualifiedPackages` passed against the clean qualified commit, and verified that source remained unchanged during the run. It verified package hashes/metadata, restored the consumer graph, built the solution with **0 warnings / 0 errors**, passed formatting verification and ran every test.

- .NET 8: **201 passed, 0 failed, 0 skipped**.
- .NET 10: **245 passed, 0 failed, 0 skipped**.
- M4 adds **24** contract/runtime/supervisor cases per framework and **2** .NET 10 native process cases. All **177/219** existing M1–M3 cases remain included.
- Windows build **10.0.26200.0**, SDK **10.0.401**, runtimes **8.0.31 / 10.0.12**.

The first full run exposed an older M1 qualification race: `replacement.started` could precede durable delegated handle capture. Recovery correctly refused the unknown outcome. Commit `65906df` strengthens that fixture to wait for captured handle plus observation, retries dispatch of the same run while recovering its lease, and checks a capture receipt before killing. It does not change Qingniao recovery or permit respawning unknown provider work. All nine M1 adaptation process cases passed separately, followed by the complete final suite above.

The M4 harness also bounds process completion and redirected-output draining, disables reusable SDK servers for owned validation children, and uses short isolated checkout paths for native SQLite loading. These are qualification mechanics, not broader effect guarantees.

Only fixtures created during the successful final qualification are selected in [qualification.json](qualification.json). It records TRX checksums, all killed host IDs, graph/intent/patch/state identities, per-effect and prerequisite authority, dispositions and validation. Failed exploratory fixtures remain ignored local artifacts and are excluded. The captured views and validation outputs here were extracted from that same final scenario; raw scenario checksums were verified during capture. CI is configured to upload M4 artifacts; no remote CI run is claimed.

A read-only audit of the captured evidence independently checked the five state transitions, explicit replacement identities, active acceptance/reuse, R2→R3 drill-down, named partial blocking and all twelve receipts. It recomputed prerequisite-authority request identities and every local-validation output hash, verified original/corrective validation targets and confirmed all report links resolve. No workflow or checkout state was changed by this audit.

## Physical history and accepted graph

A modifies the tests project to support an explicit custom fingerprint-validation mode. B changes the implementation and README to the tentative 63-character assumption. C adds a matching conditional test. A/B/C each pass their immediate validation, including C's compiled custom test. The independent broader suite then reports **8 failures / 175 passes**: existing `WorkflowPlanResolutionTests` consumers require canonical 64-character fingerprints.

R3 explicitly adopts unchanged A and preserves the immutable-source inventory execution `1a2f48ab-9877-4ee4-9363-40f8d5b34323`. B2 replaces the implementation/README against actual S3; C2 replaces the generated test against S4. The custom 64-character test passes and the broader suite passes **183/183**. The qualified branch keeps its original HEAD and logical index; effects change tracked contents, rather than manufacturing Git history.

```text
Physical:    S0 --A--> S1 --B--> S2 --C--> S3 --B2--> S4 --C2--> S5
Accepted R2: A → B → C
Accepted R3: A → B2 → C2
```

| Effect / origin | Base | Recorded post | Current disposition / writes |
|---|---|---|---|
| A / R2 `bf668e4e-721b-4611-aae6-e2712dc5a881` | S0 `b84aa1201e2f…` | S1 `821b7f5f19b7…` | Preserved and explicitly adopted; 1 |
| B / R2 `bb288558-11d7-4a4d-91e2-a4332cecedc8` | S1 | S2 `8d4e8b3605a9…` | Invalidated/superseded, physical base retained, replaced by B2; 1 |
| C / R2 `912081ef-5f01-4d72-890a-75a381f6abdd` | S2 | S3 `58229713cd0b…` | Invalidated because B changed, replaced by C2; 1 |
| B2 / R3 `58c9b29c-6130-4111-94d6-a5519237ee56` | Actual S3 | S4 `74383545a53e…` | Accepted; 1 |
| C2 / R3 `47524323-5446-4d12-aa2a-638679eb87e2` | S4 | S5 `2cd7a9512d50…` | Accepted; 1 |

Full hashes and explicit replacement/prerequisite IDs are in machine evidence. B/C retain their original locally passed receipts even though their outputs were later corrected. Historical per-effect validation remains bound to its original state/graph; the separate R3 workflow ledger proves current S5. Local command outputs are captured for [A](local-validation-A.json), [B](local-validation-B.json), [C](local-validation-C.json), [B2](local-validation-B2.json) and [C2](local-validation-C2.json).

## Restart receipts

| Barrier | Durable/physical observation before kill | Qualified continuation |
|---|---|---|
| K1 | A `IntentCommitted`, checkout S0 | Fresh checks, single A application |
| K2 | A written at S1, receipt not yet recorded | Recognize applied A; no second write |
| K3 | A locally `ValidationPassed` at S1 | Begin B once |
| K4 | First of B's two file replacements | Reconcile partial B, park scope; C remains Prepared, blocked by B; C writes 0 |
| K5 | B locally validated at S2 | Begin C against S2 |
| K6 | C locally validated, actual S3 | Resume broader validation without effect replay |
| K7 | R2 failed workflow validation recorded | Preserve A/B/C physical lineage and failure context |
| K8 | Exact R3 proposal pending, R2 still active | Recover original proposal digest before approval |
| K9 | Approved R3 active, actual checkout still S3 | Retain dispositions; apply new B2 |
| K10 | B2 locally validated at S4 | Begin C2 once |
| K11 | Corrective effects validated at S5 | Resume final validation |
| K12 | Final validator child actually running at S5 | Kill child tree; rerun validation with no effect replay |

Eleven kills continue one workflow through K1/K2/K3/K5–K12. K4 is a separate partial-effect fixture. Both are actual native process-kill tests, not injected logical restart flags.

## Deliberate limits and stop

No Siming, Workflow.Abstractions, Cangjie current-store, IO.Identity/Protocols, Hufu or Gagamba producer implementation changed. No stable foundation was reopened. Hufu remains a dependency exercised through its existing API.

This is one controlled checkout and owner journal, with the existing four-path allowlist, regular tracked replacements and 1 MiB per-effect patch-content bound. The qualified scenario selects three effects per graph and retains five historical effects. Concurrent writes, arbitrary branch/merge topologies, overwriting accepted upstream outputs, other operating systems and power-loss durability remain unqualified. Model output never decides physical classification or replay safety.

No generic compensation, rollback, automatic merge, partial/unresolved repair, arbitrary external-effect support, repository commit/publication effect or distributed transaction was added. There is no general exactly-once external-system claim. Source and evidence are recorded in local commits only: **nothing was pushed, and no package, tag, PR or deployment was published.** M4 stops at this qualified boundary.
