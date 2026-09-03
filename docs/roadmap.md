# Marang roadmap

## Goal

Prove that an MCP-capable supervisor can delegate one bounded repository change
to an existing coding-agent runtime, while Marang durably owns the outcome and
returns a tested, independently reviewed candidate with concise evidence.

Progress is marked here so development can resume without relying on chat
history.

## Milestone 0 — Project and boundary scaffold

Status: **complete**

- [x] Create multi-target .NET solution and package boundaries.
- [x] Add initial provider-neutral delegation contracts.
- [x] Add request validation and contract tests.
- [x] Add CI build, format, test, and pack checks.
- [x] Document architecture, security posture, non-goals, and design review.
- [x] Correct Hongxian's role to temporal session/evidence rather than worker
      execution.
- [x] Accept agent runtimes as opaque execution providers rather than rebuilding
      their native delegation and repository loops.
- [x] Verify that the official Codex SDK and non-interactive surfaces can support
      an initial Codex execution adapter.

## Milestone 1 — Contract and identity freeze

Status: **next**

- [ ] Define canonical request normalization and fingerprinting.
- [ ] Specify `RequestKey` scope and conflict behavior.
- [ ] Make collection ownership immutable at the public boundary.
- [ ] Define state-transition invariants and terminal result availability.
- [ ] Define artifact envelopes, schema versions, content identity, and
      candidate revision references.
- [ ] Define budget consumption receipts and `BudgetExceeded` semantics.
- [ ] Define worker invocation and review-independence evidence.
- [ ] Define capability descriptors without coupling routing to a closed
      provider/model enum.
- [ ] Define the durable external-operation protocol: idempotent start, handle
      capture, observation, cancellation, result retrieval, and resume.
- [ ] Separate Marang workflow budgets from provider-specific budget hints and
      telemetry.
- [ ] Define normalized evidence shared by agent, model, and deterministic
      execution without forcing them into one lowest-common-denominator API.
- [ ] Add public API baselines after the contracts pass review.

Exit: duplicate submissions cannot create duplicate cost, and every public
identity and terminal outcome has unambiguous semantics.

## Milestone 2 — In-memory delegation control plane

- [ ] Define provider registry, capability selection, and policy decision
      contracts.
- [ ] Implement the fixed `Implement` state machine with fake agent, model, and
      deterministic providers.
- [ ] Simulate ambiguous provider acceptance and reconnect through its external
      handle.
- [ ] Seal a candidate revision before parallel Test and Review.
- [ ] Evaluate deterministic test results separately from reviewer judgment.
- [ ] Support one bounded fix cycle.
- [ ] Aggregate a concise `DelegationResult` with artifact references.
- [ ] Cover success, rejection, cancellation, budget exhaustion, worker error,
      and `NeedsSupervisor` paths.

Exit: the complete policy can be tested without MCP, real models, shells, or a
workflow database.

## Milestone 3 — Codex execution capability spike

- [ ] Implement an approved-workspace resolver and disposable candidate
      provider for a sample repository.
- [ ] Implement a process-isolated adapter around `codex exec --json` with an
      output schema, explicit sandbox, bounded output, and early thread-ID
      capture.
- [ ] Prove that a known Codex thread can be observed or resumed without
      starting duplicate work.
- [ ] Prove bounded implement-code execution without exposing Marang MCP or
      credentials to the subordinate task.
- [ ] Record resolved model/tool/usage provenance.
- [ ] Compare the CLI spike with an SDK/app-server bridge for cancellation,
      progress, approvals, and long-lived support.
- [ ] Verify local authentication and subscription/API-key behavior without
      claiming unavailable cost precision.

Exit: one economical Codex agent can reliably change a tiny disposable project,
return structured evidence, reconnect after interruption, and remain inside
host policy. Stop and revisit the product if this seam is not viable.

## Milestone 4 — Durable Zhinu execution

- [ ] Inspect and pin the minimum supported Zhinu API/version.
- [ ] Map the fixed state machine to durable workflow steps.
- [ ] Persist delegation-to-workflow identity and idempotent acceptance.
- [ ] Persist external provider handles before awaiting their terminal results.
- [ ] Map Zhinu cancellation without pretending it rolls back effects.
- [ ] Restore status and result after process restart.
- [ ] Test crash windows around acceptance, artifact publication, and result
      completion.

Exit: a delegation survives host restart and returns exactly one result.

## Milestone 5 — Evidence, validation, and bounded repair

- [ ] Publish typed inspection, plan, implementation, test, review, and result
      artifacts.
- [ ] Run Test and Review against the same immutable candidate revision.
- [ ] Use a deterministic executor for tests and diff inspection; agent claims
      never override its receipts.
- [ ] Record the review-independence dimensions.
- [ ] Implement evaluator policy and one repair revision.
- [ ] Return unresolved findings rather than suppressing them.
- [ ] Add optional Hongxian session correlation for decisions, incidents, and
      unusual-but-recovered events.

Exit: the supervisor can judge the result without raw worker transcripts.

## Milestone 6 — MCP vertical slice

- [ ] Expose `marang_delegate`, `marang_status`, `marang_result`, and
      `marang_cancel`.
- [ ] Keep transport DTOs separate from domain contracts.
- [ ] Define host authentication and workspace authorization.
- [ ] Add compact status revision/cursor behavior.
- [ ] Bound MCP response and artifact payload sizes.
- [ ] Add transport contract and ambiguous-response retry tests.
- [ ] Ensure subordinate agent environments cannot recursively call Marang.

Exit: Codex can safely submit, observe, cancel, and retrieve a delegation.

## Milestone 7 — Dogfood and hardening

- [ ] Delegate one small Marang improvement to Marang.
- [ ] Measure supervisor context avoided, worker calls, retries, duration, and
      model usage.
- [ ] Add adversarial repository prompt-injection and malicious-path tests.
- [ ] Add restart, cancellation, stale-revision, and partial-artifact tests.
- [ ] Review package boundaries and graduate only packages with a stable use.

## Later, evidence-driven integrations

- Hetu-focused inspection context.
- Cangjie memory and context snapshot references.
- Fuwen representation of stable predefined strategies, without exposing
  arbitrary caller-authored graphs.
- Resumable supervisor decisions if terminal `NeedsSupervisor` proves too
  limiting.
- More predefined strategies: Investigate, Review, and Fix.
- Additional bounded-execution providers.
- Deeper visibility into opaque provider subagent trees only when cancellation,
  cost, traceability, or policy demonstrates a need.

## Non-goals

- Replacing Codex or becoming a general autonomous coding agent.
- A workflow DSL or caller-supplied arbitrary workflow graph.
- Unbounded self-reflection, retries, fan-out, or recursive delegation.
- Recreating native Codex/Claude Code/OpenCode subagent scheduling, repository
  exploration, or prompt iteration.
- Owning workflow durability, model-provider clients, memory, code graphs,
  session history, or JSON repair.
- Letting model output grant itself tools, filesystem scope, budgets, or model
  escalation.
- Direct commits, pushes, publication, credential access, or primary-workspace
  mutation by default.
- Returning complete worker transcripts in normal MCP results.
