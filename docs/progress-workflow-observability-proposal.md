# Marang Progress, Workflow Visualization and Live Observability

## 1. Purpose

Marang needs a first-class progress, workflow-state, and observability layer.

Marang is primarily exposed through MCP, but execution state must also be available independently through HTTP so that browser-based tools can observe a running workflow.

V1 should provide enough information to render a useful live workflow visualization, including:

- workflow graph
- current execution progress
- checkpoints
- workflow revisions and redesigns
- activity details
- activity journals
- live updates

The fundamental design principle is:

> Marang owns workflow and execution truth. HTTP exposes current state. SignalR exposes changes. MCP is an adapter over the same model.

Marang execution must never depend on a connected browser, SignalR client, or MCP progress subscriber.

---

# 2. V1 goals

V1 must provide:

1. A generic progress-event model.
2. Hooks that Marang operations can use to report progress.
3. A current-state projection for each run.
4. HTTP endpoints for retrieving run state.
5. SignalR support for live updates.
6. Activity-level status and progress.
7. Activity journals suitable for a chat-style inspector.
8. Workflow graph representation.
9. Workflow graph rendering support.
10. Checkpoint representation and visualization.
11. Workflow revision representation.
12. Visualization of workflow redesigns and superseded paths.
13. Sequence numbers for ordering and reconnect recovery.
14. Separation between durable transitions and transient progress.
15. MCP progress correlation.
16. Stable identities suitable for retries, revisions, and future replay.

V1 should therefore already be capable of presenting:

```text
Plan
 │
 ▼
Activity A
 │
 ▼
Activity B
 │
 ▼
Checkpoint
 │
 ├──── Activity C ─── Activity D
 │        superseded
 │
 └──── Activity X ═══ Activity Y
          new revision
```

---

# 3. Deferred beyond V1

The following are deliberately deferred:

- workflow time travel
- historical graph reconstruction at arbitrary timestamps
- full replay visualization
- animated graph-diff transitions
- experimentation and A/B visualization
- workflow effectiveness scoring
- automatic route optimization visualization
- comparative execution analysis
- large-scale distributed SignalR infrastructure
- long-term analytics

The V1 model must not prevent these features later.

---

# 4. Architecture

```text
                    Marang Execution
                          │
                          │
                    IRunProgress
                          │
                          ▼
                ProgressEventDispatcher
                          │
       ┌──────────────────┼──────────────────┐
       │                  │                  │
       ▼                  ▼                  ▼
 Run Projection      History Store      SignalR Sink
       │                  │                  │
       ▼                  ▼                  ▼
    HTTP API          Event History        Live UI
                                              │
                                              ▼
                                      Workflow Graph
                                      Activity Inspector

                          │
                          ▼
                     MCP Adapter
```

Marang code reports semantic execution and workflow events.

It does not:

- publish directly to SignalR
- construct UI-specific DTOs
- depend on a browser
- use MCP progress as its source of truth

---

# 5. Core identity model

Four concepts must remain distinct.

```text
Workflow Node
     │
     │ represented by
     ▼
   NodeId

Logical Activity
     │
     │ represented by
     ▼
 ActivityId

Execution Attempt
     │
     │ represented by
     ▼
 ExecutionId

Workflow Version
     │
     │ represented by
     ▼
 RevisionId
```

These identities must not be interchangeable.

## NodeId

Identifies a stable structural node in the workflow.

Where possible this should correspond to the stable identity provided by Fuwen.

## ActivityId

Identifies the logical work represented by a node.

## ExecutionId

Identifies a particular attempt to execute an activity.

Example:

```text
ActivityId: compile-project

ExecutionId: execution-001
Attempt: 1
Failed

ExecutionId: execution-002
Attempt: 2
Completed
```

## RevisionId

Identifies the workflow revision in which a particular topology is valid.

---

# 6. Progress abstraction

Application code should normally use semantic progress APIs.

```csharp
public interface IRunProgress
{
    ValueTask RunStartedAsync(...);
    ValueTask ActivityStartedAsync(...);
    ValueTask ActivityProgressAsync(...);
    ValueTask ActivityJournalAsync(...);
    ValueTask ActivityCompletedAsync(...);
    ValueTask ActivityFailedAsync(...);

    ValueTask CheckpointReachedAsync(...);
    ValueTask CheckpointCompletedAsync(...);

    ValueTask WorkflowRevisionStartedAsync(...);
    ValueTask WorkflowRevisionAppliedAsync(...);

    ValueTask RunCompletedAsync(...);
    ValueTask RunFailedAsync(...);
}
```

