# M4 causal effect contract and migration

M4 adds experimental host coordination around M3. It does not release a package or change producer versions. Use the same locked Fuwen, Zhinu, Qingniao, Hufu and Hongxian packages as M3. M1–M3 archives remain readable; absent graph metadata means their original profile.

## Composition and admission

Initialize the existing revision owner, M1 recovery journal, M2 continuity/catch-up stores, M3 effect journal and the new `SqliteRepositoryEffectGraphJournal` against one owner database. Open one exclusively controlled clean Git checkout using the existing M3 four-path allowlist and 1 MiB replacement bounds. Compose `RepositoryEffectGraphRuntime` with current Hufu authorization and `RepositoryEffectGraphRevisionGuard` with the same physical checkout and journals. The ordinary single-effect M3 guard does not admit causal graphs.

Create every fresh M3 intent with its own effect/execution/attempt identity and exact predicted base. Wrap it in `RepositoryCausalEffect`, explicitly naming prerequisite identities, causal digests and result-state digests. Build an immutable `RepositoryEffectGraph` with actual initial physical state, selected effects and explicit reuse. Call `PrepareGraphAsync` before authoring/proposing; it is safe to repeat after interruption. A graph row alone is not complete admission.

Use `AdaptivePlanAuthoring.AuthorEffectGraphAsync`. Each effect specification has `IsRepositoryEffect=true`, input equal to its causal digest, the exact intent execution ID, reuse matching selection, and logical dependencies matching the causal edges. All branches carry `RepositoryEffectGraphAuthorityProfile`; read callables remain read-only. The ordinary entry points retain their older bounded shapes. Store the exact authored objective in M2 intent metadata and use the normal proposal, review, explicit approval and commit flow.

Host ports dispatch the causal descriptor to `RepositoryEffectGraphRuntime.ExecuteAsync`, resolving only the immutable selected causal digest. Do not substitute an unkeyed writer. A downstream call can return a named prerequisite refusal without committing mutation intent. Resource checks before every write still come from current Hufu policy, normalized path identities and exact immutable intent binding.

## Acceptance and replacement

Selected is not applied or accepted. Downstream use requires locally validated prerequisite receipts, exact causal selection and compatible observed outputs. A recorded applied receipt remains historical even if its output later becomes invalid or is overwritten by an explicit correction. Catch-up keeps recorded old post-states distinct from actual current checkout state.

To correct a graph, reconcile the real checkout, generate fresh replacement intents against that state and their exact subsequent predicted states, and explicitly select only still-valid prior effects for reuse. Derive prior dispositions with `RepositoryEffectGraphImpact.Analyze`; the physical guard checks them again. The old prefix accepted as a new physical base may contain effects that are no longer semantically accepted. No rollback is implied.

Bind a fresh selection's optional `ReplacesEffectId` to the exact old effect when a replacement changes its logical label, as B→B2 does. The graph digest and approval cover this declaration. Impact analysis checks parent membership and unique replacement; it never infers replacement from matching paths or trusts hand-written disposition output as its own proof.

Keep the adopted effect's logical identity, requirement, input, authority, provenance, dependencies and execution contract equivalent. Retaining a step label while changing a prerequisite is insufficient. A false preservation claim fails authoring, impact or activation checks. A superseded prepared effect ID is never recycled for its replacement.

## Validation and restart

Return deterministic local `RepositoryEffectValidation` from the effect callback. It binds to the immediate expected post-state. After all effects are accepted, call `ValidateWorkflowAsync` for the broader goal; its separate result binds graph digest and terminal physical state and remains immutable for that graph. Generated validator output must stay within M3's ignored-output rules. Do not complete final workflow work unless full validation passed for the still-current observed state.

On restart use the same effect IDs. M3's four physical classifications remain authoritative; missing completion is never proof of no write. Already recorded upstream work follows recognized physical lineage and is not replayed. Partial/unresolved prerequisites block their descendants by name and retain scope-wide M3 parking. There is no M4 command that automatically repairs or clears such state.

Graph, causal, validation and prerequisite-authority tables are additive. No independent acceptance state is copied outside the immutable owner archive. The optional extra authority evidence fields are absent in older records and default to null on read. Future incompatible graph/identity/schema changes require explicit versioning/migration and conformance qualification, not silent identity recomputation.

The qualified topology is a dependent mutation chain and its approved corrective chain, alongside unaffected read work. Fresh effects form one serial physical lineage. This milestone does not qualify arbitrary branch/merge topologies or concurrent writes; retained prerequisite outputs must still be present, so intentionally overwriting an accepted upstream output requires a different explicit contract.

See [ADR 0019](../decisions/0019-aaaw-m4-causal-effect-coordination.md) for ownership and the bounded claim.
