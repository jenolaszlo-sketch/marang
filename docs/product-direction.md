# Marang product direction

## What Marang is becoming

Marang is a durable supervisory execution environment for coding agents and
other bounded artifact-producing work. A supervisor can author or select a
workflow, start it, schedule future attention, leave, return at a planned or
emergent checkpoint, inspect bounded re-entry context, intervene, and
selectively re-execute affected work. Marang owns who acts, when supervision is
needed, what context and budget may be used, and how a result is aggregated.

The simple `marang_delegate` preset remains the easiest entry point. Advanced
users may select or author workflows represented by Fuwen. Marang does not
become a second workflow language or expose arbitrary caller-authored graphs as
an execution shortcut.

## Responsibility split

| Component | Owns |
| --- | --- |
| Marang | supervisor identity, wake/attention policy, budget and context policy, interventions, provider coordination, validation gates, and result aggregation |
| Fuwen | workflow semantics, typing, compilation, and the artifact-driven workflow representation |
| Zhinu | durable execution, waiting, signals, retries, leases/fencing, recovery, and selective restart |
| Hongxian | session continuity, participant attribution, correlation, incidents, decisions, and immutable audit narrative; the session authority for the real durable slice |
| Hetu | code graph indexing, structural code context, impact analysis, and ownership mapping |
| Cangjie | demand-driven memory/context retrieval and immutable context snapshots |
| Baize | bounded model execution, tool calls, structured output, usage, and provenance |

These are aligned dependencies, not subsystems Marang duplicates. Integration
adapters preserve each primitive's authority and keep their protocol types out
of Marang's core contracts where possible.

## Identity and immutability

The identity hierarchy is deliberately explicit:

```text
Hongxian Session
  -> Marang SupervisedWork / Delegation
    -> Fuwen PlanRevision
      -> Zhinu WorkflowRun / ExecutionEpoch
        -> structural Node
          -> NodeGeneration
            -> provider ExecutionAttempt / handle
              -> immutable artifacts
```

Hongxian owns session continuity and correlation; Zhinu remains execution
truth. The supervised-work identity is stable and user-visible, and a
caller-scoped request key plus canonical workflow-plan fingerprint is
idempotent for that identity. A retry/reconnect
stays in the same `NodeGeneration` and may create a new provider attempt only
where policy permits, with the same semantic input. Semantic node re-execution
creates a new `NodeGeneration`. Reopening completed supervised work creates a
new linked Zhinu `WorkflowRun`/`ExecutionEpoch`; it never mutates terminal
results. All interventions are idempotent and revision-fenced so stale
supervisor actions cannot overwrite newer decisions.

## Supervision and context

The current fixed lifecycle uses `NeedsSupervisor` as a terminal escalation
outcome. The planned lifecycle amendment adds `WaitingForSupervisor` as a
durable, resumable state for a workflow that is intentionally paused. It is not
an error and does not reopen a terminal execution. A later supervisor decision
or intervention resumes the waiting work under an explicit expected revision.

Each pause is addressed by a stable `SupervisorCheckpointId` scoped to the
session, supervised work, workflow run/epoch, plan revision, structural node,
and checkpoint address. A top-level waiting state gates progress that depends
on that decision; independent eligible branches may continue. Intervention
targets the checkpoint ID, expected current revision, and caller-scoped
idempotency key.

Wake and notification values are hints only. A notification may request the
supervisor's attention, but it cannot authorize work, change state, extend a
budget, or replace a result. The durable state and revision remain authoritative.

Context is demand-driven. A worker receives only the context required for its
current activity, with Cangjie snapshot identities and Hetu revisions recorded
for reproducibility. Marang does not preload an entire session, repository, or
conversation merely because it exists.

## Provider boundary

The durable provider contract remains non-blocking and handle-based:

```text
Start(request, idempotency identity) -> external handle
Observe(handle)                    -> progress/state
GetResult(handle)                  -> normalized result/evidence
Cancel(handle)                     -> idempotent cancellation request
Resume(handle, optional input)      -> continued execution
```

The provider must reveal a durable handle before an acknowledgement can be
considered safe. A blocking `ExecuteAsync` alone cannot recover from an
ambiguous acknowledgement and is not the foundational contract.

## Scope and non-goals

Marang is not a replacement for Codex, a general autonomous coding agent, a
workflow engine, a session database, a memory or code-graph implementation, a
model SDK, or a generic MCP/A2A gateway. It does not duplicate Fuwen's compiler,
Zhinu's durable mechanics, Hongxian's ledger, or Baize's model execution.

Marang also does not promise unbounded autonomous retries, recursive
delegation, automatic promotion/merge, or complete transcript replay. The
supervisor remains the authority for intent, policy, and final disposition.