This is the primary hook into the observability subsystem.

---

# 7. Event envelope

All events use a common envelope.

```csharp
public sealed record ProgressEvent(
    string RunId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Kind,
    string? ActivityId,
    string? ExecutionId,
    string? NodeId,
    string? RevisionId,
    ProgressDurability Durability,
    object? Data);
```

Suggested durability:

```csharp
public enum ProgressDurability
{
    Transient,
    Durable
}
```

---

# 8. Durable events

Durable events represent meaningful changes to workflow or execution truth.

Examples:

```text
run.started
run.completed
run.failed
run.cancelled

activity.started
activity.completed
activity.failed
activity.cancelled
activity.superseded

checkpoint.reached
checkpoint.completed

workflow.revision.started
workflow.revision.applied

node.added
node.superseded
edge.added
edge.superseded
```

These should be retained.

---

# 9. Transient progress

Examples:

```text
activity.progress

Reading repository...

Analyzed 84 of 230 files

Running test suite...

72%
```

These are primarily useful for live visualization.

They may be:

- throttled
- sampled
- coalesced
- dropped under backpressure

They do not need to be permanently persisted at full resolution.

---

# 10. Sequence numbers

Every event in a run receives a monotonically increasing sequence.

Example:

```text
101 run.started
102 workflow.revision.applied
103 activity.started
104 activity.progress
105 activity.completed
106 checkpoint.reached
107 workflow.revision.started
108 workflow.revision.applied
109 activity.started
```

Sequence numbers provide:

- deterministic ordering
- gap detection
- reconnect recovery
- debugging
- future replay
- future time travel

Sequence numbers are scoped to a run.

---

# 11. Workflow graph

V1 should expose the workflow structure directly.

A workflow graph consists of:

```csharp
public sealed record WorkflowGraph(
    string RevisionId,
    IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges);
```

---

# 12. Workflow node

Example contract:

```csharp
public sealed record WorkflowNode
{
    public required string NodeId { get; init; }

    public required string Type { get; init; }

    public required string Name { get; init; }

    public string? ActivityId { get; init; }

    public required string IntroducedInRevision { get; init; }

    public string? SupersededInRevision { get; init; }

    public WorkflowNodeStatus Status { get; init; }

    public object? Metadata { get; init; }
}
```

Possible node types:

```text
Plan
Activity
Checkpoint
Decision
FanOut
Join
Tool
Completion
```

Future types can be added without changing the graph model.

---

# 13. Workflow edge

```csharp
public sealed record WorkflowEdge
{
    public required string EdgeId { get; init; }

    public required string SourceNodeId { get; init; }

    public required string TargetNodeId { get; init; }

    public string? Label { get; init; }

    public required string IntroducedInRevision { get; init; }

    public string? SupersededInRevision { get; init; }

    public WorkflowEdgeStatus Status { get; init; }
}
```

Possible states:

```text
Pending
Active
Traversed
Superseded
Disabled
```

---

# 14. Workflow graph rendering

V1 should provide a frontend component capable of rendering the workflow graph.

Recommended frontend stack:

```text
React
TypeScript
React Flow
ELK.js
SVG
Motion
```

Responsibilities:

```text
React Flow
    node/edge interaction
    pan/zoom
    selection
    viewport

ELK
    graph layout

SVG
    custom edges
    markers
    flow effects

Motion
    state transitions
```

The V1 renderer does not need advanced animation.

The important requirement is that it understands workflow semantics.

---

# 15. Node state visualization

At minimum nodes should visually distinguish:

```text
Pending
Running
Completed
Failed
Waiting
Superseded
```

Example:

```text
✓ Research

● Implement API
  72%

○ Validate

× Old implementation
  superseded
```

Superseded nodes remain visible.

They are not deleted from the displayed execution history.

---

# 16. Active path visualization

The graph should distinguish:

- planned path
- completed path
- currently executing path
- superseded path

Conceptually:

```text
A ─── B ─── C
           │
           ▼
        Checkpoint
           │
           ├ - - D - - E
           │     old
           │
           ╰════ X ═══ Y
                 active
```

The exact visual styling remains a frontend concern.

---

# 17. Checkpoints

Checkpoints are first-class workflow nodes.

