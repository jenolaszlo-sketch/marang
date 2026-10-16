# M6.1 corruption detection and fail-closed recovery

Status: implementation/qualification in progress. Baseline: Marang
`9638e2a37524ba6999c4e7df6016851578ec7fc8`; the four M5 qualification anchors
remain unchanged. Branch: `codex/aaaw-m6-1-integrity`.

This batch detects damaged durable state before admitting recovery of the
bounded repository workflow. It adds no repair, schema migration, backup/restore,
resource-exhaustion handling, soak behavior or workflow semantics.

## Recovery boundary

A host must distinguish an explicitly new workflow from resuming a known
workflow. A missing store for a known workflow is uncertainty, never permission
to initialize a replacement. An existing store is inspected before running
initializers, restoring execution, publishing authority, admitting provider
work, invoking validators/checkpoints or mutating the checkout.

The inspector opens the existing SQLite file read-only, in a consistent read
transaction. It performs SQLite physical integrity and bounded-profile logical
consistency checks. It never calls schema initialization or any repair API.
The known expected workflow identity comes from the host, not a guessed row.
The dedicated bounded-profile database is one trust unit: inconsistent rows
elsewhere in that store also refuse admission. This is not a multitenant
recovery-isolation guarantee.
Healthy initial, in-flight, partially applied, blocked and completed M5 states
must remain recoverable under their existing rules. Expired approval is not
itself corruption; existing live authority checks still decide permission.

Results distinguish admission from refusal, with bounded reason codes for
missing storage, invalid SQLite storage, unavailable/inaccessible state,
missing required state shape, and inconsistent durable records. A refusal
does not classify an uncertain external effect as unapplied. No alternate
store, new workflow identity, inferred approval or automatic retry is created.
Cancellation remains cancellation, not evidence of corruption.

## Integrity checks and limits

- Require the existing execution store and expected initialized profile tables.
  Missing/empty/truncated/malformed storage and missing workflow identity refuse.
  Require the pinned existing Zhinu schema marker (6), including execution and
  supervisor continuity tables. Other markers refuse without running the
  initializer or its existing migration code. This adds no upgrade support.
- Verify revision/proposal/archive identities, stored digests and required
  execution/ownership references against the pinned M5 representation.
- Cross-check graph selection, causal contracts, effect projections and
  append-only effect history. JSON validity alone is insufficient.
- Verify stored index columns agree with serialized documents. Detect missing
  projections with retained events, missing event history and disagreement
  between the current projection and the final recorded event state.
  Event numbering belongs to the whole append-only journal, including
  interleaved effects. Retained rows and the SQLite allocator must agree; there
  is no history retention/deletion path in this pinned writer.
- Validate existing graph validation/approval bindings without granting new
  authority. Physical integrity of additional required SQLite stores may be
  checked without interpreting or changing security-component internals.
- Validate stored run/step identities, lifecycle values and JSON shape, plus
  existing supervisor intent, discussion, checkpoint and reviewed-proposal
  bindings. This does not prove semantic truth of arbitrary valid payloads.

The inspector admits internally coherent state for existing recovery checks;
it is not a proof of authorization or repository freshness. Existing M5
actual-state reconciliation and final authority checks remain mandatory.
The read check is an admission boundary for a single controlled recovery owner,
not a defense against an arbitrary concurrent database writer after inspection.

Detection is limited to the specified invariants. A coherent rollback, complete
deletion of all redundant evidence, or malicious rewrite of data plus all
matching digests cannot be detected without independent trusted evidence.
M6.1 does not add such an anchor or claim cryptographic tamper resistance.
SQLite physical checks inspect its exposed committed state; M6.1 adds no
independent expected commit-tip anchor or raw WAL-format recovery algorithm.
Generic logical validation of Hufu internals is outside this slice.

No original database or WAL content may be rewritten on refused recovery.
SQLite's shared-memory sidecar is coordination state and may be accessed by a
read-only WAL reader; it is not restored evidence. Qualification must compare
main database/WAL bytes and report sidecar observations explicitly. When SQLite
opens a WAL-mode main file with no WAL present, it may materialize a zero-length
WAL sidecar; this must contain no frames. Existing WAL content must remain byte
identical. This sidecar observation is distinct from initializing a database.
If SQLite cannot inspect the state safely, recovery refuses rather than opening it for
automatic recovery or initialization.

## Required qualification

Preserve actual baseline counterexamples before production fixes. Use fresh
disposable stores with healthy positive controls and isolated raw-byte/SQL
damage. Cover missing, empty/truncated/malformed files, missing required tables
or rows, malformed JSON, digest mismatch and cross-record disagreement.
Repeat refused recovery in a fresh process: no provider dispatch, validator,
checkpoint, authority publication or repository mutation may occur, and the
damaged evidence must remain unchanged. Include a healthy fresh-process
recovery control through the same boundary.

Keep M5 tests and pinned packages unchanged. Rerun the original semantic
scenarios and all twelve barriers, K4 and actual-state control across the
qualified Windows, WSL2 Linux and macOS ARM64 profiles. Add M6.1 cases separately;
do not turn new corruption cases into reduced M5 assertions. Record exact
source/package/host identity, raw results, cleanup, skips and independent review.
