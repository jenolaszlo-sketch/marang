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

## V1 release track — Live workflow visibility and product experience

Status: **planned; required for V1 release**. Added 2026-09-27.

Reviewed specification and acceptance contract:
[Live workflow visibility and product experience](progress-workflow-observability-v1.md).
UI execution handoff: [Sol implementation plan](ui-implementation-plan-sol.md).
The [original proposal](progress-workflow-observability-proposal.md) is preserved
for traceability; the reviewed specification resolves its architecture and
recovery gaps. These are planned capabilities, not completed implementation.

The workflow UI moves out of the later-work bucket. Its first impression is a
product requirement: users must quickly understand the goal, current work,
attention state, changes of plan, and outcome. Visual polish, stable interaction,
and truthful failure/reconnect behavior are release gates alongside the API.

This track spans Milestones 3–5. UI fixtures and contract design can start now
without interrupting Fuwen's active implementation priority. Real activation,
durable recovery, and end-to-end release evidence depend on the released
Fuwen/Zhinu/Qingniao/Hongxian boundaries in Milestone 5. V1 observes admitted and
activated revisions; new adaptive proposal/approval tools and experiments remain
in the later supervision/V2 track. Do not relabel a fixture as live integration
or silently remove revision visibility if an upstream dependency is late.

### V1.1 — Product storyboard and representative UI
Implementation note (2026-09-27): UI-01 is underway in `src/Marang.Ui` with a
labeled sample graph, concurrent activity state, checkpoint/revision example,
inspector, and mobile list. The first live slice now lists and polls
caller-scoped Qingniao delegations through `/api/delegations`; it does not
present them as workflow graphs. MCP reads and actions now enforce the same
caller ownership. The server still has no execution provider, durable run
projection, or remote browser session. Build and initial browser review passed. See
[UI validation](ui-validation.md) and the [missing backend contract features](ui-contract.md).
The sample preview and integrated V1 gates remain open.

- [ ] Define Marang typography, spacing, semantic state colors/icons, node
  treatments, focus behavior, and restrained motion; build one coherent theme.
- [ ] Build the run list, direct run view, summary header, primary workflow
  graph, activity inspector, and checkpoint/revision milestone strip against
  deterministic fixtures. A fresh install offers a clearly labeled sample and
  guidance for starting real work through MCP.
- [ ] Cover parallel work, retry, checkpoint evaluation, superseded branches,
  waiting for a supervisor, failure, cancellation, and a useful completed result.
- [ ] Review the first impression before completing the backend: the user sees
  the goal, what is happening, whether attention is needed, and resulting evidence.

Exit: a coherent sample can be explored from plan to artifacts, including one
explained revision, with custom Marang styling and no decorative dead controls.

### V1.2 — Semantic events, identities, and durable projection

- [ ] Define transport-neutral progress reporting with distinct run, node,
  activity, execution attempt, revision, checkpoint, and edge identities;
  explicitly map upstream instance/generation and receipt identities.
- [ ] Consume authoritative receipts: Fuwen owns workflow semantics, Zhinu
  execution/activation, Qingniao delegation, Hongxian continuity/evidence.
  Marang owns the authorized product projection, not another workflow engine.
- [ ] Add versioned event identities/payloads, run ordering, separate durable
  recovery cursor, attempt/generation fencing, and idempotent ingestion.
- [ ] Project concurrent active executions, honest nullable progress, separate
  execution outcome and superseded membership, checkpoint reasons/results,
  immutable attempt history, and atomically applied revisions.
- [ ] Persist event/projection watermarks consistently; recover from durable
  source receipts after restart. Bound transient queues and isolate slow sinks.
  Keep execution independent of browser/MCP observers and report projection lag.

Exit: deterministic events produce the correct graph and attempt history;
duplicates, late old-generation events, restart, and dropped telemetry cannot
corrupt current state. Reusable cores have no Marang/HTTP/SignalR/UI dependency.

### V1.3 — Authorized HTTP, journals, and live recovery

- [ ] Expose bounded run list/snapshot, workflow, revision list/detail, durable
  event history, activity detail, and cursor-paginated journal reads under
  `/api/marang/runs`; reuse authorized artifact retrieval.
