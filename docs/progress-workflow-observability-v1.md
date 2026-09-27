# V1: Live workflow visibility and product experience

Status: **reviewed direction; implementation planned**. Reviewed 2026-09-27.

This document refines the [original proposal](progress-workflow-observability-proposal.md)
and supplies the acceptance contract for the [roadmap](roadmap.md). Where they
differ, this review governs planned implementation. The proposal's sample C#
records and package names are illustrative, not frozen APIs.

## Product decision

The workflow UI is a V1 product surface and release gate. A first-time viewer
should understand what Marang is doing, what changed, what needs attention, and
what it produced without reading raw logs or knowing Penghou internals. Visual
quality means clear hierarchy, calm live behavior, readable evidence, and a
convincing finished result, as well as attractive styling.

Keep the proposal's graph, live activity progress, journals, checkpoints,
revision lineage, HTTP snapshots, SignalR updates, MCP correlation, and durable
history. Start the UI against deterministic fixtures while backend contracts
are developed; do not wait until the service is finished to discover UX issues.
Fixtures demonstrate intended behavior and must be visibly labeled as samples.
V1 release still requires the same experience on real execution.

## Review findings and required corrections

| Proposal area | Review decision |
| --- | --- |
| Marang owns execution truth (sections 1, 4, 27) | Marang owns the authorized product projection. Fuwen owns admitted workflow semantics; Zhinu owns durable workflow execution and activation; Qingniao owns delegated attempts; Hongxian owns session/evidence continuity. Consume their receipts. |
| One sequence with droppable telemetry (10, 37) | Retain a run event order, but add a durable recovery cursor. Missing transient numbers are not missing durable history. Specify the subscription/snapshot handshake and expired-cursor recovery. |
| One current activity (23) | Support concurrent active executions, fan-out/join, retries, and waiting work. A selected/focused activity is UI state, not run truth. |
| Node status includes superseded (12, 15, 21) | Separate execution outcome from membership in the current revision. Completed-and-superseded must remain representable. |
| Revision expressed as individual node/edge changes (20, 35) | Publish a complete revision atomically, or fence a bounded batch with a commit marker. Do not render dangling edges or a half-applied redesign. |
| Journal has no stable identity (26) | Add entry identity, ordering, attempt linkage, typed bounded payloads, pagination, and disclosure policy. Retain decisions/evidence; coalesce disposable progress. |
| Asynchronous dispatcher and durability (27, 40, 47) | Commit authoritative transitions before announcing them. Recover the projection from receipts with idempotent ingestion; a browser or slow sink cannot block execution. An in-memory channel alone cannot guarantee durability. |
| HTTP/SignalR security omitted (29–36) | Apply run/workspace authorization to reads and subscription/rejoin, including journals and artifacts. Existing server middleware is scoped to `/mcp`; it does not secure new routes. |
| Static renderer late in the sequence (50) | Bring a polished end-to-end sample forward, then replace fixtures with real state. Treat loading, empty, error, reconnect, and completed views as core design work. |
| Many proposed packages (49) | Start with modules in the product and adapter boundaries. Extract packages only when real consumers justify them, consistent with ADR 0015. |

## Experience contract

### Landing and run overview

Provide a compact authorized run list with outcome/status, task title, workspace,
start time, elapsed time, and attention indicator. Include search and basic
status filters. A direct run link works without navigating this list.

A fresh installation offers a clearly labeled, locally available sample run
and instructions for starting a real run through MCP. Never fabricate real
activity to make an empty installation look busy. No new workflow editor or
browser submission system is required for V1.

The run header answers four questions immediately: **what is the goal, what is
happening now, does it need me, and what is the result?** Show task title,
workspace, state, elapsed time, active/waiting counts, and connection freshness.
Use honest progress such as “3 completed · 2 running · 1 waiting.” Percentages
require a known denominator; changed revision scope is labeled. Do not invent
ETAs or claim that completed steps imply accepted results.

### Main composition

```text
Runs / Implement authentication                Running   Live · updated now
3 completed · 2 running · 1 waiting             Elapsed 04:32
Now: validating token handling                  Attention: none
┌──────────────────────────────────────────────┬─────────────────────────┐
│ Workflow   [Fit] [Focus active] [History 2]   │ Validate tokens         │
│                                              │ Running · attempt 2     │
│ Plan → Research → Implement → Checkpoint     │ Summary | Journal       │
│                                   ├ Validate │ Evidence | Attempts     │
│                                   └ Review   │                         │
│ Previous branch · superseded · expand        │ Tests: 43 / 45          │
│                                              │ Latest meaningful update│
├──────────────────────────────────────────────┴─────────────────────────┤
│ Milestones: Started · Checkpoint evaluated · Revision 2 applied         │
└────────────────────────────────────────────────────────────────────────┘
```

