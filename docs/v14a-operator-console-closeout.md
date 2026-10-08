# Marang V1.4a closeout: delegation-scoped operator console

Status: complete 2026-10-08. Marang-local feature work pauses here;
projection/graph/SignalR/durability/production-Hufu remain M5-gated (see
[m5-run-projection-handoff.md](m5-run-projection-handoff.md)).

## Definition

V1.4a is the usable operator console over the delegations Marang already
runs: observe status, answer attention, stop work, diagnose failure, and
inspect evidence — all through small fenced HTTP surfaces over existing
runtime operations, with immediate refresh and continued polling. No live
run projection, no SignalR, no durable recovery, no production authority.

## Capabilities (all live, all tested)

- Delegation list/detail with status, steps, worker/retry counts, result
  summary (`GET /api/delegations`, `GET /api/delegations/{id}`).
- Supervisor attention: `WaitingForSupervisor` renders as "Needs your
  approval" with a checkpoint card and one Approve/Resume action
  (`GET .../waiting`, `POST .../interventions`); stale/finished →
  409 + refresh, foreign → 404.
- Cancel: one button on cancellable delegations (`POST .../cancel` over the
  existing fenced `runtime.CancelAsync`; idempotent; `canCancel` from the
  backend, never shown for terminal states).
- Failure detail: `Failed` renders distinctly with the summary plus
  individually listed `unresolvedConcerns` (empty handled truthfully).
- Live evidence/artifacts: counters, flattened validation/review findings,
  and artifact descriptors via `GET .../evidence` (metadata only, never
  bytes) plus single-descriptor `GET .../artifacts/{id}` mirroring
  `marang_get_artifact`.
- Operator language throughout (`Needs your approval`, `Continue this
  step?`); engine terms (`WaitingForSupervisor`, `WakeHint`,
  `AdmissionFence`) stay in diagnostics.
- UI `typecheck` + `build` enforced in CI since 2026-10-08.

## Explicitly not in V1.4a

Reject/retry/escalate UI (MCP only; product semantics pending), MCP
`marang_result` enrichment (bare-string shape preserved for compatibility;
needs a versioned tool), live graph, SignalR, journals, durable recovery,
production Hufu composition, providers, chat, dashboards.

## Sample route

The `/ui/sample` fixture remains as an explicit labeled demo (V1.1 exit).
It is not a stand-in for live runs. Fixture evidence tabs for live views
are retired (live cards replaced them). When M5 run projection lands, the
Runs screen gains a real run list; the sample stays demo-only.