They should not be represented as ordinary activities with special labels.

Example contract:

```csharp
public sealed record CheckpointState
{
    public required string CheckpointId { get; init; }

    public required string NodeId { get; init; }

    public required string RevisionId { get; init; }

    public required CheckpointStatus Status { get; init; }

    public string? Reason { get; init; }

    public bool RequiresPlanning { get; init; }

    public DateTimeOffset? ReachedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}
```

Possible status:

```text
Pending
Reached
Evaluating
Completed
Failed
```

---

# 18. Checkpoint visualization

A checkpoint should be visually distinct from ordinary activities.

Example:

```text
───────────────◉───────────────
               │
          CHECKPOINT
       Reviewing evidence
```

While evaluation is occurring:

```text
        ◉
    Evaluating
```

If the workflow continues unchanged:

```text
A → B → ◉ → C → D
```

If the workflow is redesigned:

```text
A → B → ◉
         │
         ├ - - C → D
         │
         ╰═══ X → Y → Z
```

This should be one of the primary structural events in the UI.

---

# 19. Workflow revisions

Workflow redesign must be represented explicitly.

It must not simply replace the current graph.

Example:

```text
Revision 1

A → B → C → D
```

After a checkpoint:

```text
Revision 2

A → B → X → Y
```

The system should retain enough information to show:

```text
A → B
     │
     ├ - - C → D
     │
     ╰═══ X → Y
```

---

# 20. Workflow revision contract

```csharp
public sealed record WorkflowRevision
{
    public required string RevisionId { get; init; }

    public string? ParentRevisionId { get; init; }

    public required int RevisionNumber { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? Reason { get; init; }

    public string? TriggeringCheckpointId { get; init; }

    public IReadOnlyList<string> AddedNodeIds { get; init; } = [];

    public IReadOnlyList<string> SupersededNodeIds { get; init; } = [];

    public IReadOnlyList<string> AddedEdgeIds { get; init; } = [];

    public IReadOnlyList<string> SupersededEdgeIds { get; init; } = [];
}
```

---

# 21. Revision rules

Workflow revisions should follow several rules.

### Existing identity is preserved

If node `A` remains logically the same:

```text
Revision 1: A
Revision 2: A
```

it retains the same `NodeId`.

### Removed work is superseded

Do not delete:

```text
C
D
```

Instead:

```text
C.SupersededInRevision = revision-2
D.SupersededInRevision = revision-2
```

### New nodes receive stable identity

```text
X
Y
```

are introduced in revision 2.

### Historical execution remains valid

If `C` already ran before being superseded, its execution remains part of the run history.

---

# 22. Revision visualization

V1 should show workflow revisions even if graph animation remains basic.

At minimum:

```text
Revision 1
A → B → C → D

Revision 2
A → B
     ├ - C → D
     └ → X → Y
```

The current path should be visually dominant.

Superseded branches should remain visible but subdued.

The interface should expose revision information such as:

```text
Workflow revision 3
Triggered at checkpoint "Evaluate implementation"
Reason: Initial implementation failed validation
```

---

# 23. Current run state

```csharp
public sealed record RunState
{
    public required string RunId { get; init; }

    public required RunStatus Status { get; init; }

    public required long Sequence { get; init; }

    public required string CurrentRevisionId { get; init; }

    public required WorkflowGraph Workflow { get; init; }

    public IReadOnlyList<WorkflowRevision> Revisions { get; init; } = [];

    public IReadOnlyList<ActivityState> Activities { get; init; } = [];

    public IReadOnlyList<CheckpointState> Checkpoints { get; init; } = [];

    public string? CurrentActivityId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}
```

A single HTTP request therefore gives a UI enough information to draw the entire current workflow.

---

# 24. Activity model

```csharp
public sealed record ActivityState
{
    public required string ActivityId { get; init; }

    public required string NodeId { get; init; }

    public required string Name { get; init; }

    public required ActivityStatus Status { get; init; }

    public string? CurrentExecutionId { get; init; }

    public int Attempt { get; init; }

    public double? Progress { get; init; }

    public string? ProgressMessage { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? Model { get; init; }
}
```

Do not invent percentage progress where none exists.

This is valid:

```text
status: Running
progress: null
progressMessage: Reviewing architecture
```

---

# 25. Activity journal

Each activity may expose a chronological journal.

This is broader than chat.

Supported entries may include:

