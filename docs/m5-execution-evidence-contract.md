# M5 execution-evidence contract (dependency assessment)

Status: **authoritative dependency assessment for Marang M5 run projection.**
Recorded 2026-10-08 after a contract survey of Zhinu and Hongxian. Assessment
only; no implementation. Read with the
[M5 run-projection handoff](m5-run-projection-handoff.md) and the
[V1 projection spec](progress-workflow-observability-v1.md).

The earlier framing that M5 was blocked on a missing execution-evidence "seam"
or subsystem was wrong. The capability is largely present; what remains is a
narrow read-contract gap on the Zhinu side, plus Marang's own projection and
correlation work.

## Ownership boundaries

- **Zhinu** owns durable execution truth, ordering, watermark/read shape, and
  durability classification.
- **Hongxian** remains generic evidence/session infrastructure and is
  **unchanged**.
- **Marang** owns run → delegation/attempt correlation, the authorized
  projection/read model, and the snapshot + journal handshake.
- **Qingniao** owns delegation/attempt truth; **Fuwen** owns plan semantics.

## Current Zhinu capabilities (already available)

| Fact | Read | Identity |
| --- | --- | --- |
| Run | `GetRunAsync`, `GetRunsAsync(RunQuery)`, `GetRunSubtreeAsync` | `WorkflowRunId` |
| Steps (current) | `GetStepsAsync` (`WorkflowStepRun`: status, attempt, revision, lease, timestamps, in/out) | `StepKey` |
| Graph edges | `GetDependencyGraphAsync` / `GetStepDependenciesAsync` | `StepKey` pairs |
| Waits | `GetWaitsAsync` (`WorkflowWait`: kind, status, signal, deadlines) | `StepKey` |
| Durable events | `GetEventsAsync(runId, afterSequence, limit)` | `(RunId, Sequence)` |
| Ordered export cursor | `IWorkflowEventExportRepository.ReadExportBatchAsync` / `AcknowledgeExportAsync` | per-consumer cursor |
| External operations | `ListAsync`, `GetAsync` (`WorkflowExternalOperation`) | `OperationId` |
| Instances / generations | `GetGeneration*`, `ListGenerationsAsync`, `ListDispositionsAsync` | `InstanceId`, `GenerationId` |
| Aggregates | `GetRunProgressAsync`, `DiagnoseAsync` (`RunDiagnosis` codes) | — |
| Event durability classification | `WorkflowEvent.Durability` via `WorkflowEventTypes.Durability` | `(RunId, Sequence)` |
| Bounded event pages | `IWorkflowEventPageReader.GetEventPageAsync` → `WorkflowEventPage` | `(RunId, Sequence)` cursor |

Plan/execution correlation exists: `WorkflowRun.DefinitionFingerprint`,
`WorkflowGeneration.ExecutionFingerprint` + `PlanRevision`.

## Missing primitives (narrow read-contract gap)

1. ~~Snapshot + durable watermark.~~ **Delivered** —
   `IWorkflowSnapshotReader.GetRunSnapshotAsync` returns a `WorkflowRunSnapshot`
   (run, current-revision steps, dependency edges, waits, artifacts, bounded
   external operations, active operation, generation + instance + dispositions,
   source run/lineage, recursive child snapshots, derived diagnosis) together
   with `ThroughDurableSequence`, all from one consistent read boundary (single
   deferred read transaction, rolled back without writing). Watermark meaning:
   all represented state includes every durable execution transition through D,
   and no state change caused solely by a durable transition after D is
   represented; advisory events past D do not invalidate the snapshot.
2. ~~Explicit durable/advisory classification.~~ **Delivered** —
   `WorkflowEventDurability` (`Durable`/`Advisory`), `WorkflowEventTypes.Durability`,
   `WorkflowEvent.Durability`; no schema or model change.
3. ~~Bounded event-page metadata.~~ **Delivered** —
   `IWorkflowEventPageReader.GetEventPageAsync` returns `WorkflowEventPage`
   (`Events`, `NextCursor`, `HasMore`, `ThroughDurableSequence`,
   `RetentionFloor`, `ResyncRequired`) over the existing run-scoped sequence,
   independent of export acknowledgement.
4. **Generation/operation transition visibility decision.** They are absent from
   the event stream and are currently snapshot-poll-only; make that explicit or
   add them to the stream.

**Not missing:** execution model, identity scheme, export/cursor subsystem,
Hongxian contract.

## Required cursor semantics

- **Initial rebuild:** read from sequence 0 / consumer start.
- **Incremental polling:** read after the last acknowledged sequence.
- **Reopen/re-read:** durable consumer cursor (export ack) or client-held
  sequence.
- **Duplicate reads:** deduplicate by `(RunId, Sequence)` (at-least-once).
- **Process restart:** `AcknowledgeExportAsync` is idempotent and monotonic.
- **Snapshot barrier:** needs the durable watermark from primitive (1) so
  snapshot-at-D plus events-after-D is race-free.

## Durable truth vs diagnostics

- **Truth:** run/step/wait/operation/generation/artifact state rows, and the
  transactional `workflow_events` stream as the committed-transition journal.
- **Diagnostics:** OpenTelemetry activities/metrics; `Progress` events;
  `DataJson` summaries. Observation time is not ordering authority.

## Hongxian

Unchanged. Marang adapts Zhinu execution facts into Hongxian's existing generic
envelopes (`SessionEventRequest` + `SessionEvidenceDescriptor`) when session
evidence is desired. No workflow-specific identity enters Hongxian.

## Rejected alternatives

- **New execution IDs / new identity scheme** — identities already exist.
- **New event-sourcing model / rebuild from the diagnostic stream** — state rows
  are already authoritative; events are a transition journal.
- **Workflow-specific Hongxian types** — violates ownership and the ecosystem
  guide.
- **SignalR/push as truth source** — polling a versioned projection is
  sufficient initially.
- **A new projection subsystem in Zhinu** — Marang owns the projection.

## Minimum viable M5 handoff
Zhinu provides (a) ~~a run snapshot reporting its durable watermark~~ (**delivered**,
see above), (b) ~~an
explicit durable/advisory event classification~~ (**delivered**),
(c) ~~bounded event-page metadata
over the existing sequence/export cursor~~
(**delivered**), and (d) an explicit statement that
generation/operation transitions are snapshot-only (or their promotion to
stream events) — **still open**. Hongxian
unchanged. Marang then builds the projection and
run → delegation/attempt correlation over those receipts.

## Implementation order

1. ~~Zhinu: durable/advisory classification + doc.~~ **Done.**
2. ~~Zhinu: event-page metadata (reuse existing cursor).~~ **Done.**
3. ~~Zhinu: watermark accessor + snapshot-with-watermark read.~~ **Done.**
4. Zhinu: decide generation/operation transition visibility (stream vs
   snapshot-only).
5. Marang: projection + correlation; adapt to Hongxian envelopes.
6. Marang: real provider + durable restart/recovery proof (V1.5 exit).