This is an information hierarchy, not a prescribed visual theme. Establish a
small Marang design system: deliberate typography, spacing, node dimensions,
surface contrast, semantic colors, icons, focus rings, and consistent controls.
Custom node styling must look like a finished Marang product. One excellent
theme is required; a second theme must not delay clarity and accessibility.

### Graph that remains understandable while running

- Make the current path dominant; combine icon, text, edge pattern, and color
  to distinguish planned, running, completed, waiting, failed, cancelled,
  skipped, and superseded work. Distinguish blocked work from human approval
  and automatic checkpoint evaluation.
- Show checkpoints as distinct nodes and fan-out/join as real topology. Show
  all concurrent active activities; never imply one active path when several
  are running. Node cards show a short title, state, actor when useful, elapsed
  time, and one meaningful progress line.
- Fit once on initial load. Preserve viewport, selection, and unaffected node
  positions through updates; run layout only for topology changes. “Focus
  active” is explicit. Optional follow mode pauses when the user pans or
  inspects history; it never steals focus back.
- Keep superseded work discoverable in the graph. Small graphs show it subdued;
  dense graphs may collapse it to a labeled branch with count and expansion.
  Never delete it from history, confuse it with failure, or reduce text to
  unreadable opacity. Use a legend and clear labels on new/superseded branches.
- Support pan, zoom, fit, node search, keyboard selection, and a list view
  equivalent to the graph. On narrow screens switch to the list plus inspector
  rather than shrinking the desktop canvas beyond usefulness.
- Use restrained transitions only for meaningful state changes; avoid constant
  edge animation and whole-graph relayout. Honor reduced motion. Node dragging
  and graph editing are not V1 requirements.

### Inspector, checkpoints, and revisions

The inspector opens with an activity summary: purpose, state, current attempt,
latest meaningful update, evidence, and outcome. Separate Summary, Journal,
Evidence, and Attempts views. Group repeated tool output and progress; provide
filters, bounded expansion, copyable error references, artifact links, and a
clear distinction between a reported claim and validation evidence. Escape
untrusted content and do not present private model reasoning as a transcript.

Preserve inspector selection, journal scroll, and text selection during updates.
If the user scrolls up, display “new updates” rather than forcing scroll to the
bottom. Retry history must make a failed first attempt and successful second
attempt easy to distinguish. Unknown/unsupported content has a useful fallback.

A checkpoint card explains why execution paused, whether evaluation is
automatic or needs a supervisor, what evidence is available, and the outcome.
After redesign, show a plain-language change card: **why it changed, what was
retained, what was replaced, and what happens next**, with revision and
checkpoint links. The milestone strip shows meaningful transitions, not every
token/tool event. It is a navigation aid, not a time-travel slider.

V1 observes authoritative revisions and checkpoint outcomes. Expose an action
only when its existing server capability, authorization, and revision fence
are available. Otherwise state “waiting for supervisor via MCP.” New adaptive
proposal/approval tooling remains the later supervision/V2 track. Never ship
decorative buttons that cannot complete their action or infer activation from
a model's message.

### Completion and difficult states

Make completion a designed outcome view: accepted/failed/cancelled result,
concise outcome summary, validation evidence, produced artifacts, duration,
attempts, and significant revisions. Keep the graph and journal available.
Display usage/cost only when authoritative and label partial or unavailable
values. A successful tool execution alone is not an accepted workflow outcome.

Design loading skeletons, no runs, missing/unauthorized run, partial evidence,
provider unavailable, long wait, failure, cancellation requested vs confirmed,
reconnecting, stale projection, server restart, and expired history. Keep the
last known graph on transport loss, label its age, and explain recovery. Do not
turn a disconnected browser into a failed run or a quiet activity into a hung
activity. “Live” requires healthy transport and a reconciled projection.

## Contracts and delivery guarantees

### Identity and state

Keep `RunId`, `NodeId`, `ActivityId`, `ExecutionId`, `RevisionId`, `EdgeId`, and
`CheckpointId` distinct. Map them explicitly to upstream workflow instance,
generation, delegation, and receipt identities. Preserve stable Fuwen identity
when semantics are retained; changed work gets a new identity plus lineage.
Retries get new execution identities and retain attempt history. Scope all
references to the authorized run and validate their relationships.

Replace singular `CurrentActivityId` with active execution references. Separate
node membership (`Current`/`Superseded`) from execution state. Separate a proposed
or admitted revision from an activated revision; current revision advances only
on an authoritative activation receipt. Events from an old generation remain
historical and cannot overwrite a newer attempt. Cancellation, skipped work,
waiting reasons, terminal outcomes, and revision failure/abandonment need explicit
contracts. Initial queued/unplanned runs may have no revision and an empty graph.

