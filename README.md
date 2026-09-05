# Marang

[![CI](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/marang)](LICENSE)

Marang is an MCP-accessible durable supervisory execution environment. It lets
a supervising agent hand off bounded artifact-producing work, schedule its own
future attention, leave, return at planned or emergent checkpoints, intervene,
and receive a concise result backed by tests, review findings, and inspectable
artifacts.

> Delegate an outcome, supervise durable execution, return evidence.

Marang is not another autonomous coding agent. The supervisor retains intent,
architecture, prioritization, and final judgment. Marang owns the supervisory
middle: delegation identity, wake and attention policy, budgets, context policy,
interventions, worker selection, validation, bounded repair, escalation, and
result aggregation.

## Why it is useful

A normal engineering task consumes supervisor context on repository inspection,
planning, editing, testing, debugging, and review. Marang turns that sequence
into one durable operation without hiding the evidence needed to judge it.

- Submission is asynchronous and idempotent, so an interrupted MCP call does
  not duplicate costly work.
- A supervisor can return with bounded re-entry context instead of replaying an
  entire session, then intervene or selectively re-execute affected work.
- Work is bounded by calls, retries, duration, and parallelism.
- Test evidence is kept separate from model judgment.
- A reviewer receives a stable candidate revision rather than a changing
  workspace.
- Exhausted or unsafe work can end as `NeedsSupervisor` instead of retrying
  without control.
- Results summarize what changed while retaining references to detailed typed
  artifacts.

## Ecosystem responsibilities

| Component | Responsibility |
| --- | --- |
| **Marang** | Supervised-work identity, wake/attention policy, context and budget policy, interventions, capability routing, validation gates, escalation, and concise results |
| [Penghou.Fuwen](https://github.com/jenolaszlo-sketch/penghou-fuwen) | Workflow semantics, typing, compilation, and artifact-driven workflow representation |
| [Penghou.Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) | Durable workflow execution, retries, signals, cancellation, fencing, and recovery |
| [Penghou.Baize](https://github.com/jenolaszlo-sketch/penghou-baize) | Provider-neutral model invocation, structured output, tools, diagnostics, usage, and provenance |
| [Penghou.Hongxian](https://github.com/jenolaszlo-sketch/penghou-hongxian) | Session continuity, participant attribution, correlation, incidents, decisions, and immutable evidence for the durable supervisory slice |
| Execution providers | Agent, model, and deterministic execution behind capability-oriented durable adapters |
| [Penghou.Hetu](https://github.com/jenolaszlo-sketch/penghou-hetu) | Code graph indexing, structural context, impact analysis, and ownership mapping |
| [Penghou.Cangjie](https://github.com/jenolaszlo-sketch/penghou-cangjie) | Demand-driven memory/context retrieval and immutable context snapshots |

Hongxian is deliberately not treated as a repository sandbox or command
runner. An agent such as Codex may use its native tools and internal subagents
inside one bounded provider activity; Marang independently verifies its output.

Fuwen workflows and the `marang_delegate` preset are two entry points into the
same supervisory model: one is simple and opinionated, the other is explicit
and composable.

At its external boundaries, Marang uses MCP northbound for supervising agents
and prefers A2A southbound for interoperable external agents. Process and SDK
adapters remain available when an agent does not expose A2A. Protocols are
adapters; Marang's durable outcome model remains independent of them.

## First vertical slice

```text
submit/select workflow
    -> non-supervisor executor
    -> deterministic validation
    -> WaitingForSupervisor checkpoint
    -> revision-fenced supervisor response
    -> resume
    -> immutable result
```

The durable vertical slice uses Hongxian as the session and correlation
authority, Zhinu as execution truth, and Fuwen for selected/compiled workflow
semantics. Pure core and in-memory tests may use fakes, but the real durable
slice is not complete without session continuity and reconciliation.

## Simple delegation preset

`marang_delegate` remains the easy, opinionated `Implement` entry point. Its
executor may inspect, plan, edit, test, and iterate internally; Marang still
validates the resulting candidate and preserves evidence. Applying, committing,
or publishing remain explicit host or supervisor decisions.

## Repository layout

- `Marang.Abstractions` contains transport- and provider-neutral public
  contracts.
- `Marang` will contain orchestration and delegation policy.
- `Marang.Hosting` will compose providers and host policy.
- `Marang.Mcp` will expose the small MCP transport surface.
- `Marang.Tests` contains contract and workflow tests.

The project currently contains the reviewed architecture, initial contracts,
CI scaffold, and its first validation tests. See the
[architecture](docs/architecture.md), [product direction](docs/product-direction.md),
[specification review](docs/spec-review.md),
the [agent execution boundary](docs/agent-execution.md), the
[protocol boundaries](docs/protocol-boundaries.md), the
[initial workflow contract](docs/initial-workflow.md), the
[dependency release plan](docs/dependency-release-plan.md), and the
[roadmap](docs/roadmap.md).
