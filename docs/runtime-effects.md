# Workflow runtime-effect inventory (AE-6.1 input)

Status: **proposed**. Covers both runtime surfaces per scope decision.
Inventory only: no engine, Hufu, or Zhinu contract changes yet.
Cross-repo paths are owned by their repositories; this
file is the AE evidence trail, not a design claim on them.

Rule: an effect is protected when it touches a governed resource or exposes
protected information — regardless of which engine performs it. Pure engine
transitions and replay-of-completed-results are explicitly distinguished so
AE-6 never requires fake resource rights for bookkeeping (§18, §25).

## Surface A — Zhinu step engine (`Penghou.Zhinu`)

The engine already evaluates a pre-effect gate per step attempt
(`WorkflowContext.cs:1272`, `WorkflowAuthorizationGate`): fresh evaluation,
provider-response validation, evidence verifier, fenced commit, dispatch-fence
re-validation, expiry re-checks. The authorizer behind it is host-supplied
policy — **no production Hufu-backed `IExecutionAuthorizer` exists** (test
doubles only). That missing backing is the AE-6 gap on this surface, not the
framework.

| Engine path | Classification | Authority today |
|---|---|---|
| Forward step execution (step delegate after claim) | protected effect dispatch (implementation-defined) | gated by host policy; NOT Hufu current authority |
| Compensation execution (`CompensatingWorkflowStep.CompensateAsync`) | protected effect (undoes external effects) | **UNGATED** — declaration drift-check only; no per-attempt evaluation |
| Claim acquire/release, lease renew, wait park, cursor moves | pure engine transition | none needed |
| Durable writes (step results, events, external-op handles, auth commits) | engine bookkeeping (§18) | fenced by leases/generations, not Hufu |
| Reconstructed reads / replay of recorded completions | replay, no new effect (§9A) | none needed |
| Unknown-outcome recovery | reconciliation (§9C) | must not blind-redispatch; verify in AE-6.2 |

`IExecutionAuthorizer` and `ExecutionRequirement(Capability, Resource,
ScopeReference)` come from pinned external `Penghou.Workflow.Abstractions`
preview.2 (immutable). Requirements are host/workflow-definition supplied, so
the Hufu adapter must treat capability strings as untrusted labels mapped
through trusted host mapping — the same rule as AE-5B §4.

## Surface B — Qingniao provider dispatch (`Penghou.Qingniao`)

Marang delegations execute here, not in Zhinu steps (Zhinu holds projections).
The dispatch point is `Adapter.StartAsync` (`InMemoryDelegationCoordinator`,
explicitly `CallProviderOutsideGateAsync`). Admission/derivation preflight is
host-owned and schedule-time only.

| Coordinator path | Classification | Authority today |
|---|---|---|
| `Adapter.StartAsync` (provider spawn, e.g. Codex) | protected external invocation | **UNGATED** for current authority |
| `ResumeAsync` (re-dispatch after crash/wait) | protected external invocation | **UNGATED** — retry-laundering risk (probe H). Attach-only resumes are authorized under the same meaning (fail-closed direction: strictly stronger than needed, never weaker) |
| `ObserveAsync` (state, revision, provider status ≤512 chars, failure class, result-available flag — **no content**) | protected lifecycle read | **UNGATED** → maps to `ReadMetadata` (status/lifecycle polling only) |
| `GetResultAsync` (summary text, artifact descriptors, provenance — **execution content**) | protected output read | **UNGATED** → new action `ReadExecutionResult`. Never hidden under `ReadMetadata` |
| Provider `CancelAsync` (abort a live external operation) | protected mutation | **UNGATED** → new action `CancelExecution`. Not `Release` (a completion-gate, not an abort) |
| Phase moves, budget enforcement, terminal publish, pump bookkeeping | pure transition / bookkeeping | none needed |

## Durable authority binding (§4) — the structural gap

Neither runtime durably retains *whose* current authority to evaluate:

- Zhinu `WorkflowRun` carries host binding (`AuthorizationProviderId/BindingId`)
  but **no tenant/subject/delegation/workspace**. Adding trusted run identity
  is a Zhinu contract change (new fields → Zhinu package publish, AE-6.2).
- Qingniao `RuntimeState` holds `DelegationId`/`Request`/correlation in
  memory; what survives restart, and whether the binding resolves from trusted
  Marang durable stores (descriptors/visibility) rather than arguments, is
  verified in AE-6.2. No raw HTTP credentials may be stashed in workflow
  arguments to bridge this.

The binding answers "whose authority do I evaluate" — never "is this
permitted". It must be created from trusted host context at admission,
immutable thereafter except through explicit authority-aware migration.

## Hufu decision kind (§5) — proposal, not yet implemented

`HufuExecutionAuthorizer` (`Penghou.Hufu.Workflow`, already published and
qualified) already implements the required semantics: fresh per-requirement
`AuthorityRequest` against current authority, trusted binding resolution with
exact execution/revision/fresh-request binding (anti-replay), approval
coordination, evidence-before-result, deadline caps, fail-closed on every
failure. **No new Hufu operation type is proposed.**

Remaining Hufu-side judgments for AE-6.1b (with separation tests):

- Capability→action mapping under the AE-5B identity rule. Exact reuses go in
  as-is (e.g. a genuine runtime file read → `ReadFile`); anything requiring
  analogy gets an appended action. Two mappings need explicit confirmation:
  provider-spawn (≈ `ExecuteProcess`?) and provider poll-reads (≈
  `ReadMetadata`?). If either is not exact, append.
- Accept / Marang-permit / Derive non-substitution for runtime effects, proven
  at the Hufu level (distinct types already; behavioral tests to add).
- Publish a new Hufu prerelease **only if** contracts change (new actions or
  adapter surface); the existing `HufuExecutionAuthorizer` needs no publish
  by itself.

## Out of scope for AE-6 (unchanged)

Gagamba enforcement (next workstream), A2A, Marang command-plane changes,
authorization of every internal persistence write, general policy generation.