The current graph includes current topology and retained superseded branches,
with membership metadata. It is not historical reconstruction at arbitrary
timestamps. Snapshots expose schema version, projection watermark, source
receipt/generation references, freshness, capability flags, and truncation.
Define supported graph sizes and explicit bounded expansion; never silently
return a partial graph as complete.

### Semantic reporting and durable projection

Keep `IRunProgress` as the proposal's semantic reporting seam, including
cancellation, waiting, journal, checkpoint evaluation, and revision failure
events. Transport adapters consume it; reusable Penghou cores must not reference
Marang, HTTP, SignalR, or frontend DTOs. Map committed upstream receipts into the
Marang view; progress callbacks are not an alternative command/scheduling path.

Use a versioned envelope with stable `EventId`, run-scoped `Sequence`, optional
`DurableSequence`, kind, durability, occurrence/observation time, identity and
generation references, source receipt/correlation, and typed bounded payload.
Serialize 64-bit cursors as decimal strings in browser-facing JSON. Wall-clock
time is display data, never ordering authority. Define unknown-event handling
and a resnapshot/upgrade path for incompatible versions.

Durable ingestion is idempotent on source receipt/event identity. Allocate the
durable cursor and atomically persist the local event plus projection watermark;
restart cannot reuse committed identities or sequence values. A projector crash
after the authoritative transition is repaired by reading durable source
receipts/outbox records, not by repeating external work. Expose projection lag
or failure honestly. SQLite is a sufficient initial product projection store;
it does not become a replacement Zhinu workflow database or Hongxian ledger.

Publish live deltas only after durable projection commit. Transient updates may
be throttled/coalesced per execution and dropped through bounded queues; they
carry a durable base cursor and cannot regress terminal state. Isolate slow
SignalR, MCP, and telemetry sinks. Durable storage failure is explicit and
recoverable; “never drop” cannot be implemented by an unbounded memory queue.
Observers never hold execution open. Retain structured, redacted errors rather
than serialized exceptions. OpenTelemetry metrics/traces correlate to the same
run/attempt without becoming workflow truth.

### Race-free snapshot and live recovery

The proposal's single numeric gap test is insufficient: transient events can be
intentionally absent, and a run may finish between GET and subscription with
no later event to reveal the gap. V1 uses a separate contiguous durable cursor:

1. Register handlers, connect, authorize and subscribe to the run, then buffer
   bounded live deltas. Subscription acknowledgment establishes a barrier and
   includes the latest durable watermark; it cannot leave an unobserved window.
2. Fetch an atomic HTTP snapshot at durable watermark D. Install it, discard
   buffered durable duplicates at or below D, and catch up retained durable
   events after D through a server-advertised high watermark. Apply in durable
   order, deduplicating events. Queue limits or incompatible state trigger a
   fresh snapshot instead of speculative application.
3. Apply subsequent durable deltas in cursor order. A missing durable cursor
   triggers bounded recovery. Missing transient sequence numbers do not.
   Transient updates are best effort, attempt-fenced and accepted only once
   their durable base is installed; they are never required to recover truth.
4. On reconnect, reauthorize and rejoin, repeat the handshake, and recover even
   if the run finished while offline. On initial connection failure, retry with
   bounded backoff and an explicit offline state. Periodic server watermarks
   reveal a missed tail event even if no more workflow events occur.
5. Event pages expose `nextCursor`, `throughDurableSequence`, retention floor,
   and `hasMore`; `after` means durable cursor, not mixed event sequence.
   Expired history yields explicit `resyncRequired` and a snapshot path, not an
   empty-success response. Show history limits in the UI.

SignalR's client reconnect support is opt-in and does not itself implement this
application recovery protocol; configure and test it explicitly. See the
[official client documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-10.0).

### HTTP, journals, authentication, and correlation

Use `/api/marang/runs` for an authorized paginated list and `/{runId}` for the
snapshot. Retain the proposed `/workflow`, `/revisions`, `/revisions/{revisionId}`,
`/events?after=...`, `/activities/{activityId}`, and
`/activities/{activityId}/journal` reads; use `/hubs/marang/runs` for live deltas.
Every secondary read carries a watermark/revision so mixed-version responses
can be detected. Cap response bytes, page sizes, payload sizes, subscription
counts, and history retention. Reuse authorized artifact retrieval.