```text
ModelMessage
PlannerMessage
ToolCall
ToolResult
Progress
Evidence
Artifact
Warning
Error
Decision
Delegation
Metric
```

Example:

```text
10:42 Planner
Implement authentication.

10:42 Delegation
Assigned to Sol.

10:43 Model
Inspecting authentication layer.

10:43 Tool
SearchFiles("Authentication")

10:44 ToolResult
17 files found.

10:46 Progress
Implementation complete. Running tests.

10:47 ToolResult
43/45 tests passing.

10:48 Warning
Two compatibility tests failed.

10:49 Model
Adjusting token handling.
```

---

# 26. Journal contract

```csharp
public sealed record ActivityJournalEntry(
    string ActivityId,
    string? ExecutionId,
    string EntryType,
    DateTimeOffset Timestamp,
    string? Source,
    string? Message,
    object? Data);
```

Possible sources:

```text
Planner
Marang
Qingniao
Sol
Astra
Terminal
Compiler
TestRunner
GitHub
```

---

# 27. Progress dispatcher

```text
IRunProgress
     │
     ▼
ProgressDispatcher
     │
     ├── CurrentStateProjection
     ├── DurableHistory
     ├── SignalR
     ├── OpenTelemetry
     └── future adapters
```

A suitable internal implementation may use:

```csharp
Channel<ProgressEvent>
```

Execution should not wait for browser delivery.

---

# 28. Current-state projection

Events update an in-memory or persisted projection.

Example:

```text
activity.started
       │
       ▼
Activity.Status = Running
Node.Status = Running
```

```text
checkpoint.reached
       │
       ▼
Checkpoint.Status = Reached
Node.Status = Waiting
```

```text
workflow.revision.applied
       │
       ▼
CurrentRevision = revision-3
supersede old nodes/edges
insert new nodes/edges
```

The HTTP API reads this projection.

---

# 29. HTTP API

Suggested base route:

```text
/api/marang/runs
```

## Current run

```http
GET /api/marang/runs/{runId}
```

Returns the full current projection.

This should include:

- run state
- current revision
- workflow nodes
- workflow edges
- checkpoints
- activities
- sequence number

---

# 30. Workflow endpoint

Although the complete run contains the graph, a dedicated endpoint is useful:

```http
GET /api/marang/runs/{runId}/workflow
```

Possible response:

```json
{
  "runId": "run-123",
  "sequence": 184,
  "currentRevisionId": "rev-3",
  "nodes": [],
  "edges": []
}
```

---

# 31. Revision endpoints

```http
GET /api/marang/runs/{runId}/revisions
```

and:

```http
GET /api/marang/runs/{runId}/revisions/{revisionId}
```

These allow the UI to inspect why and how the workflow changed.

---

# 32. Event history

```http
GET /api/marang/runs/{runId}/events
```

Recovery:

```http
GET /api/marang/runs/{runId}/events?after=184
```

---

# 33. Activity endpoints

```http
GET /api/marang/runs/{runId}/activities/{activityId}
```

Journal:

```http
GET /api/marang/runs/{runId}/activities/{activityId}/journal
```

This powers node selection:

```text
Workflow node
      ↓
click
      ↓
Activity inspector
      ↓
journal
```

---

# 34. SignalR

Suggested hub:

```text
/hubs/marang/runs
```

Clients subscribe to:

```text
run:{runId}
```

SignalR is a live-delta transport.

It is not authoritative storage.

---

# 35. SignalR events

The UI should receive the same semantic events used internally.

Examples:

```text
activity.started
activity.progress
activity.completed

checkpoint.reached

workflow.revision.started
workflow.revision.applied

node.added
node.superseded

edge.added
edge.superseded
```

This means the graph can update incrementally rather than repeatedly downloading the complete run.

---

# 36. SignalR strongly typed client

Prefer:

```csharp
public interface IRunClient
{
    Task ProgressChanged(ProgressEvent progressEvent);
}
```

used through:

```csharp
IHubContext<RunHub, IRunClient>
```

The core Marang execution layer must not reference SignalR directly.

---

# 37. Snapshot + stream client model

Recommended connection flow:

```text
1. GET /runs/{runId}

2. Receive snapshot
   sequence = 184

3. Connect SignalR

4. Subscribe to run

5. Receive event 185
```

If the first event received is:

```text
188
```

the client detects the gap.

It then calls:

```text
GET /events?after=184
```

and receives:

