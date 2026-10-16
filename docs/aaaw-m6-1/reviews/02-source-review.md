# M6.1 source review — preliminary disposition

Date: 2026-10-16. Candidate is the in-progress M6.1 branch based on
`9638e2a37524ba6999c4e7df6016851578ec7fc8`. This is a read-only source review;
it is not a qualification result.

## Current blocker

The validator now checks the `adaptive_provider_handles` index in both
directions: each indexed row must match a captured attempt, and each captured
attempt must have an index row. That closes the deleted-handle projection gap.
The reverse relation for `adaptive_branch_delegations` is still missing. The
pinned M5 writer binds an accepted delegation to its execution before the first
provider pump; the pump then writes `adaptive_delegation_attempts`. Therefore an
attempt without its exact branch binding is inconsistent for this profile.
`SupervisorCatchUp` discovers attempts through an inner join on that binding,
so the missing projection can hide a durable uncertain external operation.
Require every existing attempt to have the matching execution/delegation
binding and request digest. Preserve the legitimate binding-without-attempt
state, which can occur if the process stops after acceptance/binding but before
the provider pump. Add a missing-binding refusal fixture plus a healthy
binding-only positive fixture.

Until that projection link and a focused test exist, the recovery inspector
does not fully establish the execution-to-provider recovery relation required
for the bounded fail-closed claim.

## Reviewed source behavior

- `RepositoryRecoveryInspector` opens the existing database read-only with
  private cache and pooling disabled, establishes a read transaction, runs
  SQLite integrity and foreign-key checks, checks the pinned Zhinu schema
  marker/version and required tables, then validates logical records. It
  checks optional authority stores physically without parsing Hufu internals.
- The main entry guard runs before directory creation, process admission,
  runtime/database initialization, checkout creation, and downstream work for
  resume/reconcile or an existing store/checkout marker. The explicit fresh
  workflow path remains the M5 path.
- Effect events and projections are validated through the journal reader;
  retained graph selections are cross-bound to causal records and exact
  effect-journal intent. Event sequence/history, document digests, SQLite
  projection columns and final projection/event agreement are checked.
- Execution run and step status/identity fields and JSON payloads are parsed;
  core workflow schema version/tables are required. Continuity rows are checked
  against revisions, proposals, evidence watermarks, and stored statuses.
- The contents validator is intentionally store-wide for the dedicated
  execution profile: unrelated malformed delegation/causal/effect records also
  refuse admission. This is conservative refusal, not tenant isolation.
- Healthy initial/pending, committed, Applying, partially applied/Blocked,
  expired-approval, and validation-failure states have inspector fixtures.
  Cancellation is propagated; malformed/null records map to refusal without
  granting permission. Inspection does not initialize, migrate, repair or
  classify uncertain effects as unapplied.

## Qualification still pending

The direct inspector tests now cover WAL-backed healthy and corrupt cases while
comparing database and WAL bytes. Repeated owned-process refusal tests cover
missing/empty/malformed/truncated storage and selected logical corruption
before provider/authority/checkout admission. Additional M6.1 logical,
fresh-process, healthy-owner and no-side-effect cases are still being added and
run. No M6.1 claim is made until exact-source tests, the required platform
matrix, package/source provenance, cleanup evidence and final review complete.

The contract remains bounded: this does not detect coherent rollback or
malicious rewrites that update all matching digests, validate Hufu internals,
handle resource exhaustion/soak, or repair/migrate/restore state.
