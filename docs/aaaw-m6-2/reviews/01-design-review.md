# M6.2 bounded schema-transition design review

Review date: 2026-10-16. Read-only review against Marang baseline
`5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29` (qualified M6.1 source
`e267f1c16b5cd7dc56ac33b65971be3d712fc673`). No source or test changes were
made.

## Recommendation

Implement one Marang-owned transition for the bounded main execution store:
from the implicit M5/M6.1 profile to an explicit Marang profile version 1.
Use a Marang-owned schema metadata table, and formalize the already-existing
nullable `supervisor_pending_discussions.dismissal_reason` addition as part of
the transition. Do not add the proposed effect-event resulting-record digest
column in this batch; it changes an integrity record format and requires a
separate threat-model and compatibility justification.

This is a real, deliberately small transition rather than an arbitrary version
bump: the old supervisor initializer silently adds `dismissal_reason` when it
is absent, while M6.1 recovery requires that column. M6.2 can recognize that
known old shape before any initializer runs, preserve every existing row and
meaning, add the nullable column if missing, and record an explicit Marang
format version. If it is judged too risky to support the column-absent shape,
the fallback marker-only transition is still defensible, but it must be
presented as formalizing an implicit profile boundary—not as migrating user
payloads or fixing a broader compatibility problem.

## Bounded format contract

Keep Zhinu's pinned `zhinu_schema.version = 6` as an independent external
precondition. Do not edit or reinterpret it. Use a Marang-owned metadata table
in the existing dedicated execution database (not `PRAGMA user_version`, which
can be shared with SQLite/framework ownership). A singleton row should identify
the Marang format and version; avoid a generic registry or migration engine.

The only legacy form eligible for explicit upgrade is: no Marang marker,
Zhinu schema 6, all required pinned Marang/Zhinu tables present, exact known
legacy column sets, and M6.1 logical integrity checks passing under the
legacy interpretation. Recognize `supervisor_pending_discussions` with either
the exact old columns (no `dismissal_reason`) or exact current columns (the
column exists but no marker). Unknown columns or other shape variants refuse.
The old missing column means legacy `dismissal_reason = NULL`; do not synthesize
a reason. A Dismissed row without a valid reason remains inconsistent and must
refuse.

Version 1 requires the explicit marker and the current exact expected schema,
including `dismissal_reason`. A version-1 marker with the column absent,
malformed/duplicate marker rows, unsupported Zhinu version, missing tables,
unknown columns, or any marker version other than exactly 1 refuses before
owner initialization. Do not treat a present-but-invalid marker as legacy.
Reads and writes for this M6.2 binary accept v1 only; the dedicated upgrade
operation accepts only the known implicit legacy form and produces v1. There
is no downgrade migration. An old binary that ignores the new table cannot be
made to honor this fence without changing that binary, so document older
writers as unsupported after upgrade rather than claiming they are
technically prevented from opening the additive schema.

## Upgrade ordering and interruption behavior

The upgrade entry point must run before every Marang owner initializer that
can create or alter this existing store. It must not call
`EnsureInitializedAsync`, current constructors that initialize on use, or
`CREATE TABLE IF NOT EXISTS` as a substitute for version admission. Known
workflow recovery first performs file/SQLite physical checks and read-only
legacy shape and logical preflight; a fresh workflow follows a separate
explicit new-store initialization path that creates the current profile and
v1 marker. A missing known-workflow database is never an upgrade candidate.

For a legacy store, acquire a SQLite write transaction, recheck the marker and
recognized shape under that transaction, add the nullable column only when
absent, create the metadata table and insert version 1, then commit. Keep the
DDL and marker insert in the same transaction. The migration must only add
schema metadata/shape: preserve existing IDs, documents, JSON bytes, digests,
event sequence and allocator, approval/evidence values, and all row counts.
After commit, run the ordinary M6.1 v1 inspector before constructing owners or
admitting recovery. If a post-commit check refuses, leave the versioned store
untouched and refuse; never guess at rollback or repair.

Test injected termination at: before the write transaction; after the ALTER;
after metadata insertion but before commit; and immediately after commit but
before success is reported. Before commit, reopening must find the exact
legacy form and safely retry. After commit, reopening must find v1 and no-op.
There must be no committed half-state (column without marker or marker without
column). Include exception rollback and fresh-process restart at the same
boundaries. This demonstrates SQLite transaction atomicity and idempotent
restart; it is not power-loss or hardware-flush qualification.

Before/after fixture snapshots should compare all pre-existing records and
semantics (identity, lineage, effect intent/history, approvals, provider state,
supervisor decisions and execution rows), allowing only the nullable column
and Marang marker. Test v1 re-open is a no-op. Test that a simulated v0/downgrade
reader policy refuses v1, and that unknown/future markers and incompatible
column sets refuse with database content unchanged. Since old binaries do not
check the new marker, this is a compatibility policy for the new code and
operators, not an enforceable barrier against arbitrary old executables.

## Architectural risks and limits

- Multiple Marang-owned initializers currently issue `CREATE TABLE IF NOT
  EXISTS`; `SqliteSupervisorContinuityStore.InitializeAsync` also adds
  `dismissal_reason` without a version gate. Wiring must place one central
  compatibility gate ahead of all these paths, and the initializer must stop
  performing an unversioned migration on an existing store.
- M6.1's normal inspector directly queries `dismissal_reason`, so it cannot
  itself be the legacy preflight for a column-absent store. Provide a narrow
  legacy-compatible validator (or parameterized column projection) that
  interprets the absent column as null and otherwise applies the same
  M6.1 integrity rules. Do not weaken the v1 inspector to accept missing
  columns.
- Preflight outside the write transaction is subject to races. Recheck the
  exact schema marker/shape while holding the migration transaction. The
  profile assumes one controlled owner and does not promise arbitrary
  concurrent writers.
- A metadata marker does not detect coherent payload rewrite/rollback, and
  does not force old binaries to check it. Do not overstate it as a cryptographic
  integrity anchor or global downgrade defense.
- The batch should not add backup/restore, resource exhaustion, soak behavior,
  a generic migration framework, changes to Hufu/Gagamba, package changes, or
  new workflow semantics. Although the M6 plan mentions preserved backups,
  this user's M6.2 scope excludes backup/restore; disposable fixture snapshots
  are test evidence, not a backup product.

## Review disposition

The proposed explicit marker plus the already-existing nullable supervisor
column is the smallest defensible transition I found. It gives Marang a
versioned compatibility boundary, replaces a silent schema mutation with one
atomic and testable upgrade, and avoids rewriting durable payload meaning.
No implementation is approved by this design review; the exact preflight
shape, entry-point ordering, unknown/downgrade behavior, and interruption
fixtures should be fixed in the M6.2 contract before coding begins.
