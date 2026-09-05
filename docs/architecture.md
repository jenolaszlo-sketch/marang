# Marang architecture

## Product boundary

Marang is the supervisory execution environment around bounded artifact-
producing work. It accepts or selects a workflow, coordinates durable worker
activities, schedules future supervisory attention, supports bounded
intervention and selective re-execution, and returns evidence. It is a
composition and policy layer, not a model SDK, session ledger, workflow
compiler, or general coding agent.

```text
supervisor
    | MCP: objective, workflow/preset, constraints, budget, attention policy
    v
Marang: supervised-work identity, wake/context policy, intervention, result
    |
    v
Fuwen: workflow semantics and compilation
    |
    v
Zhinu: durable execution, retries, recovery, cancellation
    |
    +--> A2A provider: interoperable external agent task
    +--> process provider: bounded local/headless agent
    +--> Baize: provider-neutral bounded model execution
    +--> deterministic provider: tests, builds, diff and static analysis
    +--> artifact repository: typed reports and immutable evidence
    +--> Hongxian: session continuity, correlation, and audit narrative
```

Dependencies point inward: neither the abstractions package nor core domain
contracts expose MCP, Zhinu, Baize, or a filesystem path.

Hongxian is the session and correlation authority for the real durable
supervisory slice, although pure core/in-memory tests and simple preset policy
evaluation may run with fakes. Hongxian is not an executor or sandbox.

MCP is the primary agent-facing northbound protocol. A2A is the preferred
southbound protocol when an external agent supports it. Both remain adapters;
Marang's lifecycle and evidence contracts do not expose their wire models. See
[protocol boundaries](protocol-boundaries.md).

## Delegation identity and idempotency

`DelegationId` is the stable identity shown to callers. `WorkflowReference` is
an opaque provider-qualified execution reference and may change if the workflow
implementation changes.

Every submission has a caller-scoped `RequestKey`. The durable implementation
must enforce:

1. first use creates one delegation;
2. retrying the same key with the same normalized request returns that handle;
3. reusing the key with different semantics returns a conflict;
4. a response lost after durable acceptance is safe to retry.

This contract is distinct from Zhinu step idempotency.

The identity hierarchy is:

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

The Hongxian session is the temporal/correlation authority; Zhinu remains the
execution truth. The supervised-work identity is stable and user-visible. A
retry/reconnect stays in the same `NodeGeneration` and may create a new
provider attempt only where policy permits, using the same semantic input.
Semantic node re-execution creates a new `NodeGeneration`. Reopening completed
supervised work creates a new linked Zhinu `WorkflowRun`/`ExecutionEpoch`; it
never mutates terminal results. Interventions are idempotent and
revision-fenced so stale actions cannot overwrite newer decisions.

When a workflow is selected or authored, its canonical plan fingerprint and
immutable Fuwen `PlanRevision` are part of supervised-work acceptance. Changing
workflow semantics therefore creates a new plan revision rather than silently
reinterpreting an accepted request key.

## Lifecycle

The current fixed lifecycle is intentionally smaller than internal workflow
detail:

```text
Queued -> Running -> Completed
                  -> Failed
                  -> Cancelled
                  -> BudgetExceeded
                  -> NeedsSupervisor
```

For version 1, `BudgetExceeded` and `NeedsSupervisor` are normal terminal
results with accumulated evidence. `NeedsSupervisor` represents
unrecoverable/current-policy escalation; Marang will not silently resume
unbounded work. Status has a monotonic revision so MCP clients can suppress
duplicate updates. A planned lifecycle amendment adds `WaitingForSupervisor`
as a durable resumable state for an intentional pause. It is not implemented by
the current enum and will not reopen terminal state.

Cancellation stops future work. It is not rollback: the candidate workspace,
completed artifacts, unusual events, and diagnostic evidence remain available.

## Execution capability and workflow ownership

Agentic runtimes may explore, plan, edit, test, iterate, and delegate internally.
Marang treats those mechanics as opaque execution-provider behavior. Marang
still determines when the activity runs, its input and budget, required
evidence, dependencies, acceptance, retry, escalation, and durable lifecycle.

