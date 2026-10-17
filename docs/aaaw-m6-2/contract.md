# M6.2: one bounded repository-profile compatibility transition

Status: implementation and qualification in progress. Starting qualification
anchor: **`5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29`**. Its M6.1 application,
evidence and all four M5 anchors remain immutable.

The invariant is that a runtime must not interpret durable state with different
semantics unless an explicit, qualified transition establishes the new
representation. This batch qualifies one additive Marang-owned transition.
It introduces no workflow feature or general migration framework.

## Supported boundary and ownership

The source format is the exact supported M5/M6.1 bounded repository profile:
Zhinu schema 6, existing Marang tables and documents, no
`marang_repository_profile` object and no `repository_effects.record_format`
column. Call this implicit profile 0. Earlier predecessor schemas, including
predecessors without `supervisor_pending_discussions.dismissal_reason`, are not
eligible inputs. No older state is manufactured by changing Zhinu's marker.

The destination is explicit Marang profile 1, with two atomic additions:

- A singleton `marang_repository_profile` table binds the owner, format version,
  contract identity and SHA-256 contract digest.
- `repository_effects.record_format INTEGER NOT NULL DEFAULT 1
  CHECK(record_format=1)` identifies the existing effect-document decoder. The
  writer explicitly emits tag 1 in this format. Readers verify tags before
  returning or extending records.

This creates a persisted reader/writer contract for previously unversioned
Marang representations. The new metadata controls admission and decoder
selection. It is not a new effect lifecycle, an event digest extension or a
change to the meaning of a stored document. Every original typed cell and
document byte, event sequence/allocator, identity, graph, authority binding and
revision lineage must survive unchanged. Only the declared metadata is added.

Zhinu owns schema marker 6; it does not advance. Package pins remain unchanged.
Separate Hufu storage receives physical read-only integrity checks only.
Migration must not read its logical internals, rewrite it or publish authority.
The external checkout is outside migration ownership and must not change.

## Contract identity and compatibility policy

The exact contract-identity input, with no trailing newline, is:

```text
marang/bounded-repository/m6.1@5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29;zhinu-schema=6;effect-json=web-v1;profile=1;effect-record=1
```

Its SHA-256 identifies the supported pinned semantic contract. It is not a
signature, a mutable database digest or an independent tamper-proof anchor.
The source anchor fixes the actual serializers, owners and workflow semantics;
the contract is not inferred from an installed assembly's marketing version.
An unsupported contract identity/digest, profile version, row-format tag or
required shape refuses admission. A future format is never treated as legacy.

The compatible reader can still inspect the recognized implicit profile under
its unchanged legacy semantics, without modifying it. A host requesting profile
1 must call `RequireCurrentAsync`; implicit profile 0 refuses until an explicit
`UpgradeAsync` completes. Qualification adds separate profile-1 host entry
phases; original M5/M6.1 scenarios and assertions remain intact.

Only this legacy-to-1 transition is implemented. There is no downgrade path.
An unchanged old executable cannot be made to enforce metadata it never knew
about; old binaries are not qualified writers for profile 1. The additive DDL
does not claim technical prevention of launching an old binary. Operators must
retain the existing single-owner deployment/recovery discipline.

## Upgrade admission and atomicity

1. Require the existing known workflow and all required stores to pass the
   strict M6.1 physical/logical checks before opening for writes. Do not run
   initializers, create missing storage, infer authority or reconstruct state.
2. Open the existing execution database in read/write-existing mode and obtain
   SQLite's immediate write transaction. Repeat preflight under the writer
   reservation and reread the format. Competing writers cannot change this
   execution database between validation and the transition.
3. If already valid profile 1, return `AlreadyCurrent` without durable changes.
   If exact recognized legacy, add the record tag, metadata table and singleton
   row in that transaction, validate the destination, and commit once.
4. Unknown or incomplete combinations refuse. Marker-only, column-only, empty
   or malformed manifest, unknown versions/digests/tags and incompatible source
   schema are not requests to complete or repair a migration.
5. Recovery and execution remain separate. Only after destination validation
   may the profile-1 host enter the existing initializer/recovery path. Current
   authority, fencing and repository actual-state checks still apply.

An exception or process termination before commit must leave the recognized
legacy representation; retry performs the same single transition. A stop after
commit but before success is reported must leave profile 1; retry is a no-op.
Internal qualification hooks mark transaction begin, post-DDL, pre-commit and
post-commit. Hooks supply no production authority or generic migration API.
Atomicity is qualified for process termination, not host power loss.

## Required evidence

- Generate genuine old-state fixtures using a separately built, clean checkout
  of exact `5dce00e`; record source, package and binary hashes. Stage completed A
  at K3 and partial B at K4 through the existing owned process/lifecycle path.
- Use the new runtime to read recognized legacy without changing meaning,
  explicitly migrate, and recover the same stores. Preserve completed mutation
  receipts, the partial mutation, effect identities, graph/revision lineage,
  supervisor state and uncertainty; repeat recovery without replay.
  Completed A exercises two normal host resumes; partial B exercises the normal
  recovery followed by a fresh read-only supervisor catch-up. Its durable
  blocker remains explicit. Normal recovery retains the existing authority
  publication behavior; only migration and read-only catch-up preserve the
  separate authority-store bytes.
- Compare exact original typed-cell contents in all pre-existing tables,
  including approval/authority/effect/provider/supervisor/execution records and
  `sqlite_sequence`. Do not canonicalize JSON to hide byte changes. Existing
  table/index definitions remain unchanged except the declared added column.
- Stop fresh owned upgrade processes at each declared checkpoint and retry in
  a new process. Retain cleanup/capture receipts and distinguish rolled-back
  legacy from committed profile 1. No partially committed shape may be admitted.
- Derive and label isolated unsupported-future/partial/corrupt fixtures from
  genuine old output. Repeat refusal in fresh processes; preserve database and
  existing WAL bytes, separate authority storage and repository state.
- Test migration contracts on .NET 8 and .NET 10; run native process composition
  on .NET 10 across Windows, WSL2 Linux and macOS ARM64. Rerun original M5/M6.1
  controls without reducing their assertions. Preserve failures and review.

Successful DDL changes the SQLite main/WAL representation by design. The
migration preservation claim concerns exact original cells and semantics;
byte-identical main/WAL evidence applies to refused admission, not successful
migration. SQLite shared-memory/empty-WAL observations retain the M6.1 limits.

## Explicit exclusions

No backup/restore implementation or qualification, repair, migration of Hufu or
Zhinu internals, package upgrade, resource-exhaustion behavior, soak, arbitrary
external rollback, new workflow semantics, general predecessor support or
production-readiness claim. Discardable fixture copies and retained evidence
are test infrastructure, not a backup/restore feature. Coherent removal or
rewrite of all redundant version evidence remains outside the M6.1 detection
claim. M6.3 and M6.4 have not started.