- [ ] Add stable journal entry IDs, attempt linkage, meaningful summaries,
  evidence references, disclosure/redaction, retention, and truncation indicators.
- [ ] Add typed SignalR deltas at `/hubs/marang/runs`, authorized run subscription,
  rejoin, a race-free subscribe/snapshot barrier, duplicate suppression, durable
  catch-up, watermark reconciliation, and explicit expired-cursor resnapshot.
- [ ] Extend the current MCP-only authentication boundary to HTTP and hub
  access; implement a browser-compatible authenticated session without exposing
  service API keys. Enforce run/workspace scope, session expiry, and read bounds.
- [ ] Correlate MCP request/progress/task identities when supplied with the same
  run; preserve token types. Progress cancellation/disconnection cannot cancel
  execution. Correlate operational telemetry without using it as run truth.

Exit: an independently connected browser reconstructs and follows a run,
including one that finishes during disconnect or initial subscription; missing
transient events never cause an endless durable-recovery loop. Unauthorized
reads and subscriptions disclose nothing.

### V1.4 — Finished live workflow experience

- [ ] Replace fixture transport with the real projection and stream using the
  same state reducer. Show all concurrent work and explain checkpoint/revision
  changes, retained work, superseded paths, and the next steps.
- [ ] Preserve viewport, node positions where possible, selection, and journal
  scroll during updates. Provide fit/focus controls, node search, branch
  expansion, new-update indicators, and accessible list navigation.
- [ ] Provide Summary, Journal, Evidence, and Attempts inspection with bounded
  tool output; distinguish worker claims, validation evidence, and final outcome.
- [ ] Design loading, empty, unavailable, partial, failed, cancelled, stale,
  reconnecting, and completed states. Display connection health separately from
  execution health; show percentages, cost, or ETA only with supporting data.
- [ ] Make keyboard access, screen-reader labels, contrast, reduced motion,
  200% zoom/reflow, and narrow-screen list/inspector layouts part of the design.
- [ ] Surface supervisor actions only where an authorized, fenced server
  capability exists; otherwise identify the MCP supervision path explicitly.

Exit: live updates improve understanding without moving the user's reading
position; the completed view presents usable outcomes and evidence.

### V1.5 — Release demonstration and UX acceptance

- [ ] Demonstrate one real provider run through parallel work, retry, checkpoint,
  authoritative revision, retained superseded branch, browser disconnect/recovery,
  and final artifacts. Exercise a separate failure/waiting scenario.
- [ ] Meet the reviewed spec's first-impression and comprehension gates with
  unfamiliar users (at least 4 of 5 succeed); retain findings and fixes.
- [ ] Review screenshots and browser interactions for every major state at
  desktop, compact, and narrow widths; verify keyboard/screen-reader flows.
- [ ] Measure the specified responsiveness and bounded-load budgets on a
  recorded reference setup; treat targets as acceptance criteria, not claims
  of measured performance until results exist.
- [ ] Prove restart, mixed durable/transient gaps, duplicate/out-of-order events,
  offline completion, atomic revision updates, history expiry, slow consumers,
  authorization isolation, and safe journal rendering.

Exit: both the visible product experience and real execution/recovery satisfy
the reviewed acceptance contract. V1 is not complete with only a static graph,
a polished fixture, or a working MCP/API surface.

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
- [x] Remove the empty `Marang.Hosting` scaffold (unreferenced
      assembly-marker only; `Marang.Mcp` is the real MCP project).
- [x] Add architecture/dependency tests proving Qingniao has no reference to
      Marang, ASP.NET Core, MCP transport DTOs, or product configuration.
- [x] Keep the old `Marang` and `Marang.Abstractions` preview packages
      superseded; do not publish forwarding packages without a real consumer.

Exit: all reusable delegation code builds and tests under Penghou.Qingniao
names, with no Marang product dependency.

## Milestone 2 — Complete the Qingniao in-memory delegated-execution runtime

Status: **Marang-side complete; runtime lives in Qingniao post-extraction**

