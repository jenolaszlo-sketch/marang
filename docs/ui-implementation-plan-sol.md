# Marang V1 UI implementation handoff to Sol

Status: **ready for implementation; no UI work completed by this plan**.
Prepared 2026-09-27. Owner: Sol, UI implementation. Repository: `Marang`.

## Mission and source of truth

Build the first Marang workflow UI as a finished product experience. The first
screen must communicate the task, current work, attention state, and outcome.
The graph is the main exploration surface; evidence and explanations make it
credible. A developer console with a graph attached does not meet the brief.

Read these in order:

1. [Reviewed V1 specification](progress-workflow-observability-v1.md), including
   its contract corrections and release evidence. This governs implementation.
2. [V1 roadmap track](roadmap.md#v1-release-track--live-workflow-visibility-and-product-experience).
3. [Service architecture](architecture.md) and
   [ownership decision](decisions/0015-qingniao-extraction-and-marang-service-boundary.md).
4. [Original proposal](progress-workflow-observability-proposal.md) for context;
   do not restore its superseded ownership, recovery, or singular-activity model.

This plan makes implementation choices within that scope. Preserve existing
uncommitted documentation and other contributors' work. Recheck repository
instructions and current code before starting; the inventory below is dated.

## Starting point and scope

At handoff, `src/Marang.Server` is a .NET 10 host exposing `/mcp`, `/healthz`,
and `/readyz`. `Program.cs` authenticates only `/mcp`. There is no frontend
package, HTTP run projection, SignalR hub, or browser session implementation.
The existing CI builds/tests/packs .NET; `tests/Marang.Tests` targets .NET 8/10.
Do not treat these planned browser APIs as already available.

Sol owns the frontend application, fixtures, client contract/adapters, graph
layout and interaction, responsive/accessibility work, browser tests, frontend
CI, and minimal static-host integration. Runtime projection, durable storage,
authoritative event production, run authorization, and browser authentication
are backend dependencies. Document the required contracts and integration
failures; do not implement another workflow runtime to make the UI appear live.

Deliver two separately reported milestones:

- **UI preview ready:** complete, interactive, clearly labeled sample experience,
  with deterministic scenarios and the real adapter boundary in place.
- **V1 integrated ready:** the same experience uses authenticated real state and
  passes the real-run, recovery, usability, and performance release gates.

Finish all unblocked frontend work if backend dependencies are unavailable.
Report exact remaining integration gates; a sample-only build cannot be called
V1 complete. This handoff does not include workflow editing, browser run creation,
new adaptive approval policy, replay/time travel, analytics, or experiments.

## Design direction

Use a calm, precise workspace, with a warm near-white canvas, dark ink text,
teal active accents, amber attention states, and restrained red failure cues.
Superseded work uses a dashed treatment and an explicit label, not washed-out
unreadable text. Choose one coherent light theme first; dark mode is optional.
Use a readable system sans-serif stack and monospace only for identifiers or
technical output. Avoid a dependency on remotely loaded fonts or images.

Create tokens for surface/text/border/state colors, spacing, radii, typography,
elevation, and motion. Start from a 4-pixel spacing scale, 14–16 px body text,
compact metadata, and a clear page-title hierarchy; validate at actual viewport
sizes rather than shrinking labels to fit. Use a consistent icon set with text
labels. Color must never carry status alone.

At 1440×900, prioritize a compact run header, generous graph, and roughly
340–400 px inspector. Make the inspector resizable within readable bounds. At
1024×768, support a collapsible inspector without losing selection. At 390×844,
default to a workflow list and a full-width detail view with a clear Back action.
Provide graph/list switching on larger screens too. Breakpoints can adjust
during implementation; the three acceptance viewports are fixed.

The header shows task title and workspace, a plain-language current-state line,
counts of completed/running/waiting work, elapsed time, and connection freshness.
Counts describe the current revision and identify changes of scope. Do not
count superseded work toward current completion or imply that unknown progress
is zero. Run outcome and connection state are independent.

Treat these as designed screens, not later CSS fixes: first launch, loading,
parallel execution, waiting for MCP supervision, checkpoint evaluation,
revision change, failed attempt, cancelled run, reconnecting, session expired,
and completed result with artifacts. Prefer subtle 120–180 ms state transitions;
remove nonessential motion under reduced-motion preferences.

## Proposed application structure and tools

Create `src/Marang.Ui` with React, TypeScript in strict mode, Vite, npm and a
committed lockfile. Use React Flow (`@xyflow/react`) for interaction and ELK for
layout. Use CSS tokens and custom nodes/edges; do not ship the default graph
appearance. Start with CSS transitions instead of adding an animation library.
Choose compatible stable versions and a supported Node version at implementation
time; record/pin the toolchain. Do not introduce a full dashboard framework.

Use Vitest for state/contract tests, Testing Library for meaningful component
behavior, and Playwright for browser journeys and selected visual baselines.
These are proposed tool choices; document a concrete compatibility reason for
any substitution. No extra state library is required initially: use a normalized
run store with selector subscriptions and keep local interaction state separate.

```text
src/Marang.Ui/
  package.json, package-lock.json, vite.config.ts, tsconfig*.json
  src/
    app/                 routes, app shell, error boundary, source selection
    design/              tokens, global styles, shared accessible controls
    contracts/           versioned DTOs, runtime parsing, cursor utilities
    data/                RunDataSource, fixture and HTTP/SignalR adapters
    state/               run reducer, selectors, stream reconciliation
    features/runs/       RunList, RunHeader, ConnectionStatus, OutcomeSummary
    features/workflow/   WorkflowCanvas, WorkflowList, nodes, edges, layout
    features/inspector/  Summary, Journal, Evidence, Attempts, checkpoint view
    features/revisions/  RevisionSummary, MilestoneStrip
    fixtures/            scenarios, snapshots, event scripts, safe sample assets
  tests/                 browser journeys, fixtures, visual baselines
docs/ui-contract.md       client/backend agreement and outstanding gaps
docs/ui-development.md    commands, source modes, hosting and testing
docs/ui-validation.md     evidence index, measured results, remaining gates
```

Directories indicate responsibility, not an instruction to create empty files
or one abstraction per component. Co-locate unit tests with their implementation.

Use UI routes `/ui/runs`, `/ui/runs/:runId`, and `/ui/sample/:scenarioId`.
Node, attempt, and inspector-tab selection may be encoded in query parameters
for deep linking and Back/Forward navigation; validate IDs against the loaded
run and gracefully handle unavailable selections. Keep journal text, credentials,
and artifact contents out of URLs and persistent browser storage. A separate
sample route must never silently replace a failed real-run request.

## Contract handshake before live integration

Write `docs/ui-contract.md` and representative versioned JSON fixtures during
the first slice. Names below are client requirements to agree with the backend,
not a second set of frozen server contracts.

| Dependency | UI needs | If unavailable |
| --- | --- | --- |
| Run list | Authorized paginated summaries, stable paging/filter semantics, title/workspace, status and attention | Finish sample list; mark live discovery unavailable |
| Snapshot | Schema version, run state, graph, checkpoints, revisions, attempt summaries, concurrent active executions, source generation, durable watermark and freshness | Develop against the matching fixture; never derive authority from journal text |
| Event stream/history | Subscription barrier, durable cursor, event ID, typed payload, durable base for telemetry, catch-up pages, retention floor, watermark notifications | Complete a scripted transport and recovery tests; live status remains unavailable |
| Activity/journal | Attempt identity, entry ID/order, cursor pagination, revision/watermark, bounded typed content and disclosure indicators | Show explicit unavailable/partial state; no fabricated transcript |
| Artifacts | Authorized reference, display metadata, disposition/content type, safe retrieval and expiry behavior | Provide labeled fixture assets; real unavailable links explain the state |
| Browser session | Session/caller state, sign-in entry point, expiry/logout behavior, scope, authorized HTTP and hub joins | Sample mode works; real mode requires sign-in and cannot bypass auth |
| Optional commands | Advertised capability plus exact authorization/fencing and result receipt | Show supervision via MCP; omit nonfunctional command controls |

Define a narrow `RunDataSource` seam for list, snapshot, activity/journal,
revisions, artifact access, subscription, and durable catch-up. Both adapters
produce identical parsed domain events and snapshots. Inject the clock and
transport scheduler for deterministic tests. Components never fetch from a hub
or parse raw JSON directly. Abort pending reads, leave subscriptions, and reject
late responses when changing run, source mode, or authenticated session.

Represent 64-bit cursors as decimal strings at the JSON boundary and compare
numerically without lossy JavaScript `Number` conversion or lexical ordering.
Distinguish execution outcome from current/superseded membership; attempts from
activities; proposed/admitted revisions from activated revisions. Keep schema
parsing/version checks at the adapter boundary. Unknown structural data triggers
an explicit unsupported/resync state, not silent successful rendering.

Normalize entities by stable identity. Keep viewport, inspector selection,
expanded branches, filters and scroll anchors in a separate view-state store.
Keep transient progress separate from durable truth, fence it by execution and
generation, and never let it change a terminal outcome. Apply a revision as one
validated graph update; no dangling edges or half-replaced paths may render.

Implement the reviewed spec's subscribe-buffer-snapshot-catch-up protocol.
Connection states should include connecting, synchronizing, live, reconnecting,
offline, expired session, and incompatible data. Only reconciled state is live.
Deduplicate durable events; recover durable gaps; ignore expected transient
gaps; resnapshot on retention expiry or bounded-buffer overflow. Reauthorize
and rejoin after reconnect. A transport reconnect alone cannot mark recovery
complete. Keep the last authorized state visible with its age during temporary
network loss; clear protected data on logout or loss of authorization.

## Ordered implementation slices

Each slice is a reviewable change set with a working preview. Update its status
and evidence in the roadmap as it lands. Visual review is part of the work,
not an automatic requirement to wait for new user permission between slices.

### UI-01 — Scaffold, contracts, and first credible screen

Depends on: repository inventory only. Maps to roadmap V1.1.

- [ ] Scaffold the application, strict types, scripts, tokens, and route shell.
- [ ] Define the data-source seam and the first versioned snapshot/event fixture.
- [ ] Build one polished run screen: header, custom graph nodes, checkpoint,
  two concurrent activities, selected-node summary, and milestone strip.
- [ ] Add a permanently visible Sample indicator and explicit source selection.
- [ ] Capture desktop and narrow-screen previews and correct hierarchy,
  typography, whitespace, and clipping before adding more features.

Exit: the screen looks intentional and explains the sample task at a glance;
node selection works, and it does not depend on backend availability. Deliver
this visual slice early rather than spending the first iteration only on plumbing.

### UI-02 — Run navigation and complete scenario fixtures

Depends on: UI-01. Maps to V1.1 and V1.4.

- [ ] Implement run list/search/status filters, direct links, Back/Forward,
  loading/error/empty states, and sample onboarding with MCP guidance.
- [ ] Build a fixed-clock main scenario: plan → implementation → parallel
  validation/review → checkpoint → revision → retry → evidence and outcome.
  Preserve superseded work with its original outcome and attempts.
- [ ] Add separate scenarios for waiting, failure, cancellation, no provider,
  no plan yet, missing evidence, long labels, and large graphs/journals.
- [ ] Add deterministic event stepping and network fault controls in a sample
  developer panel. Keep them out of real-run product controls.

Exit: every required product state is reproducible without random IDs, wall-clock
timing, or live providers. Synthetic sample artifacts are clearly identified.

### UI-03 — Stable workflow graph and accessible equivalent

Depends on: UI-02. Maps to V1.4.

- [ ] Implement plan/activity/checkpoint/decision/fan-out/join/completion nodes
  and an unknown-type fallback. Render tool nodes only when topology supplies
  them; do not turn every journal tool call into a graph node.
- [ ] Implement semantic edge/node styles, a legend, search, fit, focus active,
  graph/list switching, and retained/expanded superseded branches.
- [ ] Layout off the interaction path when practical. Key layout by topology,
  not progress; discard stale asynchronous layout results after run/revision
  changes. Preserve unaffected positions using cached anchors and local layout;
  if overlap requires broader layout, retain selection and viewport anchor.
- [ ] Fit on initial load only. Disable graph editing/drag-to-change-topology.
  Keyboard users can select and inspect nodes. Collapse cannot hide the current
  selection without an explicit visible replacement/expansion affordance.
- [ ] Provide the same state, branches, selection, and evidence access through
  WorkflowList. Avoid forcing a huge canvas onto a phone-sized viewport.

Exit: a progress burst cannot relayout the graph; a revision cannot steal focus;
all concurrent work is visible; the list supports equivalent inspection.

### UI-04 — Inspector, evidence, checkpoint and revision explanation

Depends on: UI-03. Maps to V1.4.

- [ ] Build Summary, Journal, Evidence, and Attempts with explicit selection of
  current and historical attempts. Summaries distinguish claims from validation.
- [ ] Paginate/virtualize journals, group repeated updates, filter entry types,
  cap inline output, and link larger payloads through artifact retrieval.
- [ ] Preserve scroll anchors when older pages prepend and updates append. If
  the user is reading history, show a new-update indicator instead of scrolling.
- [ ] Build checkpoint reasons, automatic vs supervisor wait state, evidence,
  outcome, and resulting revision. Build a revision card showing reason,
  retained/replaced work, and next steps; never infer this from free-form text.
- [ ] Build terminal outcome views with artifacts and validation, keeping the
  graph accessible. Treat unknown cost/usage/progress honestly.
- [ ] Render untrusted text safely; use approved link protocols and authorized
  artifact references. Do not embed arbitrary HTML, remote content, or execute
  sample/tool output. Authentication failures clear protected content.

Exit: someone can explain a revision, distinguish retry outcomes, and obtain a
result without reading the entire journal. Scrolling and selection remain stable.

### UI-05 — Real adapter and recoverable observation

Depends on: UI-02 contract and backend snapshot/history/session/hub capabilities;
can proceed alongside UI-03/04 when those capabilities exist. Maps to V1.2/V1.3.

- [ ] Implement runtime-validated HTTP and SignalR adapters with bounded reads,
  subscriptions, cleanup, request cancellation, and reconciliation state.
- [ ] Exercise the subscription race, duplicate/reordered deltas, missed tail
  event, transient drops, full-buffer resnapshot, and offline completion.
- [ ] Map HTTP/stream errors to meaningful states, including session expiry,
  forbidden/missing run, incompatible schema, projection lag, and history expiry.
- [ ] Preserve the graph on a temporary disconnect; recover through the agreed
  handshake without losing inspector selection. Never fall back to sample data.
- [ ] Verify cookies/session flow and authorized artifact retrieval against the
  actual host; ensure no API key appears in bundles, URLs, storage, or logs.

Exit: a real authorized browser can observe, disconnect, and recover a completed
run. If upstream endpoints are absent, report UI-05 blocked with concrete missing
contracts while completing the other slices; transport mocks do not close it.

### UI-06 — Host packaging, responsive finish, and accessibility

Depends on: UI-01–04; authenticated host smoke also requires UI-05. Maps to V1.4.

- [ ] Build static assets with base `/ui/`; integrate them into the Marang.Server
  publish output. Scope SPA fallback to `/ui` routes so API/hub/MCP/health paths
  keep their own response semantics. Missing assets must remain errors.
- [ ] Provide a development proxy for API/hub traffic, including WebSockets;
  use the same-origin server for final authentication verification.
- [ ] Keep .NET-only development usable; document an explicit release command
  that builds UI assets then publishes the server and fails if UI assets are
  missing. Node is a build dependency, not a production server dependency.
- [ ] Add frontend CI for lockfile install, type checking, linting, unit tests,
  production build, and browser checks. Retain existing .NET CI. Ignore generated
  dependencies/build output/reports, while retaining fixtures and reviewed baselines.
- [ ] Verify all acceptance viewports, 200% zoom/reflow, keyboard-only navigation,
  screen-reader naming/status, contrast, reduced motion, and focus restoration.
  Announce meaningful state changes without reading every progress event aloud.

Exit: the published host serves deep-linked UI pages correctly; UI assets work
under `/ui/`; existing service routes remain intact; all layouts are usable.

### UI-07 — Verification and release evidence

Depends on: UI-01–06 and real upstream execution for integrated readiness.
Maps to V1.5.

- [ ] Complete the verification matrix below and document actual results.
- [ ] Run the real provider demonstration from the reviewed spec, including an
  authoritative revision, parallel work, retry, disconnect, and final artifacts.
- [ ] Run the unfamiliar-user evaluation. If participants are unavailable,
  record that gate as pending; do not substitute agent self-evaluation.
- [ ] Address observed visual/usability issues, update validation evidence, and
  report preview readiness separately from integrated V1 readiness.

Exit: reviewed release gates have evidence, or clearly enumerated remaining
dependencies. No claims of measured performance without recorded measurements.

## Verification matrix

Test invariants and user behavior, not copies of component implementation.

| Layer | Minimum meaningful coverage |
| --- | --- |
| Reducer/contract | Parallel activity sets; completed-and-superseded state; retry identity; atomic revision; terminal state protected from stale telemetry; decimal cursors above 2^53; unsupported schema |
| Stream reconciliation | Duplicate/out-of-order events; durable vs transient gaps; terminal event during handshake; reconnect/rejoin; retention expiry; bounded-buffer overflow; late responses from previous run/session |
| Browser behavior | Deep-link selection, Back/Forward, focus active, graph/list equivalence, stable viewport, inspector attempt switching, journal scroll/prepend, revision reason, artifact access, sample labeling |
| Security boundary | 401/403/session expiry; protected cache cleared; malicious journal markup inert; unsafe URLs rejected; no cross-run data during navigation; no credential leakage |
| Host integration | Published UI load and refresh under `/ui/`; assets; authenticated HTTP/hub smoke; API/hub/MCP/health responses unaffected by SPA fallback |
| Visual/accessibility | Required product states at 1440×900, 1024×768, 390×844; fixed fixture clock/fonts/browser for screenshots; keyboard and screen-reader checks, reduced motion, zoom/reflow |
| Performance | Reviewed 100-node/150-edge budgets: p95 usable view ≤2 s, selection ≤100 ms, committed update visible ≤1 s; record machine/browser/build/network and sample count |
| Stress | 500 nodes, 10,000 paginated entries, 50 transient updates/s for 10 min; record queue/memory behavior, responsive interaction, cleanup, and explicit grouping/limits |

Use automated visual comparisons for a focused set of stable screens plus
manual inspection of the full state matrix. Do not approve regenerated baselines
without inspecting them. Browser functional tests do not replace the reviewed
4-of-5 unfamiliar-user comprehension gates.

Define these scripts in `src/Marang.Ui/package.json` and document their usage:
`dev`, `build`, `preview`, `typecheck`, `lint`, `test`, `test:e2e`, and
`test:visual`. Run the relevant frontend checks after each slice. Run the existing
.NET build/tests and formatting check when host integration changes. These are
planned commands, not commands verified by this handoff.

Record screenshots/traces and a short demo recording when available under an
ignored artifact directory; link the retained review artifacts from
`docs/ui-validation.md`. Include commit/build identity, fixture or real-run
identity, browser/machine, commands, results, and pending gates. Never commit
private real-run payloads or credentials as fixtures.

## Completion report expected from Sol

Provide the preview URL and exact startup commands, the completed UI slices,
screenshots of running/revised/completed and narrow views, checks run with actual
results, backend contract changes, and remaining integration/release gates.
Update the roadmap only for demonstrated work. Distinguish unavailable endpoints
from frontend defects and fixture evidence from real-run evidence.

Keep routine implementation choices moving. Escalate only a concrete conflict
with the governing spec or a dependency that prevents the next required result.
Do not expand this assignment into upstream runtime redesign or V2 features.

## Ready-to-use handoff prompt

> Implement the Marang V1 workflow UI in this repository using
> `docs/ui-implementation-plan-sol.md` and the governing reviewed specification
> `docs/progress-workflow-observability-v1.md`. Start with UI-01 and deliver an
> early polished preview, then work through all unblocked slices. The UI is a
> V1 release gate: prioritize clear run state, stable graph interaction,
> checkpoint/revision explanations, evidence, and finished difficult states.
> Use deterministic labeled samples and the same reducer/data-source boundary
> as real HTTP/SignalR integration. Preserve existing edits and Penghou ownership
> boundaries. Do not bypass authentication or imply fixtures are live. Verify
> browser behavior and visual quality, update the roadmap with evidence, and
> report preview readiness separately from real integrated V1 readiness. Complete
> frontend work when backend dependencies are unavailable and list exact remaining
> contract/integration gates. This assignment does not authorize V2 scope.

## Implementation references

- [React Flow performance guidance](https://reactflow.dev/learn/advanced-use/performance):
  use targeted subscriptions and stable component/callback definitions to avoid
  unnecessary graph rerenders; validate behavior under the stated load.
- [Vite static build guidance](https://vite.dev/guide/static-deploy.html):
  build assets for the configured base; `vite preview` is a local check, not the
  production host. Marang.Server serves the release assets.
- [Playwright visual comparisons](https://playwright.dev/docs/test-snapshots):
  keep the rendering environment consistent and inspect baseline changes.
