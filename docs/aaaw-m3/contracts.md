# M3 repository effect contract and migration

The APIs under `Marang.Runs.Effects` and explicit effect authoring are experimental host composition. `Marang.Runs` remains non-packable; there is no M3 package or new stable compatibility promise. The M1/M2 producer package versions are pinned unchanged in `eng/aaaw-m2-packages.json` and consumed through `NuGet.AAAWM1.Config`.

## Composition

Initialize the existing revision repository, M1 `SqliteDelegationRecoveryJournal`, M2 continuity/catch-up stores, supervisor authority adapter and `SqliteRepositoryEffectJournal` against the same owner database. The M1 recovery journal is required by M2 catch-up even when this scenario has no delegated branch. Compose `RepositoryEffectWorkflowBinding` with fresh Hufu request authorization, `RepositoryEffectExecutor` with the controlled checkout, and `AdaptiveSupervisor` with `RepositoryEffectRevisionGuard`. Omitting the guard rejects effect proposal, activation and cancellation. Raw repository mutation methods are internal; the public write entry point is the reconciliation executor.

Start from a clean, exclusively controlled Git checkout root. Allowlist one to four existing regular tracked paths. The profile rejects path aliases/traversal, symlinks/reparse ancestors, submodules, unmerged index stages, case collisions and bookkeeping paths. The snapshot binds canonical root identity, HEAD, logical stage-zero index entries and bytes of all tracked and meaningful untracked files. Git-ignored build outputs are excluded. The index file's transient stat cache is not contract identity. HEAD/index changes and unrelated meaningful edits invalidate the base. Later dirty bases are supported only as exactly observed, recognized successor states of recorded applied effects.

Prepare immutable `RepositoryEffectIntent` bytes before authoring a successor. Give it a new effect ID, attempt, execution ID and the exact workflow/revision/logical branch identity. The journal rejects reuse of an ID with different contents. Use `AuthorEffectAsync` and a specification with `IsRepositoryEffect=true`, `Input=intent.IntentDigest`, no dependencies and `AuthorityDigest=RepositoryEffectAuthorityProfile`. Other branches use that same authority profile marker. The effect input is a digest, not mutable patch text. The Fuwen port resolves only that exact journal intent and delegates to the keyed executor.

Use the normal supervisor proposal/context/intent/approval/commit flow. Record the exact objective used in Fuwen authoring as M2 intent metadata. The proposal binds the candidate and owner-state digest; the candidate binds the effect digest; the digest binds patch, base and scope. Resource requests add exact per-path `WriteFile` identity. Each write still requires current revision selection, unexpired exact approval and current Hufu grants. Approval of A against X cannot authorize B against Y. Fresh authority may deny a previously approved revision.

The host preserves actual Git path spelling in the intent and snapshot, while normalizing the authority resource path through Hufu's existing canonical path contract. The request identity still binds the exact original intent and target spelling. Case-colliding checkout paths are refused; this adapter does not broaden the resource grant or modify Hufu.

## Recovery and validation

Invoke the same effect key after restart. Never decide replay from absent success or model reasoning. `RepositoryEffectReconciler` compares durable expected states with the actual checkout. Definitely applied state does not enter the writer; definitely not applied may do so after all original gates. Partial/unresolved or previously parked state blocks. A restored file alone does not clear a durable block. All writers and cutovers must use the same checkout lease and the same owner journal; independent databases cannot coordinate ownership of one checkout.

Validation is a deterministic callback executed under the checkout lease. Return the observed state digest, precise validation target/command identity, outcome and output digest/evidence. Source bytes are observed before and after. A completed recorded validation is reused only for its exact recorded state. A killed validation can re-run; the mutation cannot. Validation commands must confine generated changes to excluded ignored outputs.

Mutation completion alone must never release the final workflow join. The host dispatches final work only after the current effect is `ValidationPassed` and its validation target still matches the observed expected post-state. A branch result describing failed validation or parked state is evidence for supervision, not workflow success. The qualified probe enforces this dispatch gate explicitly.

On failed validation, retain A's physical receipt and failed evidence. Observe actual post-A state, prepare B against it, and propose/approve/commit a normal corrective revision under the physical guard. Preserve only work with equivalent contract, immutable input, authority and provenance; accept completed reuse explicitly. B has independent authority and effect history. Neither fencing nor cancellation erases A.

## Schema and versioning

Journal tables are additive to M2. Existing read/report workflows need no effect migration and preserve their descriptor/routing fingerprints. Initializing the effect journal creates empty tables; it does not backfill physical effects from old execution records. Do not reinterpret an existing read activity as a writer. Upgrade an effect workflow only through explicit new admission.

Domain identities are versioned (`marang-repository-*/v1`, effect authority request v1, controlled profile v1). Incompatible snapshot/intent rules or table changes require explicit migration or a new profile version and conformance tests; never silently recompute admitted identities. No automatic upgrade/repair of ambiguous physical state is provided in M3.

See [ADR 0018](../decisions/0018-aaaw-m3-repository-effect-reconciliation.md) and the [operator guide](operator.md).