Providers are selected by semantic capability rather than vendor or model name.
An external execution has its own durable handle. Zhinu replay re-observes or
resumes that handle instead of launching duplicate work after an ambiguous
failure. See [agent execution](agent-execution.md).

## Current Implement preset

```text
Execute agent -> Candidate revision N
                   |             |
                   v             v
                 Test          Review
                   +------v------+
                        Evaluate
                     success | problems
                             v
                  Correct -> revision N+1
                              |       |
                             Test   Review
```

`Test` and `Review` may run concurrently only against the same sealed candidate
revision. A fix creates a new revision; it never mutates evidence that was
already reviewed. The evaluator consumes structured reports plus deterministic
test outcomes. A model may explain test output but cannot change whether a
command succeeded.

The preset remains the simple `marang_delegate` entry point. Advanced callers
may select or author an artifact-driven Fuwen workflow. Marang coordinates who
acts, what context and budget are allowed, when the supervisor should be
notified, and how outcomes are accepted; Fuwen owns workflow semantics and
Zhinu owns durable execution.

## Supervision and context

Wake and notification values are hints only. They request attention but cannot
authorize work, change state, extend a budget, or replace a result. Durable
state and revision checks remain authoritative.

Each planned pause is addressed by a stable `SupervisorCheckpointId` scoped to
the session, supervised work, workflow run/epoch, plan revision, structural
node, and checkpoint address. A top-level wait gates only progress that depends
on its decision; other eligible independent branches may continue. An
intervention targets the checkpoint ID, expected current revision, and a
caller-scoped idempotency key.

Re-entry is demand-driven: a supervisor receives bounded context for the
checkpoint being inspected, including relevant artifact references and
correlation identities. Cangjie snapshots and Hetu revisions are referenced for
reproducibility rather than loading an entire conversation or repository.

Retry reconnects to an accepted external operation or repeats a failed
transient observation under the same `NodeGeneration` (with a new provider
attempt only where policy permits). Semantic node re-execution is a deliberate
new `NodeGeneration`; reopening completed supervised work creates a new linked
Zhinu `WorkflowRun`/`ExecutionEpoch`.

## Workspace and mutation boundary

An MCP caller supplies an opaque `WorkspaceReference`, not an unrestricted
filesystem path. The host resolves it against configured projects and allowed
roots. Initial adapters may support local paths internally, but must canonicalize
the path, reject traversal and links that escape the root, and execute in an
isolated candidate workspace.

Execution providers cannot commit, push, publish, access credentials, or modify the primary
checkout by default. A successful delegation means “candidate ready for
supervisor disposition,” not “change merged.” Promotion is a separate explicit
capability.

Agent providers may expose rich internal tools, but only inside the granted
sandbox. Deterministic providers remain host-controlled. Both return normalized
receipts rather than granting Marang a general unrestricted shell.

## Artifacts and evidence

Initial artifact kinds are:

- `InspectionReport`
- `ImplementationPlan`
- `ImplementationResult`
- `TestReport`
- `ReviewReport`
- `DelegationResult`

Each artifact needs a kind, schema version, delegation and producer identity,
creation time, immutable content identity, and candidate revision where
applicable. A result references artifacts instead of embedding transcripts.
Sensitive raw prompts and command output follow explicit retention and
redaction policy.

## Profiles and provenance

Requests select a semantic profile such as `fast`, `coding`, or `review`, not a
provider model ID. Host configuration resolves profiles to Baize models. Every
worker receipt records the resolved provider/model, invocation identity, usage,
profile, tool capabilities, and input artifact identities so routing remains
auditable.

Review independence is evidence, not a boolean promise. The result should state
whether implementation and review used a different invocation, context,
profile, model, and provider. Policy decides the minimum acceptable level.

## Public package boundary

- `Marang.Abstractions`: stable contracts and `IDelegationService`.
- `Marang`: orchestration, policies, validation, and aggregation.
- `Marang.Hosting`: DI and concrete adapter composition; not packable
  until it has a useful host-neutral surface.
- `Marang.Mcp`: MCP DTO mapping and tools; no domain policy.

Fuwen, Zhinu, Baize, Hongxian, Hetu, and Cangjie adapters may become separate
packages if they are useful
without forcing those dependencies on the core.
