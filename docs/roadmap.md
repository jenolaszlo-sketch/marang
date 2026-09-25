# Marang and Qingniao roadmap

## Goal

Deliver Marang as an ASP.NET Core MVC MCP service for remote supervisors while
extracting its already-proven delegation contracts and runtime into reusable
Penghou.Qingniao packages that Marang, Guyabano, and future products can consume.

Progress lives here so the work can resume without chat history. Detailed work
completed before the ownership correction is preserved in the
[Qingniao runtime roadmap](qingniao-runtime-roadmap.md).

## Current state

- The original Marang-named delegation contracts, lifecycle, evidence,
  provider selection, reconnect store, plan resolution, and atomic in-memory
  execution store are implemented and tested.
- Their public surface was frozen only as a pre-release baseline. They now form
  the extraction source for Qingniao rather than the Marang service API.
- Siming `0.1.0-preview.4` is published and unblocks Qingniao's canonical JSON
  fingerprint adapter.
- The executable coordinator has not yet been completed.
- The ASP.NET Core `Marang.Server` host is scaffolded with a connectivity
  probe (`marang_ping`) and a health endpoint; Qingniao runtime composition
  into the server is pending.
- The independent `Penghou.Qingniao` repository is created and verified. The
  old source remains temporarily frozen here only until the first Qingniao
  preview is published; it is not a second implementation line.
- Fuwen remains the active ecosystem implementation priority until its compiled
  artifact-workflow path is usable. The shared-code-graph work below is a
  recorded subsequent proving sequence, not an interruption of that work.

## Adaptive planning supervision direction

Marang is a natural first supervisor for adaptive workflow evolution, but it
does not own the underlying semantics. A Codex or other participant may observe
execution and propose `Accept`, `Retry`, or `Replan`; Fuwen compiles and admits
an immutable candidate revision; Zhinu previews and durably activates a new
execution generation; Hongxian records the complete causal timeline.

Marang should eventually expose bounded MCP/HTTP operations to propose a replan,
inspect a transition preview, approve or reject activation, and explain reused
or invalidated work. It must consume authoritative receipts from Fuwen/Zhinu and
must not edit a running graph, infer activation from chat, treat model output as
authorization, or recreate workflow-instance/generation storage.

This work follows Fuwen admission plus plan comparison and Zhinu's atomic
generation-cutover foundation. The initial Marang policy should require Codex or
human supervision; automatic low-impact activation is later evidence-driven
policy.

