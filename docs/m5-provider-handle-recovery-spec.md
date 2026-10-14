# M5 durable provider-handle recovery spec (design decision, no implementation)

M1 update (2026-10-14): [ADR 0016](decisions/0016-aaaw-m1-branch-revision-admission.md) and the
[bounded recovery contract](aaaw-m1/delegation-recovery.md) now resolve and implement the minimum
read/report recovery boundary. The original M5 completed-case proof and broader alternatives below
remain historical; full provider/runtime recovery is not claimed.

Status: **decision record, not a work order.** The completed-case V1.5 proof
is closed. This spec frames the one remaining architectural decision — how an
in-flight provider operation survives process loss — for the
delegation/restart milestone owner. No production code follows from this
document until the ownership decision in §2.1 is explicit.

## Background: what is proven and what is impossible today

Proven (completed pre-crash): real Codex execution to terminal, one spawn,
durable correlation, durable Zhinu result, durable Hongxian evidence, full
teardown, identical projection + watermark across two reopens, zero respawn.

Impossible by construction today: provider operation handles live only in
`InMemoryExternalOperationProviderCatalog` and runtime memory, and
`DelegationRuntime` takes no stores. A process lost mid-execution orphans the
external work with no durable record of the handle, so no resume path can
distinguish observe/reconnect from respawn. `PostCancelAsync` already encodes
this truth ("no live execution handle (notably after a restart)" → 409).

## 1. Ownership of the durable handle record (decision required)

Evaluate by lifecycle semantics — who creates, observes, and retires the
handle — not by table convenience. Candidates stay open:

- **(a) Zhinu `WorkflowExternalOperation`.** Attractive on paper: operation
  identity, step/attempt association, idempotency key, provider/external IDs,
  ownership/lease, and recovery intent already exist, and the store calls
  itself crash-safe. Lifecycle fit is partial: those rows describe handles
  *linked to workflow runs*, while a delegation-spawned operation belongs to
  a delegation lifecycle Marang owns. Using it would also make the first
  Marang writer of a Zhinu table no Marang code touches today.
- **(b) Marang-owned persistence** (e.g. alongside the delegation-authority
  SQLite stores). Lifecycle fit: the handle is host/delegation state — the
  provider endpoint, thread/operation identity, and observation cursor are
  created by host composition, observed by host resume, and retired when the
  terminal outcome is consumed. Delegation acceptance/visibility already live
  in that ownership domain.
- **(c) Qingniao-owned durability.** Conceptually cleanest if
  provider-operation lifecycle is fundamentally delegation-runtime state: the
  runtime would journal operations itself. But the runtime deliberately takes
  no stores today, so this is the largest architectural change (new store
  contracts in Qingniao packages) and must be justified, not assumed.

## 2. Binding key between delegation/attempt and provider operation (decision required)

The substrate already carries most of the identity: `ExternalOperationRequest`
has an `Identity`; `ExternalOperationCorrelation` carries `(DelegationId,
WorkflowRun, StructuralNode, NodeGeneration, ExecutionAttemptId)`; delegation
IDs are deterministic from `(tenant, subject, requestKey)` with exact-retry
convergence and conflicting-retry fail-closed. The decision is which tuple is
the durable binding key (e.g. delegation + generation + request identity vs.
provider-issued external ID), and where that key is recorded atomically with
the spawn decision.

## 3. Resume semantics: observe/reconnect vs respawn (decision required)

- **Observe/reconnect:** if the external process may still be alive (or its
  result retrievable), reattach via the persisted handle and observe to
  terminal. Requires handle durability plus a provider-supported
  re-observation path plus an explicit orphan/staleness policy (when a handle
  is declared dead).
- **Idempotent respawn:** if the operation cannot be observed, start anew
  only when no durable record claims the attempt. Requires a check-before-spawn
  against the durable record in the same decision path as `Start`, so a
  resumed driver converges instead of duplicating.
- A bounded observe-first-then-respawn policy is allowed, but the timeout,
  the dead-handle declaration, and the re-entry gate must be explicit, not
  emergent.

## 4. Authority semantics on recovery/re-entry (decision required)

- Existing Hufu preflight covers initial execution; any path that spawns or
  resumes provider execution post-restart must pass through the same
  preflight — no restart bypass path.
- Pure observation (no new external effects) needs no new authority but must
  never be confused with authorized execution.
- Revocation/expiry must propagate: a revoked or expired binding blocks
  resume, using the existing terminal-revocation substrate.

## 5. Explicit non-goals for this decision

- No Marang projection redesign (snapshot/rebuild stands).
- No Zhinu changes presupposed; no generation/operation stream events.
- No Hongxian contract changes; session evidence stays generic.
- No SignalR; no historical step-revision reads.
- No new provider abstractions; the Codex adapter and scripted-factory
  determinism are sufficient test infrastructure.

## 6. Suggested acceptance shape (when the decision lands)

Mirror the completed-case proof: scripted provider spawns during phase 1,
process dies mid-execution, phase 2 resumes without a second spawn, terminal
outcome is recorded, projection + watermark are stable, correlation and
session evidence verify. The assertion that distinguishes this proof from the
closed one: **phase-2 factory spawn count is zero AND the terminal outcome is
present AND the handle record shows observed-not-respawned** (not merely
"nothing re-ran").
