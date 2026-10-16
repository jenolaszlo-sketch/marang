# M6.1 bounded recovery admission

For a known bounded repository workflow, call
`RepositoryRecoveryInspector.RequireAsync(executionPath, expectedWorkflowId,
additionalRequiredSqliteStores, cancellationToken)` before constructing any
initializing owner. The qualified multi-effect host supplies its execution
database, its expected workflow ID and its required authority database. The
guard also runs when an existing execution store or checkout binding is found;
explicit new-workflow initialization remains a separate host decision.

`InspectAsync` returns the same classification for callers that need a result
instead of an exception. `Ready` admits existing recovery checks. It grants no
permission to execute and does not claim that an external operation is absent.
Current authority, approval expiry, fencing and actual-repository reconciliation
remain mandatory after admission.

| Status | Meaning and operator action |
| --- | --- |
| `MissingStore` | A required store for a known workflow is absent. Stop recovery; do not initialize a replacement. |
| `DamagedStore` | Empty storage, malformed SQLite or failed SQLite integrity checks. Stop recovery and retain the original database and existing WAL evidence. |
| `Unavailable` | The existing store cannot be inspected read-only, including a nonregular file or access/locking failure. Stop recovery; access failure is not proof of corruption or of an unapplied effect. |
| `MissingStateShape` | Required pinned-profile tables/columns or the existing schema marker are absent or incompatible. Stop recovery; no migration or schema reconstruction is admitted. |
| `InconsistentState` | Stored documents, digests, lifecycle records or cross-record bindings disagree. Stop recovery; no authority, result or missing history is inferred. |

The result includes the store filename and a bounded reason code. Cancellation
propagates as cancellation. The host must surface refusal and keep workflow,
provider, validator, checkpoint, authority-publication and repository-mutation
paths unentered. Repeating recovery against the same damaged evidence must
refuse again.

This batch supplies no repair or recovery recipe for refused state. It does not
delete, quarantine, reconstruct, migrate or replace state, renew approval, retry
uncertain mutations, or implement backup/restore. Investigation and any future
repair require separately scoped work. Do not bypass refusal by labeling an
existing workflow as new.

The dedicated profile database is a single trust unit. Inspection uses a
consistent read transaction and reads committed WAL content. Database and WAL
content are preserved; SQLite shared memory is coordination state and may be
accessed by read-only readers.
For a WAL-mode main file with no sidecars, SQLite may materialize an empty WAL;
qualification permits no frames and no change to any existing WAL content.
The caller must retain the existing single-owner
recovery discipline: M6.1 does not fence arbitrary concurrent database writers.

Detection covers the declared pinned-profile invariants, not every possible
valid-but-false payload or a coherent rollback/rewrite of all redundant evidence.
It adds no independent tamper-proof anchor. Additional authority-store checks
are physical SQLite checks only; Hufu internals and authorization semantics are
unchanged. Later M6 work, including upgrades, backup/restore, resource failures
and soak behavior, remains outside this milestone.
