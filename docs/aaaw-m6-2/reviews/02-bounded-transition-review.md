# M6.2 selected-transition design review

Date: 2026-10-16. Read-only review of the selected M6.2 design against exact
legacy anchor `5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29` and qualified
application source `e267f1c16b5cd7dc56ac33b65971be3d712fc673`. No source or
test changes were made.

## Disposition

The chosen transition is bounded and defensible: the exact M6.1 profile
without Marang-owned format metadata and without
`repository_effects.record_format` becomes an explicit Marang repository
profile version 1. Version 1 adds one singleton profile row with a canonical
contract digest and a constrained per-effect-projection format tag. It leaves
Zhinu's schema 6 and package pins unchanged, and it does not alter effect
event documents or introduce a new event-integrity feature. This is a real
schema/reader-writer transition: the new reader checks both the profile contract
and each projection's format tag before decoding the existing M6.1 payload.

Use only the exact `5dce` legacy shape as eligible input. The initial M6.1
anchor already has `dismissal_reason`; there is no need or authority to support
pre-M5 or earlier implicit predecessor shapes. Run strict M6.1 physical and
logical preflight before modifying anything. Marker absent plus record-format
column absent is the sole legacy combination. Marker present plus column
absent, marker absent plus column present, malformed marker shape, bad contract
digest, unsupported format version, or non-1/missing projection tags must
refuse without mutation. A partially applied shape should never be silently
completed: atomic DDL means it indicates external alteration or corruption.

For a legacy store, perform the exact preflight, then recheck the absent
marker/column and pinned schema inside the migration's write transaction.
Add `record_format INTEGER NOT NULL DEFAULT 1 CHECK(record_format=1)`, create
the singleton metadata table, and insert version 1 plus its canonical digest
in the same transaction. Existing rows receive the constant format tag from
the ALTER default; their document bytes, identities, authority, event rows,
digests, lineage and meanings remain unchanged. New writes must explicitly
write tag 1, and every projection reader must require tag 1 before decoding.
Validate the metadata table's structural shape as well as its values; do not
use a digest value as a substitute for physical schema checks.

The contract digest must have a deterministic, documented canonical input and
golden test. It identifies the selected reader/writer/schema contract; it is
not a signature, tamper-proof anchor, or digest of the mutable database. A
version-1 marker with a mismatching digest is an unsupported/inconsistent
profile and refuses. Keep the version policy exact: this M6.2 code supports
only v1 for normal reads/writes and one explicit legacy-to-v1 migration. There
is no downgrade path. A frozen older executable that ignores the new metadata
cannot be technically fenced by this additive schema; document old writers as
unsupported and do not claim binary-level downgrade prevention.

## Restart and compatibility proof

SQLite transactional DDL and the singleton marker should make the transition
all-or-nothing. Exercise fresh-process termination before the transaction,
after the column alteration, after marker insertion but before commit, and
after commit but before success is reported. Every pre-commit restart must see
the exact legacy combination and retry; the post-commit restart must see a
valid v1 profile and no-op. No restart may observe a committed column-only or
marker-only state. Include exception rollback and a healthy already-v1
positive control.

For payload preservation, compare all pre-existing row IDs and serialized
payload bytes/digests/counts before and after the migration, including revision
lineage, approvals/authority evidence, graph/causal/effect rows, provider
attempt/capture records, supervisor continuity, execution steps, and event
allocator/history. The only expected additions are the profile row and
`record_format` column/value. Verify every `repository_effects` reader rejects
bad/unknown tags, not just the admission inspector. Test a requested old
reader/version against v1 and require a no-write refusal; separately document
that the unchanged old binary does not itself enforce the marker.

Fresh-store creation must stamp v1 only after creating the complete current
schema; an interrupted partial fresh initialization is not legacy and should
refuse on known-workflow recovery. The compatibility check/migration must run
before all Marang owner initializers on existing stores, because current
initializers create tables and `SupervisorContinuityStore.InitializeAsync`
performs an unversioned `ALTER TABLE`. Remove that silent mutation for
existing-store startup; keep new-store creation as an explicitly separate
path. The migration path must not call `EnsureInitializedAsync` before
admission.

## Limits and scope

I found no design blocker in the selected transition. The main implementation
risk is complete gate ordering across every owner entry point, followed by
proving interruption atomicity and preserving exact legacy rows. The
contract-digest definition and per-record decoder gate need precise tests.
Do not change Zhinu schema 6, Hufu/Gagamba implementations, package pins, M5
workflow semantics, or effect event formats. Do not add backup/restore,
resource-exhaustion handling, a generic migration framework, or broader
predecessor compatibility in this batch. Disposable before/after fixtures are
test evidence only. A coherent removal of both format markers remains within
the M6.1 stated rollback/tamper-detection limit.

This is design approval to proceed to a concrete M6.2 implementation and
qualification, not qualification approval. Exact migration code, entry-point
coverage, unknown/partial-state tests, old-version refusal, process restart
results and payload-preservation evidence remain to be reviewed.
