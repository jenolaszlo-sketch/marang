# ADR 0019: Causal effect coordination in the bounded repository profile

Status: Experimental M4 host contract, 2026-10-15.

## Decision

Extend the Marang host around the qualified M3 writer. The existing Zhinu experimental revision owner already validates acyclic dependencies and conservatively propagates branch changes. No producer or stable foundation contract needs modification.

A causal effect binds one immutable M3 intent, explicit prerequisite effect IDs, their causal digests and expected result-state digests, and concise requirement evidence. An immutable revision graph selects one to eight causal effects, explicit reuse, reasons, an actual physical starting state and prior-effect dispositions. New effects have new identities in the new revision; adopted effects retain their original intent, execution identity and causal contract. The graph is included in the owner revision archive and exact approval digest. The archive extension is omitted for M1–M3, preserving their existing admission profiles.

The active Zhinu revision is the sole source of graph selection. The graph journal archives immutable graph/causal documents and evidence; it has no independent active-graph flag. Partial graph preparation may leave review metadata after restart; idempotent preparation repairs missing new intent rows, and activation requires every exact intent to exist. Preparation grants no physical authority.

## Physical lineage and semantic prerequisites

Physical transitions remain exact full-checkout snapshots under the M3 root-scoped lease. In the qualified sequence S0→A→S1→B→S2→C→S3, B and C are real, retained physical history even after invalidation. The corrective graph selects A→B2→C2, while its physical sequence starts at **S3**, then reaches S4 and S5. B2 does not pretend its base is A's old S1. Explicit `AcceptedAsBase` dispositions account for the old physical prefix separately from current semantic acceptance.

An effect being selected, physically applied, locally validated and accepted as a prerequisite are separate facts. Reuse requires an unchanged causal identity, explicit acceptance, equivalent Fuwen execution/input/authority/provenance, compatible current target hashes, local validation and recognized physical lineage. Changing an effect revokes downstream acceptance conservatively; matching labels or paths cannot preserve old work. Dispositions include preservation, physical-base acceptance, supersession, dependency invalidation, reconciliation and replacement requirements.

A fresh selection can explicitly bind `ReplacesEffectId` to an old parent effect, independently of logical labels. Impact analysis validates this mapping and emits replacement dispositions; the approved graph digest covers both. The declaration does not adopt old execution or authorize writes, and disposition output is not used as an input to manufacture its own proof.

There is no global execution lock. The existing M3 lease serializes writes to **one physical checkout**, because every patch binds its full exact state. Independent read work keeps the existing scheduling behavior. The milestone does not qualify concurrent writes within one checkout or add a distributed transaction system.

## Admission, authority and cutover

M4 has a distinct write descriptor, `aaaw.repository-causal-effect`, and authoring entry point. Its keyed protocol is safe to retry because deterministic reconciliation either recognizes prior work, refuses unsafe state, or permits an exactly based new write. It is never permission to attach an arbitrary writer. The M4 authority marker names the whole admitted host profile, including read branches; it does not turn read callables into writes.

`RepositoryEffectGraphRuntime` resolves only the active prepared graph and exact approved selection. It refuses downstream dispatch before prerequisite local validation, then checks selection, prerequisites, compatible outputs, exact base and fresh authority again while the M3 executor holds the checkout lease. Each new effect uses M3's durable intent, fresh per-file write checks, reconciliation, writer and local validation. Prerequisite reuse also obtains current Hufu `WriteFile` decisions bound to the active graph, dependent effect, prerequisite causal identity, actual physical base and target. Reuse is not a physical write.

The graph revision guard holds that same lease through proposal state capture or atomic activation. It verifies the actual physical base, exact prepared graph, deterministic prior dispositions, reconciled history and every adoption. A partial or unresolved physical effect parks the scope; logical cutover cannot clear it. Superseded prepared effects remain historical and cannot pass active selection. Cancellation fences logical contribution and retains physical effects.

## Validation and supervision

Effect-local validation proves its immediate state and is required for downstream use. Full workflow validation is a separate immutable ledger bound to the graph digest and exact terminal physical state. Both observe before/after under the checkout lease. Full failure does not rewrite local success or erase patches; it drives a normally approved corrective graph. Full validation adds semantic owner witnesses and M2 meaningful evidence. Authority timestamps alone do not invalidate review.

Authenticated catch-up, revision drill-down and effect explanation combine current owner selection with actual checkout observation and recorded history. They explain accepted prerequisites, named blocking ancestors, local versus workflow validation, original versus replacement identities and why an applied historical effect is no longer accepted. A view never grants replay permission.

## Deliberate boundary

M4 remains a non-packable, unpublished host experiment on the bounded Windows checkout profile. Keep Siming, Workflow.Abstractions, Cangjie and IO foundations closed. Hufu and Gagamba implementations remain unchanged. No compensation, rollback, automatic merge, publication, deployment, arbitrary filesystem effects, power-loss guarantee, distributed coordination or new platform qualification is added.
