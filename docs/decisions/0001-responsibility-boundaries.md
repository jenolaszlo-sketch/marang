# ADR 0001: Keep Marang a delegation composition layer

Status: accepted for the initial vertical slice.

## Decision

Marang owns delegation lifecycle, predefined strategy selection, budgets,
semantic worker profiles, evaluation policy, escalation, and result
aggregation.

Zhinu remains authoritative for durable workflow execution. Baize remains
authoritative for bounded model invocation. Hongxian may record session
continuity and evidence but is not a worker environment. Agentic coding runtimes
are execution providers: their internal tool and subagent behavior stays opaque,
while Marang controls capability selection, budgets, required evidence,
verification, retry, escalation, and durable external-operation correlation.

The public request uses an opaque `WorkspaceReference`. The first successful
output is an isolated candidate revision or patch, not an automatically applied,
committed, or published change.

## Consequences

- Marang can be tested with fake execution providers and an in-memory workflow
  policy.
- Core contracts do not force Zhinu, Baize, Hongxian, MCP, or filesystem types
  on consumers.
- Host policy remains the authority for capabilities and workspace resolution.
- Integration packages may evolve independently.
- Codex, Baize, deterministic, and future agent adapters can evolve without
  changing delegation semantics.
- Every non-atomic provider call needs an external handle so workflow replay can
  reconnect instead of duplicating work.