Most of this milestone as originally written now lives in the extracted
`Penghou.Qingniao` repository (M2.1–M2.8 supervision slice, consumed at
`0.1.0-preview.2`): deterministic coordinator, plan resolver, acceptance
registry, provider snapshot, adapter catalog, early-handle store, atomic
execution store, Queued submission with a deterministic pump, seal/test/review
flow, supervisor checkpoint with bounded re-entry and revision-fenced
intervention, and the outcome matrix. Captured-handle recovery was additionally
proven live against the real Codex CLI in Qingniao M3.

Marang-side remainder:

- [x] Reference `Penghou.Siming` `0.1.0-preview.7`.
- [x] Implement the Siming-backed canonical semantic-fingerprint producer and
      verifier; do not copy canonicalization logic (`MarangDelegationFingerprint`
      over Siming canonical JSON v2; the host acceptance catalog stores the
      fingerprint beside each record and treats mismatches as unknown
      delegations).
- [x] Cover success, no provider, unauthorized adapter, rejection,
      cancellation, budget exhaustion, transport ambiguity, worker failure,
      `WaitingForSupervisor`, and `NeedsSupervisor` through the composed MCP
      surface (plus fingerprint determinism, field sensitivity, and tamper
      rejection).
- [ ] Durable cross-restart handle recovery and the remaining runtime proofs
      stay with Milestone 5 durable execution (the in-memory index and runtime
      are volatile by design; see the V1 track).

Exit: Qingniao can be embedded and tested without MCP, ASP.NET Core, real
providers, or a workflow database. (Satisfied via the Qingniao extraction;
Marang composes it.)

## Milestone 3 — Scaffold Marang.Server

Status: **partially complete — host scaffolded with probe; composition pending**

- [x] Create a non-packable ASP.NET Core executable (minimal-API style; no MVC
      controllers yet).
- [x] Add a health endpoint (`/healthz`) and an MCP transport probe
      (`marang_ping`).
- [x] Reference Qingniao and make Marang.Server the composition root
      (`DelegationRuntime` singleton with `MarangAdmissionVerifier` and
      `ImplementVerificationPolicy`; no providers registered yet).
- [x] Add configuration validation (fail-fast authentication options),
      health (`/healthz`) and readiness (`/readyz`, proves the runtime
      resolves) endpoints.
- [ ] Add structured diagnostics, graceful shutdown tuning, and bounded
      background dispatch (no background work exists yet).
- [ ] Define remote caller/tenant identity and authentication extension points.
- [ ] Resolve workspace references, provider profiles, disclosure policy, and
      budget ceilings under server authorization.
- [ ] Keep transport DTOs independent from Qingniao domain contracts.
- [ ] Add service-level request, response, concurrency, and rate limits.

Exit: Marang hosts the fake Qingniao vertical slice as a secure local service.

## Milestone 4 — MCP and HTTP supervision surface

Status: **planned**

- [x] Expose `marang_delegate`, `marang_status`, `marang_result`, and
      `marang_cancel` over MCP (backed by the composed runtime; delegate
      pumps bounded steps, reads are null-safe, cancel is fenced).
- [x] Expose bounded `marang_wait`, `marang_intervene`, `marang_inspect`, and
      `marang_get_artifact` with authorization and fencing tests (fenced
      checkpoint/revision actions report rejections; supervisor identity
      comes from the authenticated context).
- [ ] Add the bounded run-observation HTTP endpoints and SignalR stream in the
      V1 release track, alongside health and operational endpoints.
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

- [x] Accept the key in the `Authorization` header only; never query string
      (`Bearer` scheme; anything else is rejected).
- [x] Validate with constant-time comparison; missing or wrong key returns
      401 without touching delegation state (middleware short-circuits
      before any endpoint).
- [x] Read keys exclusively from environment (`Marang__ApiKeys__<caller>`;
      never committed or logged); support key rotation without code changes
      (env change plus restart).
- [x] Derive the caller identity from the presented key and attach it to
      `DelegationCallerScope` and diagnostics (middleware sets it on
      `HttpContext.Items`; tools never take a caller argument).