Journal entries have `EntryId`, run/activity/execution identity, stable order,
timestamp, source, kind, disclosure classification, bounded summary, and optional
artifact/evidence reference. Make evidence, warnings, errors, and decisions
durable; roll up disposable progress. Support cursor pagination and live dedupe.
Surface redaction, truncation, and retention rather than implying completeness.

Extend authentication beyond the current `/mcp` middleware. Browser requests,
hub joins/rejoins, history, and artifact reads require the same caller and
workspace scope. A guessed run ID or SignalR group name grants no access.
Handle session expiry/revocation without retaining an authorized subscription.

For the first same-origin UI, prefer an explicit authenticated browser session
using secure HttpOnly cookies and a reviewed sign-in/session path; the existing
API-key middleware does not provide that flow. Protect state-changing endpoints
against CSRF and validate origins for hub access. Do not embed the service API
key in frontend bundles, local storage, or URLs. Browser WebSocket/SSE bearer
authentication has header limitations, so HTTP header-only key authentication
cannot simply be copied to those transports. See
[SignalR authentication](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0).

Correlate MCP request ID, progress token (preserving its protocol type), and task
ID when present with the same run and actor scope. Not every client supplies all
three. HTTP and browser observation must work independently of MCP subscribers.
Cancellation or loss of a progress subscriber does not cancel the run.

## Frontend implementation direction

React and TypeScript with React Flow, ELK layout, and custom SVG are a reasonable
starting point from the proposal. Validate them with representative topology,
keyboard navigation, and performance before fixing versions. React Flow provides
[accessibility hooks](https://reactflow.dev/learn/advanced-use/accessibility)
and describes [external layout integration](https://reactflow.dev/learn/layouting/layouting);
product semantics, focus behavior, and stable layout remain Marang work. Motion
is optional; basic CSS transitions may suffice for V1. Do not add dependencies
solely for decorative animation.

Develop the renderer with deterministic snapshot/event fixtures and use the
same reducer for fixtures and real transport. Virtualize long journals, avoid
layout work on progress-only events, and batch rendering without losing durable
transitions. A demo mode is an explicit adapter, never a hidden fallback when
the server fails.

## Release evidence and quality gates

These are proposed V1 acceptance budgets, not measured capabilities. Record the
browser, machine, build, and network profile with results before release.

| Gate | Required evidence |
| --- | --- |
| First impression | At least 4 of 5 people unfamiliar with Marang identify the goal, current work, and attention state within 10 seconds without coaching. |
| Comprehension | At least 4 of 5 locate an artifact, explain why a revision happened, and distinguish a failed attempt from the final outcome within 60 seconds per task. |
| Visual finish | Review captured loading, running, parallel, waiting, replanning, failed, cancelled, offline, empty, and completed screens at 1440×900, 1024×768, and 390×844; no clipping, unreadable labels, or focus loss. |
| Accessibility | Complete inspect/checkpoint/artifact flows by keyboard and screen reader; verify visible focus, non-color state cues, text contrast, 200% zoom/reflow, and reduced motion. |
| Responsiveness | On the recorded reference laptop/browser over a local service, 100-node/150-edge fixture: p95 first usable view within 2 seconds, selection within 100 ms, and committed state visible within 1 second. |
| Bounded load | Exercise 500 nodes, 10,000 paginated journal entries, and 50 transient updates/second for 10 minutes; demonstrate bounded queues/memory, retained selection, responsive inspection, and explicit grouping/limits. Record any revised budgets before release. |
| Real vertical slice | One real run includes parallel validation/review, retry, checkpoint evaluation, an upstream-authorized revision with a retained superseded branch, evidence/artifacts, and terminal outcome. No fixture-only feature passes this gate. |
| Recovery | Prove duplicate/out-of-order delivery, dropped telemetry, disconnect during revision, terminal event during connect, offline completion, service restart, cursor expiry, and slow-client recovery. Execution continues without observers. |
| Isolation | Unauthorized snapshot/journal/artifact reads and joins fail; expired sessions stop receiving data; malicious journal markup is inert and secrets remain redacted. |

The minimum convincing product demonstration follows one coherent task from
plan through parallel work, a visible checkpoint, a comprehensible revision,
recovery from a browser disconnect, and a useful outcome. Include a separate
failure/waiting scenario. Release acceptance combines usability review, browser
interaction checks, contract/recovery tests, and real provider evidence.

## Explicitly beyond V1

Time travel, arbitrary historical graph reconstruction, full replay, animated
graph morphing, experiments/A-B comparison, effectiveness scoring, route
optimization, long-term analytics, comparative execution analysis, distributed
SignalR scale-out, a workflow editor, and new autonomous activation policy.
V1 makes authoritative adaptive execution visible; it does not move the V2
planning, experimentation, or authority model into Marang.
