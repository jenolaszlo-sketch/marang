# M5.3 plan→StepKey join gap (blocker)

Status: **recorded 2026-10-08; Marang M5.3 projection stopped at the step-4
checkpoint.** No projection code was written, because the join cannot be built
from public contracts without re-deriving Fuwen structural paths, which M5.3
forbids. This is a read-contract gap, not an execution-model gap.

## What was verified

- **Zhinu (published `0.2.0-preview.2`)** already exposes run, step, edge, wait,
  and event/cursor reads: `GetRunAsync`, `GetRunsAsync(RunQuery)`,
  `GetRunSubtreeAsync`, `GetRunProgressAsync`, `GetStepsAsync`
  (`WorkflowStepRun`), `GetDependencyGraphAsync` / `GetStepDependenciesAsync`
  (`StepDependency`, StepKey pairs), `GetWaitsAsync`, `GetEventsAsync`,
  `IWorkflowEventExportRepository`. So execution state and even an executed-step
  dependency graph are available.
- **Fuwen** publishes the declared topology: `WorkflowDefinitionDocument.ReadPlan()`
  → `WorkflowPlan`, whose `WorkflowNode.StructuralPath` (and the node records)
  are public. Declared topology is therefore obtainable.

## The gap

There is **no public, port-owned mapping from a Fuwen `WorkflowNode` to its
Zhinu `WorkflowStepRun.StepKey`.**

- The interpreter passes `node.StructuralPath` **directly** as the step key for
  ordinary and conditional-branch nodes, so `StepKey == StructuralPath` holds
  *for those*.
- But nodes nested in a `RepeatNode` are keyed by
  `FuwenRepeatCoordinator.StepSuffix(nodePath, repeatPath)` — a computed
  transform (`/$body/` marker extraction, prefix strip, `/`→`-`, `$` removal).
  Synthetic steps add `/…/$merge` and `/…/$fallback` keys.
- That derivation and the path→node index (`FuwenExecutionSchedule`) are
  `internal`. Neither Fuwen nor `Penghou.Fuwen.Zhinu` publishes a node→StepKey
  table; `WorkflowStepRun.StepKey` is an opaque string on the Zhinu side; the
  public `ExecutionInvocation` is per-execution, not a static plan→key map.

## Consequence

An exact `StructuralPath == StepKey` join is correct only for plans **without
repeats**. M5.3 explicitly requires loop structure to be preserved, and loops are
exactly where the mapping is not publicly reconstructible. Building the join for
loops would require re-deriving the port's internal key scheme — forbidden. This
is the exact gap M5.3 step 4 asked to stop and report.

Separately, step 4's proposed pair (`Qingniao NodeGeneration` +
`WorkflowStepRun.Revision`) are independent identifiers from different systems;
neither joins to a declared Fuwen node on its own, so the pair cannot substitute
for the missing node↔StepKey mapping.

## Required upstream fix (proposal; upstream owns the semantics)

Fuwen/Fuwen.Zhinu should publish a stable, plan-revision-scoped mapping from
declared topology nodes to their Zhinu `StepKey`(s), covering ordinary,
conditional-branch, merge, fallback, and repeat-iteration conventions. A
`WorkflowPlan`-derived node→StepKey table (or an equivalent public read) would let
any consumer join Zhinu execution rows to declared Fuwen topology without
re-deriving structural paths. Until then, Marang M5.3 must not build the graph.

## Related M5 read-contract gaps already recorded

See [m5-execution-evidence-contract.md](m5-execution-evidence-contract.md):
snapshot-with-watermark, durable/advisory classification, bounded event-page
metadata, and generation/operation transition visibility.

## Deferred (recorded, not solved)

- **M5-Live-Codex-Smoke**: a live `codex` CLI invocation on an environment where
  the CLI exists. M5.2 proved the adapter integration path with an injected
  deterministic `ICodexProcessFactory`; no architecture is to be added solely
  for this check.
- `CodexExecAdapter` is single-use/configuration-bound; production needs
  per-delegation construction or a host-owned resolving adapter.
- Hufu workspace authority and the Codex approved workspace root must eventually
  come from one authoritative scope.
- Production `IAuthorityIssuanceTrustSource` remains a product/composition
  decision.
- Codex returning zero artifact references is valid and must not be treated as
  incomplete evidence.