- [x] Authorize every workspace reference against the caller's configured
      allowed roots before delegation starts (`Marang__AllowedWorkspaceRoots`;
      loopback dev identity pre-authorized for the default workspace).
- [x] Verify OpenCode interop: `headers: { Authorization: ... }` on a
      `remote` MCP entry against a running server (verified at protocol
      level: initialize handshake, `tools/list` of all 9 tools, and a
      `marang_delegate` call returning a fenced delegation over Streamable
      HTTP; header auth proven with 401/401/401/pass-through matrix).

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
- [x] Publish `Penghou.Qingniao.Abstractions` and `Penghou.Qingniao`
      (`0.1.0-preview.2` with API baselines, XML docs, multi-target tests,
      and isolated consumer tests).
- [ ] Refine Qingniao from both consumers before any stable release.
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
- Advanced operator dashboards and fleet analytics after the V1 workflow UI;
  the live run viewer, journals, checkpoints, and revisions are V1 release gates.
- Adaptive-plan proposal, transition-preview, approval, and explanation tools
  after Fuwen and Zhinu publish the required authoritative contracts.

## Cross-consumer extraction watchlist

Reusable patterns both Guyabano and Marang need. None is extracted until two
consumers show near-identical code; until then each keeps its own copy:

- Qingniao composition (`DelegationRuntime` plus in-memory registries and
  product policy): `Marang.Server` wires it now; Guyabano's embedded-Qingniao
  dogfood will wire it next. Extract a hosting helper only on convergence.
- MCP delegation-tool patterns (bounded pump, null-safe reads, fenced
  cancel): `MarangDelegationTools` now; compare with Guyabano's Codex web
  gateway before sharing anything.
- Hongxian session mapping: Guyabano's `HongxianGuyabanoSessionStore` is
  done; compare when Marang lands its M5 session correlation.
- Approved-root validation: the Codex adapter, Marang workspace
  authorization, and Guyabano staging each check containment independently;
  share only if a third copy appears.

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

## V2 — Evidence-driven workflow evolution (deferred)

Status: **future work after V1; not a current release gate**. Added 2026-09-26.
V2.1/V2.2/V2.3 name cross-project delivery stages, not package or IR versions.
Existing near-term priorities and completed work retain their current status.

Architecture and shared acceptance gates: [reviewed V2 specification](../../Penghou.Guihua/docs/evidence-driven-workflow-evolution-v2.md).
Cross-repository links assume sibling checkouts.

### V2.1 — Reviewable outcome-driven repair

- [ ] Extend the existing adaptive-planning and Milestone 8 decision surfaces
  with bounded views separating executor receipts, evaluator judgments,
  acceptance decisions and current/superseded revisions.
- [ ] Present an exact-base Guihua proposal plus Fuwen comparison and Zhinu
  transition preview: retained artifacts, validation-only reruns, invalidated
  dependents, external effects, budget and unresolved evidence.
- [ ] Bind approval/override to actor, scope, exact revision/generation,
  preview/policy and idempotent operation identity. Reject stale actions and
  show the authoritative transition receipt and projection lag.
- [ ] Preserve human intervention rationale as evidence; a terminal result is
  never reopened merely to display a later evaluation.

Gate: a supervisor can reject an outcome, approve one replacement, inspect why
work was reused/rerun, and recover an ambiguous response without duplicate
activation.

### V2.2/V2.3 — Experiment and preference explanations

- [ ] Show candidate differences, fixed rubric, hard constraints, total budget,
  effect isolation, losing/partial evidence and selected/inconclusive/none-
  acceptable outcomes through bounded authorized views.
- [ ] Explain when pinned relevant evidence skipped an experiment, including
  source checkpoints, version/scope compatibility, freshness, uncertainty and
  contrary evidence; expose unavailable recall and declared fallback.
- [ ] Keep tool names illustrative until upstream contracts stabilize, and
  reuse existing artifact retrieval, authentication and response bounds.

Gate: returned decision packages expose enough evidence to review the choice
without raw transcripts or cross-scope disclosure. Guihua, Zhinu, Hongxian,
Cangjie, Baize and host policy retain their existing authorities.