```text
185
186
187
```

before continuing.

---

# 38. MCP integration

MCP should consume the same progress system.

```text
                   Marang Run
                       │
              Progress/Event Model
                       │
         ┌─────────────┼─────────────┐
         ▼             ▼             ▼
       HTTP         SignalR         MCP
```

MCP progress is not the internal source of truth.

---

# 39. MCP correlation

An MCP request may correlate:

```text
MCP requestId
MCP progressToken
MCP taskId
```

to:

```text
Marang RunId
```

Example:

```csharp
public sealed record RunCorrelation
{
    public string? McpRequestId { get; init; }

    public string? McpProgressToken { get; init; }

    public string? McpTaskId { get; init; }
}
```

---

# 40. Persistence

V1 should separate:

### Current state

Optimized for:

```text
GET /runs/{id}
```

### Durable event history

Optimized for:

```text
/events?after=n
```

### Transient telemetry

Primarily used for SignalR delivery.

A full event-sourcing implementation is not required for V1.

The design should nevertheless preserve enough durable events to support future reconstruction.

---

# 41. Storage abstractions

```csharp
public interface IRunStore
{
    ValueTask<RunState?> GetRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    ValueTask SaveRunAsync(
        RunState run,
        CancellationToken cancellationToken = default);
}
```

Event history:

```csharp
public interface IProgressEventStore
{
    ValueTask AppendAsync(
        ProgressEvent progressEvent,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ProgressEvent> ReadAsync(
        string runId,
        long afterSequence,
        CancellationToken cancellationToken = default);
}
```

SQLite is sufficient initially.

---

# 42. Frontend workflow viewer

The V1 frontend should have three primary areas.

```text
┌────────────────────────────────────────────────────────┐
│ Run status                                             │
├──────────────────────────────────────┬─────────────────┤
│                                      │                 │
│          Workflow Graph              │ Activity        │
│                                      │ Inspector       │
│   A → B → ◉ → C → D                  │                 │
│            ╲                         │ messages        │
│             X → Y                    │ tools           │
│                                      │ progress        │
│                                      │ artifacts       │
├──────────────────────────────────────┴─────────────────┤
│ Revision / checkpoint information                      │
└────────────────────────────────────────────────────────┘
```

The graph is the primary surface.

---

# 43. V1 graph interactions

The user should be able to:

- pan
- zoom
- fit graph to viewport
- select nodes
- inspect node state
- identify currently executing activity
- identify checkpoints
- distinguish active and superseded branches
- inspect workflow revision information

Node dragging may be supported but is not required.

---

# 44. V1 revision UX

When a workflow revision arrives:

```text
workflow.revision.started
```

the relevant checkpoint can show:

```text
Replanning...
```

When:

```text
workflow.revision.applied
```

arrives, the graph updates.

Existing nodes remain stable where possible.

Superseded nodes fade.

New nodes appear.

V1 does not require sophisticated animated re-layout.

A basic transition is sufficient.

More elaborate structural animations can come later.

---

# 45. V1 checkpoint UX

A checkpoint should display:

- status
- reason
- time reached
- whether replanning occurred
- resulting revision, if any

Example inspector:

```text
Checkpoint
Evaluate implementation

Reached
10:42:13

Reason
Validation after implementation

Result
Workflow revised

Revision
2 → 3
```

---

# 46. Error handling

Activity failures should use structured errors.

```csharp
public sealed record ActivityError(
    string Code,
    string Message,
    string? Category,
    bool Retryable);
```

Do not expose arbitrary raw exception serialization as the public API.

---

# 47. Backpressure

Progress must not materially slow execution.

Support:

- asynchronous sink processing
- transient event coalescing
- bounded queues
- throttling
- dropping nonessential transient events

Durable structural events must never be silently dropped.

In particular:

```text
workflow.revision.applied
checkpoint.reached
activity.completed
activity.failed
```

must be preserved.

---

# 48. OpenTelemetry

Progress and telemetry should integrate but remain separate abstractions.

Potential metrics:

```text
marang.run.duration
marang.activity.duration
marang.activity.retry_count
marang.activity.failure_count
marang.workflow.revision_count
marang.checkpoint.count
marang.run.active
```

---

# 49. Package structure

Possible structure:

