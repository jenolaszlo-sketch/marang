# Marang UI contract and missing backend features

Status: first live delegation slice and backend audit, 2026-09-27. The sample model in
`src/Marang.Ui/src/contracts.ts` is a frontend fixture contract, not an approved
server API. Align real DTOs with the [reviewed V1 spec](progress-workflow-observability-v1.md)
before building the live adapter.

Update 2026-10-08 (V1.4a closeout): delegation-scoped HTTP now covers status,
waiting/approve (`.../waiting`, `.../interventions`), cancel (`.../cancel`),
failure concerns, and evidence/artifact descriptors (`.../evidence`,
`.../artifacts/{id}`) — all caller-scoped, tested, and rendered in the live
UI with polling. Still missing (M5-gated): authoritative run projection,
run-scoped journals/artifacts, SignalR, providers, durable recovery, and
production Hufu composition. Artifacts exposed here are delegation-scoped
references only. See [v14a-operator-console-closeout.md](v14a-operator-console-closeout.md)
and [m5-run-projection-handoff.md](m5-run-projection-handoff.md).

## Implemented now

- `RunSnapshot` separates node execution status from current/superseded
  membership, includes concurrent running nodes, a revision ID, and a decimal
  string durable cursor.
- `RunDataSource.getRun` is implemented by a deterministic sample adapter.
  The sample route is visibly labeled and does not fall back from a live error.
- The graph, list, inspector, attempts, journal, and milestone views consume
  this fixture model. Journal and revision examples are illustrative.
- `/api/delegations` and `/api/delegations/{id}` expose actual Qingniao progress
  through a Marang acceptance index. Both reads use the authenticated caller
  identity and return only that caller's accepted delegations. The UI has a
  separate live list/detail with polling, loading, empty, and error states.
  It makes no graph or revision claim from Qingniao's progress revision counter.
- HTTP `/api` and `/mcp` share the existing bearer-key/loopback boundary.
  MCP status, result, cancel, wait, inspect, intervene, and artifact tools now
  check the accepted delegation's caller before reading or changing it.

## Backend features required for the live UI

| Priority | Missing feature | Required behavior | Current evidence |
| --- | --- | --- | --- |
| P0 | Authoritative run projection | Map committed Fuwen/Zhinu/Qingniao/Hongxian receipts to current graph, checkpoint, revision, attempts, and outcome; persist and recover watermarks | `src/Marang.Server/Program.cs` composes only an in-memory Qingniao delegation runtime with no provider |
| P0 | Full authorized run reads | Paginated run list; atomic snapshot; workflow/revisions; activity detail; paginated/redacted journal; durable event catch-up; authorized artifact retrieval | Caller-scoped delegation list/detail exists, but no workflow run projection or cursor API |
| P0 | Browser identity and run authorization | Same-origin browser session, caller/workspace run scope on every read, session expiry/revocation, safe artifact access | `/api` now uses the MCP auth boundary; remote browser sessions do not exist, so the live UI works locally through the loopback proxy only |
| P0 | Live transport and recovery | Authorized SignalR run join/rejoin, subscription barrier/watermark, durable cursor history, retention floor, and resnapshot response | No run hub, event store, or browser recovery contract exists |
| P0 | Real execution provider | Register and authorize an actual provider and adapter in `Marang.Server`; prove accepted work proceeds to a result | The server's registries are empty, so accepted work reaches `NeedsSupervisor` |
| P0 | Plan-to-run correlation | Bind a verified Fuwen plan fingerprint/revision and stable node IDs to each Zhinu run/step; expose activated revision receipt | Fuwen definition/revision reads and Zhinu run progress exist. `WorkflowRun.DefinitionFingerprint` provides run-level definition correlation, and `WorkflowGeneration` carries `PlanRevision` and `ExecutionFingerprint`. Marang still owns the product-level mapping that relates the execution run to delegation/attempt context; no new Zhinu identity or correlation subsystem is needed |
| P1 | Attempt/evidence history | Expose per-attempt identities and history, and correlate Qingniao results/evidence to Zhinu steps and Hongxian receipts | Qingniao exposes aggregate counters/current labels and terminal result, not a per-attempt event history |

The caller check is covered by cross-caller tests, but the in-memory acceptance
index and runtime are volatile. The loopback bypass is a local development policy;
remote browser deployment needs a session and durable authorization source.

## Contract decisions to agree with backend

1. Run IDs, activity IDs, attempt IDs, node IDs, revision IDs, and upstream
   generation/receipt identities must have explicit mappings. The UI cannot
   reconstruct them from display names.
2. Snapshot responses include schema version, atomic durable watermark,
   freshness/lag, graph with retained superseded branches, all active
   executions, attempt summaries, checkpoint/revision reasons, truncation, and
   capability flags. No singular `CurrentActivityId` assumption.
3. Browser-facing 64-bit sequence/cursor fields are decimal strings. Durable
   cursor continuity is separate from droppable transient event order.
4. Event pages expose `nextCursor`, `throughDurableSequence`, retention floor,
   and `hasMore`; an expired cursor returns `resyncRequired` explicitly.
5. Subscription acknowledgment establishes a barrier. The client buffers
   deltas, fetches a snapshot, catches up to a known durable watermark, then
   applies live events. Reconnect repeats authorization and reconciliation.
6. Journal entries include stable entry/attempt IDs, order, type, bounded
   summary, disclosure/redaction, and evidence/artifact references.
7. The server advertises authorized UI actions with revision/generation fences.
   Until then, the UI only points to existing MCP supervision.

## Next frontend contract work

- Extend `RunDataSource` beyond `getRun` once the server response shapes are
  agreed. Add runtime parsing/schema rejection, cancellation, caller/session
  change handling, and the snapshot/stream reconciliation reducer.
- Move the sample data to versioned JSON snapshots/event scripts and add
  waiting, failure, cancellation, and completion scenarios. Keep sample mode
  separate from real mode; never fall back to a fixture after a real API error.
