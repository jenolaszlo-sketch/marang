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
execution/activation truth, Hongxian owns session/evidence continuity,
Qingniao owns delegation/attempt truth. Marang owns only the authorized
product projection over their receipts — never another workflow engine,
fencing scheme, or authority model.

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
  contract above, backed by (i) a Zhinu release with external-operation
  handles, parked waits, and execution generations, (ii) a Hongxian release
  with the reconciled projection read, and (iii) one real registered
  provider.
- **V1.5 exit criterion:** validated end-to-end against that real provider
  and a durable restart/recovery path (a remote delegation survives service
  restart with verifiable, session-linked evidence and no duplicate
  external work).

## Production Hufu

The Hufu library (derived authority, file admission) is proven and needs
no V1.4 UI. Production composition (store, approvals, providers acting
under authority) is an M5 product decision, not a V1.4a gap.