```text
Marang.Progress.Abstractions
    events
    identities
    IRunProgress
    workflow contracts

Marang.Progress
    dispatcher
    projection
    throttling
    revisions

Marang.Progress.Persistence
    run store
    event store

Marang.Progress.AspNetCore
    HTTP endpoints
    SignalR

Marang.Progress.Mcp
    MCP correlation
    progress adapter

Marang.Workflow.UI
    React workflow viewer
```

The exact project breakdown can initially be simpler.

The important dependency direction is:

```text
Core
  ↑
Adapters
```

not:

```text
Core
  ↓
SignalR / HTTP / MCP
```

---

# 50. Implementation milestones

## Milestone 1: Identity and event model

Implement:

```text
RunId
NodeId
ActivityId
ExecutionId
RevisionId

ProgressEvent
IRunProgress
sequence allocation
```

Acceptance:

Progress can be produced without HTTP, SignalR, or UI dependencies.

---

## Milestone 2: Workflow topology

Implement:

```text
WorkflowGraph
WorkflowNode
WorkflowEdge
WorkflowRevision
```

Acceptance:

A Fuwen plan can be represented as a stable graph DTO.

---

## Milestone 3: Current-state projection

Implement:

```text
RunState
ActivityState
CheckpointState

projection logic
```

Acceptance:

A sequence of events produces the correct workflow and execution state.

---

## Milestone 4: HTTP API

Implement:

```text
GET /runs/{id}

GET /runs/{id}/workflow

GET /runs/{id}/revisions

GET /runs/{id}/events

GET /runs/{id}/activities/{activityId}

GET /runs/{id}/activities/{activityId}/journal
```

Acceptance:

An external application can reconstruct everything needed to display the current workflow.

---

## Milestone 5: Basic workflow viewer

Implement:

```text
React Flow
ELK layout
custom node types
workflow status
activity selection
activity inspector
```

Acceptance:

A static Fuwen workflow can be rendered and inspected.

---

## Milestone 6: SignalR live execution

Implement:

```text
RunHub
run subscriptions
live activity state
live progress
```

Acceptance:

The graph changes live as Marang executes.

---

## Milestone 7: Checkpoints

Implement:

```text
checkpoint nodes
checkpoint events
checkpoint status
checkpoint inspector
```

Acceptance:

A running workflow visibly stops at a checkpoint and shows its evaluation state.

---

## Milestone 8: Workflow revisions

Implement:

```text
revision creation
added nodes
superseded nodes
added edges
superseded edges
revision metadata
```

Acceptance:

The graph can change from:

```text
A → B → C → D
```

to:

```text
A → B
     ├ - C → D
     └ → X → Y
```

without destroying the historical branch.

---

## Milestone 9: MCP integration

Correlate:

```text
MCP request
MCP progress
MCP task
Marang Run
```

Acceptance:

One run can simultaneously expose progress through:

```text
MCP
HTTP
SignalR
```

---

# 51. V1 completion criteria

Marang V1 progress and visualization support is complete when:

1. Every execution receives a unique RunId.
2. A Fuwen workflow can be exposed as a graph.
3. Nodes and edges have stable identities.
4. Activities can report progress.
5. Activities can expose journals.
6. Run state can be retrieved via HTTP.
7. Workflow topology can be retrieved via HTTP.
8. Clients receive live changes via SignalR.
9. The workflow graph can be rendered interactively.
10. Running and completed activities are visually identifiable.
11. Checkpoints are visible as first-class nodes.
12. Checkpoint evaluation state is visible.
13. Workflow revisions are represented explicitly.
14. Superseded branches remain visible.
15. New workflow branches can be displayed.
16. Revision reasons can be inspected.
17. Reconnecting clients can recover missed durable events.
18. MCP progress correlates with the same Marang run.
19. UI dependencies do not leak into Marang core.
20. The model leaves room for replay, experiments, and time travel.

---

# 52. Post-V1 direction

With this revised scope, V1 establishes the full structural foundation:

```text
V1

Workflow graph
      +
Live progress
      +
Activity journals
      +
Checkpoints
      +
Workflow revisions
      +
HTTP
      +
SignalR
```

V2 can then focus on intelligence and deeper historical behavior:

```text
V2

Replay
Experiments
Effectiveness metadata
Adaptive planning
Alternative-path evaluation
Plan vs actual analysis
Historical reconstruction
Workflow time travel
Comparative execution analysis
```

The important distinction is that V1 already makes adaptive workflow execution **visible**.

V2 makes it increasingly **measurable, learnable, and optimizable**.