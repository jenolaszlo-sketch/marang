# AAAW M6: Operational Readiness / Recovery Hardening

Status: **M6.1 complete for the bounded repository profile; later batches planned.**
See the [M6.1 qualification record](../aaaw-m6-1/qualification.md).
Direction recorded: 2026-10-16.

M6 asks what happens when the environment is damaged or unavailable. It does
not expand the workflow semantic model or declare production readiness.
Each batch needs its own bounded scope, reproducible failure evidence,
implementation review where changes are necessary, and qualification record.
This outline does not itself authorize a broad implementation batch.

## Immutable M5 starting point

| Anchor | Exact commit |
| --- | --- |
| Gagamba implementation | `0c1f05e199588622a5987f95a9149374f5bd858b` |
| Gagamba evidence | `2c8f4f7d7974510bac4baee9713acb6941b3ff09` |
| Marang qualified source | `b9df4b6fa9d1553b5f2df95dceebb699238ce935` |
| Marang final evidence | `5c3fb4b36e817b7abebd5e91cda8f0be4f7dd8e0` |

M1 durable continuation → M2 supervisor/revision continuity → M3 corrective
mutation → M4 dependent multi-effect reconciliation → M5 cross-platform
qualification. [M5 evidence](../aaaw-m5/gp-resume/qualification.md) defines the
supported repository profile and its limitations. Its original failures,
reviews, package hashes and successful results remain historical evidence.
Later commits must not move or replace these four anchors.

The Windows/macOS case-sensitive-root skip remains explicit; Linux exercised
that scenario. M6 must not eliminate a genuine capability distinction by
weakening or artificially bypassing the test.

## Proposed bounded batches

| Batch | Question and scope | Required evidence before closure |
| --- | --- | --- |
| **M6.1 Durable-state integrity and fail-closed recovery** | Detect missing, truncated, malformed or inconsistent durable records before bounded-profile recovery. Refuse uncertain state and preserve evidence. No repair, quarantine implementation, migration, backup/restore, resource-exhaustion or soak behavior. See the [authorized contract](../aaaw-m6-1/contract.md). | Controlled corruption counterexamples, explicit admission/refusal classifications, preservation of database/WAL bytes, no guessed approvals or authoritative results, and unchanged M5 recovery qualification. |
| **M6.2 Upgrade/schema compatibility** | Establish supported reader/writer/schema versions, migration ownership and compatibility policy. Exercise interruption and restart at migration boundaries. | Exact before/after versions, preserved backups, interrupted-upgrade fixtures, fail-closed handling of unsupported versions, and demonstrated recovery paths that preserve identity, revision lineage and effect evidence. |
| **M6.3 Operational failure and resource-exhaustion recovery** | Exercise bounded storage exhaustion, write/flush failures, unavailable dependencies, external repository modification and abrupt host loss where a real host-level fixture is available. | Native fault evidence with exact injection boundaries, cleanup/resource accounting, explicit uncertain-effect handling, and recovery from actual repository state without duplicate mutation. Process termination alone must not be labeled host-death or power-loss evidence. |
| **M6.4 Soak, observability and operator recovery drills** | Run finite longer-lived scenarios and make failures diagnosable. Rehearse backup/restore and documented operator decisions without reconstructing authority from logs or memory. | Declared duration/load/resource bounds, observable failure categories and lineage, independent restore/recovery drills, bounded resource trends, and an operator runbook validated against the exercised failures. |

Start with M6.1. M6.2 follows the integrity/ownership rules established there.
M6.3 uses the resulting recovery and compatibility contracts. M6.4 exercises
those contracts repeatedly and validates the operator procedures. Each batch
must choose finite scenarios and acceptance gates before implementation;
none is a claim about every storage engine, host failure or deployment.

## Contract and scope boundaries

- Preserve the M5 recovery, approval, fencing, lineage and effect-identity
  contracts. A newly exposed defect needs a documented counterexample and
  a bounded correction; it is not a reason to add workflow semantics.
- Never invent missing authority, silently discard contradictory evidence,
  or infer that an external mutation did not happen from missing local state.
  Recovery must retain uncertainty where the evidence cannot resolve it.
- Backup/restore must account for repository state and external effects;
  restoring an older journal is not permission to repeat a mutation.
- Schema/version policy and recovery ownership must be explicit before
  migrations become persistent compatibility obligations.
- Hufu and restricted-execution security workstreams receive no implementation
  scope from this outline. Any foundation change must be justified by a
  demonstrated missing contract and scoped separately.
- No generic compensation, arbitrary external exactly-once guarantee,
  distributed transaction model, arbitrary concurrent-writer support, package
  graduation or blanket production-readiness milestone is included.

Every batch should retain baseline failure, exact source/package/environment
identity, positive and negative controls, cleanup and recovery observations,
independent review, operator guidance, and explicit remaining limits. Passing
one batch advances the readiness evidence; it does not certify production use.
