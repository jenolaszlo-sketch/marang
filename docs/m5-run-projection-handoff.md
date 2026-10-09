# M5 run-projection handoff: what Marang is waiting for

Status: Marang-local feature work is paused for these items. Marang consumes;
it does not invent execution, recovery, graph, or authority semantics to
unblock its UI.

## What Marang needs

An authoritative durable run projection, approximately:

- run identity, revision, status, durable sequence/freshness,
- nodes with identity/name/kind/status/membership/attempt/generation,
- graph edges,
- run-scoped checkpoint/wait reasons,
- stable run → delegation/attempt correlation (Fuwen plan fingerprint on
  runs addressable to Zhinu steps and Qingniao delegation IDs).

Ownership stays upstream: Fuwen owns workflow semantics, Zhinu owns
execution/activation truth, ordering, and the execution-evidence read shape
(including the durable watermark and durability classification), Hongxian owns
the generic session/evidence continuity contract (unchanged), Qingniao owns
delegation/attempt truth. Marang owns run→delegation/attempt correlation, the
authorized product projection/read model and the snapshot+journal handshake over
their receipts — never another workflow engine, fencing scheme, or authority
model. See the authoritative
[M5 execution-evidence contract](m5-execution-evidence-contract.md).

## Transport

Polling a versioned projection is sufficient initially. Marang already
polls delegation state truthfully; the same pattern extends to run
snapshots with watermarks. SignalR may later transport the same
projection (push instead of poll) but is not the source-of-truth
dependency and must not gate projection work.

## Operator vs internal

Operator-visible on recovery: run state, revision, active/waiting work,
checkpoint reasons, failure summaries, evidence/artifacts, and freshness
(`updated X ago`, `reconnecting…`, `catching up…`). Runtime-internal
forever: fence tokens, idempotency keys, leases, store revisions,
derivation lineage, Hufu contexts, signal internals, raw cursor values.

## Unblocker vs exit criterion

- **Integration unblocker:** the published durable projection + correlation
  contract above, backed by (i) a Zhinu release that closes the narrow M5
  read-contract gap — a snapshot carrying a durable watermark, an explicit
  durable/advisory event classification, bounded event-page metadata, and an
  explicit statement whether generation/operation transitions are stream-visible
  or snapshot-only — and (ii) one real registered provider. Zhinu already
  supplies stable run/step/generation/operation identities, persisted
  run/step/wait/operation/generation state, per-run ordered durable events,
  per-consumer cursor/export semantics, and plan/execution correlation; no new
  execution model or identity scheme is required. Hongxian stays generic and
  unchanged; Marang owns the projection and adapts Zhinu execution evidence into
  Hongxian's existing envelopes.
- **V1.5 exit criterion:** validated end-to-end against that real provider
  and a durable restart/recovery path (a remote delegation survives service
  restart with verifiable, session-linked evidence and no duplicate
  external work).

## Production Hufu

The Hufu library (derived authority, file admission) is proven and needs
no V1.4 UI. Production composition (store, approvals, providers acting
under authority) is an M5 product decision, not a V1.4a gap.

## M5.4 notes (2026-10-08)

- Session journals read Hongxian's reconciled projection plus one bounded
  ledger page; recovery states are a pure mapping of Zhinu run status and
  Hongxian `SessionOperatorState` (no Marang state machine). Graph truth
  stays Zhinu-only; journal absence/failure never erases it.
- Production `IRunProjectionSource`/`ISessionJournalSource` are wired
  config-gated (`Marang:Runs`: `Enabled`, `ZhinuDatabasePath`,
  `HongxianRootPath`, `PlanDirectory`) with fail-fast validation; when
  unconfigured the endpoints truthfully report 404.
- Packaging: Marang unifies `SQLitePCLRaw.bundle_e_sqlite3` to 2.1.13 and
  `Microsoft.Data.Sqlite` to 10.0.11 (Fuwen.Zhinu/Hongxian.Sqlite require
  newer than Hufu.Sqlite's exact pins), with a scoped `NU1608` suppression
  proven by the full suite including Hufu Sqlite tests. Align the upstream
  package dependencies later.
- Hongxian preview.5 is net10-only, so Hongxian-dependent code lives in the
  net10-only `Marang.SessionJournal` project; `Marang.Runs` stays net8/net10
  and Hongxian-free. Journal tests are net10-gated (`#if`).
