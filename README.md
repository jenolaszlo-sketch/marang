# Marang

[![CI](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/marang)](LICENSE)

Marang is an MCP-accessible delegation and supervision runtime. It lets
a capable supervising agent hand off a bounded outcome to cheaper or specialized
models, then receive a concise result backed by tests, review findings, and
inspectable artifacts.

> Delegate an outcome, execute a controlled workflow, return evidence.

Marang is not another autonomous coding agent. The supervisor retains intent,
architecture, prioritization, and final judgment. Marang owns the repeatable
middle: delegation policy, budgets, worker selection, validation, bounded
repair, escalation, and result aggregation.

## Why it is useful

A normal engineering task consumes supervisor context on repository inspection,
planning, editing, testing, debugging, and review. Marang turns that sequence
into one durable operation without hiding the evidence needed to judge it.

- Submission is asynchronous and idempotent, so an interrupted MCP call does
  not duplicate costly work.
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
| **Marang** | Delegation lifecycle, fixed strategies, capability routing, policy, budgets, validation gates, escalation, and concise results |
| [Penghou.Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) | Durable workflow execution, retries, signals, cancellation, fencing, and recovery |
| [Penghou.Baize](https://github.com/jenolaszlo-sketch/penghou-baize) | Provider-neutral model invocation, structured output, tools, diagnostics, usage, and provenance |
| [Penghou.Hongxian](https://github.com/jenolaszlo-sketch/penghou-hongxian) | Optional long-lived session continuity, participant attribution, incidents, decisions, and immutable evidence |
| Execution providers | Agent, model, and deterministic execution behind capability-oriented durable adapters |
| Hetu, Cangjie, Nuwa | Optional code context, memory/context snapshots, and malformed JSON repair |

Hongxian is deliberately not treated as a repository sandbox or command
runner. An agent such as Codex may use its native tools and internal subagents
inside one bounded provider activity; Marang independently verifies its output.

At its external boundaries, Marang uses MCP northbound for supervising agents
and prefers A2A southbound for interoperable external agents. Process and SDK
adapters remain available when an agent does not expose A2A. Protocols are
adapters; Marang's durable outcome model remains independent of them.

## First vertical slice

```text
MCP delegate
    -> bounded agent execution
    -> seal candidate revision
    -> test + independent review
    -> evaluate
    -> at most one fix cycle
    -> evidence-backed result
```

The first strategy is `Implement`. Inspection and planning may remain internal
to the agent unless Marang needs a separate approval or reusable artifact. The
initial output is a validated candidate revision or patch; applying, committing,
or publishing it remains an explicit host or supervisor decision.

## Repository layout

- `Marang.Abstractions` contains transport- and provider-neutral public
  contracts.
- `Marang` will contain orchestration and delegation policy.
- `Marang.Hosting` will compose providers and host policy.
- `Marang.Mcp` will expose the small MCP transport surface.
- `Marang.Tests` contains contract and workflow tests.

The project currently contains the reviewed architecture, initial contracts,
CI scaffold, and its first validation tests. See the
[architecture](docs/architecture.md), [specification review](docs/spec-review.md),
the [agent execution boundary](docs/agent-execution.md), the
[protocol boundaries](docs/protocol-boundaries.md), the
[initial workflow contract](docs/initial-workflow.md), and the [roadmap](docs/roadmap.md).
