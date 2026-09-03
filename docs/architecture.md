# Marang architecture

## Product boundary

Marang accepts a bounded outcome from a supervising agent, compiles it to a
predefined delegation strategy, supervises durable worker activities, and
returns evidence. It is a composition and policy layer, not a scheduler, model
SDK, session ledger, workflow language, or general coding agent.

```text
supervisor
    | MCP: objective, acceptance criteria, constraints, budget
    v
Marang: delegation identity, policy, profiles, evaluation, result
    |
    v
Zhinu: durable execution, retries, recovery, cancellation
    |
    +--> agent provider: bounded outcome using native agent capabilities
    +--> Baize: provider-neutral bounded model execution
    +--> deterministic provider: tests, builds, diff and static analysis
    +--> artifact repository: typed reports and immutable evidence
    +--> Hongxian (optional): session correlation and audit narrative
```

Dependencies point inward: neither the abstractions package nor core domain
contracts expose MCP, Zhinu, Baize, or a filesystem path.

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

## Lifecycle

The public lifecycle is intentionally smaller than internal workflow detail:

```text
Queued -> Running -> Completed
                  -> Failed
                  -> Cancelled
                  -> BudgetExceeded
                  -> NeedsSupervisor
```

For version 1, `BudgetExceeded` and `NeedsSupervisor` are normal terminal
results with accumulated evidence. A later continuation may create a linked
delegation; Marang will not silently resume unbounded work. Status has a
monotonic revision so MCP clients can suppress duplicate updates.

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

## Fixed Implement strategy

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

Zhinu/Baize/Hongxian adapters may become separate packages if they are useful
without forcing those dependencies on the core.