The reusable planning mechanics this direction depends on are scoped in the
`Penghou.Guihua` kernel (patches and preservation, stage catalogue, planning
graph, bounded loop, decisions). Marang is not yet integrated with Guihua: its
current plan surface is a deliberately sealed `Implement/1` preset plus an
opaque Fuwen reference. The uncovered seam — provider-neutral patch admission,
transition preview, and reuse/invalidation explanation — is recorded in the
[Guihua roadmap](https://github.com/jenolaszlo-sketch/penghou-guihua/blob/main/ROADMAP.md).

## Shared code graph and delegation pivot

Marang is the coding-specific composition and MCP boundary. Qingniao coordinates
one delegated execution, Hongxian preserves session continuity and immutable
evidence, Hetu owns structural code facts, Fuwen defines workflow semantics, and
Zhinu owns durable workflow execution. Marang composes those capabilities
without moving coding vocabulary into the reusable primitives.

The hypothesis to test is:

> Delegation becomes materially more economical when the supervisor can compare
> an exact structural state it already understood with the exact state produced
> by delegated work, then inspect only the risky source changes.

This is not yet a proven product claim. Git remains authoritative for textual
change, source remains authoritative for behavior, Hetu is a deterministic
structural projection, Hongxian is temporal evidence, and worker summaries are
interpretation.

Architectural constraints:

- Marang owns coding-specific MCP. Do not add a standalone Hetu MCP surface or
  code-specific Qingniao tools.
- A Marang coding session may explicitly enable Hetu-backed code memory, but
  Hongxian and Qingniao must remain usable with zero Hetu dependencies.
- Do not assume `Penghou.Hongxian.Hosting`, `Penghou.Hongxian.Hetu`, or a
  separate `Marang.Mcp` package until their reusable/package boundaries are
  proven. Package names do not define architecture.
- Qingniao does not own workflows or dependency graphs. It coordinates one
  bounded delegation; Fuwen and Zhinu own workflow semantics and durability.
- Parallel workers produce independent candidate revisions/publications. A
  later integration/promotion produces a new authoritative source and workspace
  publication; unrelated branches must not be collapsed into an imaginary
  combined state.
- MCP requests are authenticated and workspace-authorized, and every graph,
  delta, artifact, and source result is bounded, redacted, and publication-bound.

## Milestone 0 — Correct the ownership boundary

Status: **complete — architecture decision recorded; source migration next**

- [x] Define Marang as the deployable ASP.NET Core MVC/MCP service.
- [x] Define Qingniao as the reusable delegated-execution runtime, not a
      general workflow runtime.
- [x] Assign workflow durability to Zhinu and workflow semantics to Fuwen.
- [x] Define direct Guyabano-to-Qingniao and remote Marang deployment models.
- [x] Supersede the old Marang package boundary without promising pre-release
      compatibility.

See
[ADR 0015](decisions/0015-qingniao-extraction-and-marang-service-boundary.md).

## Milestone 1 — Extract and rename Qingniao

Status: **implementation complete in the Qingniao repository; package handoff pending**

- [x] Rename `Marang.Abstractions` to `Penghou.Qingniao.Abstractions`.
- [x] Rename the reusable `Marang` delegated-execution project to
      `Penghou.Qingniao`.
- [x] Move namespaces, assembly/package metadata, XML docs, API baselines, and
      tests without changing the reviewed semantics.
- [x] Rename `Marang.Tests` to `Penghou.Qingniao.Tests`.
- [x] Grow `Marang.Mcp` into the real MCP project (connectivity probe,
      referenced by `Marang.Server`). The first Qingniao package is published.
- [ ] Decide `Marang.Hosting`: still an empty assembly-marker seam and
      unreferenced. Fold it into the future service composition or remove it.
- [x] Add architecture/dependency tests proving Qingniao has no reference to
      Marang, ASP.NET Core, MCP transport DTOs, or product configuration.
- [x] Keep the old `Marang` and `Marang.Abstractions` preview packages
      superseded; do not publish forwarding packages without a real consumer.

Exit: all reusable delegation code builds and tests under Penghou.Qingniao
names, with no Marang product dependency.

## Milestone 2 — Complete the Qingniao in-memory delegated-execution runtime

Status: **planned after extraction**

- [ ] Reference `Penghou.Siming` `0.1.0-preview.4`.
- [ ] Implement the Siming-backed canonical semantic-fingerprint producer and
      verifier; do not copy canonicalization logic.
- [ ] Complete the deterministic in-memory coordinator using the existing plan
      resolver, acceptance registry, provider snapshot, authorized adapter
      catalog, early-handle store, and atomic execution store.
- [ ] Make submission return `Queued` before provider execution and advance
      through an explicit deterministic pump rather than timing-sensitive
      background tasks.
- [ ] Prove captured-handle recovery after ambiguous start without duplicate
      work or a new semantic generation.
- [ ] Seal a candidate before parallel deterministic Test and independent
      Review.
- [ ] Exercise a stable supervisor checkpoint, bounded re-entry context, one
      revision-fenced intervention, and at most one new-generation repair.
- [ ] Cover success, no provider, unauthorized adapter, rejection,
      cancellation, budget exhaustion, transport ambiguity, worker failure,
      `WaitingForSupervisor`, and `NeedsSupervisor`.

Exit: Qingniao can be embedded and tested without MCP, ASP.NET Core, real
providers, or a workflow database.

## Milestone 3 — Scaffold Marang.Server

Status: **partially complete — host scaffolded with probe; composition pending**

- [x] Create a non-packable ASP.NET Core executable (minimal-API style; no MVC
      controllers yet).
- [x] Add a health endpoint (`/healthz`) and an MCP transport probe
      (`marang_ping`).
- [ ] Reference Qingniao and make Marang.Server the composition root.
- [ ] Add configuration validation, readiness, structured
      diagnostics, graceful shutdown, and bounded background dispatch.
- [ ] Define remote caller/tenant identity and authentication extension points.
- [ ] Resolve workspace references, provider profiles, disclosure policy, and
      budget ceilings under server authorization.
- [ ] Keep transport DTOs independent from Qingniao domain contracts.
- [ ] Add service-level request, response, concurrency, and rate limits.

Exit: Marang hosts the fake Qingniao vertical slice as a secure local service.

## Milestone 4 — MCP and HTTP supervision surface

Status: **planned**

- [ ] Expose `marang_delegate`, `marang_status`, `marang_result`, and
      `marang_cancel` over MCP.
- [ ] Expose bounded `marang_wait`, `marang_intervene`, `marang_inspect`, and
      `marang_get_artifact` only after their authorization/fencing tests pass.
- [ ] Add HTTP endpoints only where health, operations, or non-MCP clients need
      them.
- [ ] Test ambiguous client retries, caller-scoped idempotency, stale revisions,
      authentication, authorization, redaction, and response bounds.
- [ ] Ensure subordinate providers cannot recursively call Marang by default.

Exit: Codex can safely submit, leave, return, inspect, intervene, cancel, and
retrieve one immutable result through the service.

## Milestone 4b — MCP authentication and authorization

Status: **planned**

Localhost development runs without auth. Anything reachable beyond loopback
requires it. Auth is enforced at the HTTP/MCP boundary in `Marang.Server`;
Qingniao stays transport-unaware.

Stage 1 — API key (single operator, first remote deployments):

- [ ] Accept the key in the `Authorization` header only; never query string.
- [ ] Validate with constant-time comparison; missing or wrong key returns
      401 without touching delegation state.
- [ ] Read keys exclusively from environment (`.env` files, never committed
      or logged); support key rotation without code changes.
- [ ] Derive the caller identity from the presented key and attach it to
      `DelegationCallerScope` and diagnostics.
- [ ] Authorize every workspace reference against the caller's configured
      allowed roots before delegation starts.
- [ ] Verify OpenCode interop: `headers: { Authorization: ... }` on a
      `remote` MCP entry against a running server.

Stage 2 — OAuth (multi-user, enterprise):

- [ ] OAuth 2.1 with Dynamic Client Registration, compatible with OpenCode's
      automatic `mcp auth` flow; use the auth integration in
      `ModelContextProtocol.AspNetCore` rather than hand-rolled validation.
- [ ] Separate scopes for delegating vs supervising operations, so a client
      authorized to submit and poll cannot intervene or cancel.
- [ ] Per-caller rate limits, concurrency ceilings, and budget ceilings,
      reusing the Milestone 3 server limits.
- [ ] Audit every authentication decision (accept, reject, scope) into
      diagnostics without recording key material.

Tests (both stages): unauthenticated requests rejected before any state
change; wrong/rotated keys rejected; caller A cannot touch caller B's
delegations or workspaces; scoped tokens cannot exceed their scope.

Exit: the same delegation suite passes against an authenticated server,
and an unauthenticated client cannot observe or affect any delegation.

## Milestone 5 — Real provider and durable workflow integration

Status: **planned**

- [ ] Prove one bounded Codex process/app-server provider in an isolated
      candidate workspace and reconnect through its durable handle.
- [ ] Map Qingniao activities into released Zhinu steps, waits, signals,
      fencing, cancellation, and restart.
- [ ] Integrate Hongxian/Siming for session correlation and append-only audit;
      retain Zhinu as execution truth and reconcile cross-store effects through
      idempotent forward recovery rather than distributed transactions.
- [ ] Add Fuwen plan consumption only after its validation/binding gate is
      released and audited.
- [ ] Add Baize and Cangjie adapters without leaking their types into Qingniao's
      core contracts. Add Hetu only through the optional Hongxian/Hetu boundary
      and Marang's coding-specific composition after its dependency and failure
      acceptance tests pass.

Exit: a remote delegation survives service restart and produces verifiable,
session-linked evidence without duplicate external work.

## Milestone 6 — Marang + Hetu structural catch-up dogfood

Status: **planned after Fuwen usability, Hongxian optional-capability, and Hetu
historical-publication gates**

- [ ] Configure one Marang coding session with optional Hetu synchronization and
      prove an otherwise equivalent non-code Qingniao/Hongxian consumer has no
      Hetu package in its resolved dependency graph.
- [ ] Consume Hetu's immutable multi-repository workspace composition without
      flattening repository identity or assuming cross-repository resolution.
- [ ] Pin delegations to exact base source and workspace publications. After
      accepted integration, correlate the exact result source/publication and a
      bounded structural-delta reference in Hongxian evidence.
- [ ] Expose a minimal direct code-query MCP subset: workspace/repository list,
      index/freshness status, exact symbol/declaration lookup, bounded
      neighborhood/impact, current workspace publication, and changes since an
      exact publication. Names remain provisional until transport design.
- [ ] Add one higher-level preparation operation combining authorized workspace
      state, bounded Hetu impact, and Qingniao capability without automatically
      delegating or treating impact as an execution plan.
- [ ] Add one higher-level result-review operation combining the immutable
      Qingniao result, Hongxian evidence, factual Hetu delta, derived impact, and
      selected source/Git evidence without claiming the graph replaces review.
- [ ] Evaluate paired baseline and Hetu-assisted tasks on identical pinned code
      states. Record source reads/searches, relationships found and missed,
      accepted false facts, supervisor catch-up cost, and observable token use.
- [ ] Validate at least one non-Roslyn language plugin by comparing independently
      reviewed source facts with MCP results. Codex is an evaluator, not the
      oracle; confirmed parser/extractor/resolver/graph defects become tests.
- [ ] Exercise concurrent delegations as separate candidates followed by an
      explicit integration publication. Test stale, missing-history, failed
      synchronization, partial indexing, ambiguous identity, and oversized
      delta behavior.

Exit: Codex can resume after one real delegated code change from exact,
verifiable evidence and a bounded structural delta, with meaningfully less
source reconstruction and no loss of correctness. If the evidence does not show
material benefit, stop expanding federation and reassess the hypothesis.

## Milestone 7 — Guyabano dogfood and package hardening

Status: **planned**

- [ ] Consume Qingniao directly from Guyabano for one bounded activity.
- [ ] Verify that the embedded API does not require Marang service concepts.
- [ ] Exercise the same operation remotely through Marang and compare semantics.
- [ ] Refine Qingniao from both consumers before publishing its first preview.
- [ ] Publish `Penghou.Qingniao.Abstractions` and `Penghou.Qingniao` only after
      package/API compatibility, XML docs, multi-target tests, and isolated
      consumer tests pass.
- [ ] Add `Marang.Client` only if Guyabano or another real consumer needs a
      typed non-MCP remote client.

## Milestone 8 — Curated planning and checkpoint decision surfaces

Status: **planned after the durable supervision vertical and upstream recall
contracts**

Marang is the agent-facing façade, not the owner of memory, execution evidence,
pricing, reputation, or workflow state. Its MCP/HTTP views compose bounded
snapshots from authoritative providers and return their provenance and
freshness.

- [ ] Expose an enriched model-catalog view combining Baize capabilities and
  current availability/economics with Hongxian-derived contextual experience,
  sample size, recency, and evidence references.
- [ ] Expose bounded relevant-experience, similar-run, workflow-history, and
  model-usage-guidance views after Hongxian publishes the corresponding
  portable recall contracts.
- [ ] Expose a checkpoint decision package combining authoritative Zhinu state,
  revision-bound artifact/validation evidence, a delta-oriented Cangjie
  snapshot, relevant historical recovery experience, and explicit budgets.
- [ ] Return source snapshot/checkpoint identities, policy versions,
  truncation, staleness, degradation, and unsupported capabilities. Never merge
  conflicting claims into an unexplained answer.
- [ ] Record which returned context was actually supplied to a supervisor and
  which decision referenced it through Hongxian receipts.
- [ ] Enforce authentication, tenant/workspace scope, disclosure policy, and
  resource budgets before querying or returning cross-system context.

Names such as `GetAvailableModels`, `GetRelevantExperience`,
`GetSimilarRuns`, and `GetCheckpointContext` remain illustrative until the
portable upstream queries stabilize. Marang does not calculate reputation,
promote knowledge, execute model routing policy, or become a generic data
federation/query engine.

## Later, evidence-driven work

- Additional provider packages after at least two consumers need them.
- A2A integration after the provider-neutral contract is proven with a real
  interoperable agent.
- Multi-tenant operation, richer projections, collaboration, branching, and
  archival after the single-host durable slice works.
- Service UI and operator dashboards after MCP/HTTP behavior is stable.
- Adaptive-plan proposal, transition-preview, approval, and explanation tools
  after Fuwen and Zhinu publish the required authoritative contracts.

## Non-goals

- Publishing `Marang.Core` or `Marang.Abstractions` as the reusable capability.
- Making Qingniao a general workflow runtime, MCP gateway, session ledger,
  model SDK, or product-specific service.
- Letting remote or model-generated input grant provider, tool, filesystem,
  credential, budget, or promotion authority.
- Creating compatibility shims, client libraries, provider packages, or
  persistence packages before a demonstrated consumer requires them.
- Replacing the execution, workflow, session, memory, code-graph, or model
  primitives already owned elsewhere in Penghou.
- Mutating an active Zhinu graph or letting an AI proposal activate itself.
