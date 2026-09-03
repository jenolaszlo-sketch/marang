# Marang dependency release plan

## Purpose

This is the cross-project execution order agreed before serious Marang
implementation. It is intentionally narrower than “finish every roadmap.” Each
primitive closes only the release gates Marang needs; later features remain in
their own roadmaps.

## Release order

```text
Zhinu preview.12 ───────────────────────┐
                                        v
Siming preview.3 -> Hongxian preview.2 ─┘
                                        |
                                        v
Marang contracts and in-memory control plane
        |
        v
Codex execution-provider spike
        |
        v
Marang + Zhinu durability
        |
        v
optional Marang + Hongxian evidence
        |
        v
MCP dogfood
```

Engineering work on Zhinu, Siming, and Hongxian may run in parallel, but
packages are reviewed and released in dependency order.

## Gate 1 — Zhinu runtime release

Status: **implemented, reviewed, and pushed; awaiting NuGet publication/indexing**

Release candidate: **0.1.0-preview.12**

- Complete the OS-level process-loss test immediately before and after the
  durable loop state-commit boundary.
- Add provider-conformance coverage for composed durable loop behavior.
- Audit the surfaces Marang will consume: restart, cancellation, signals,
  artifacts, fencing, stable operation identity, and replay-safe external
  effects.
- Fix only concrete release blockers discovered by that audit.
- The implementation, review, verification, and release version are confirmed.
- NuGet publication/indexing remains the release action; do not advance the
  dependency gate until the package is available.

This gate does not include Zhinu's declarative workflow, activity catalogue,
policy compiler, AI activity, or Fuwen-adapter roadmap.

## Gate 2 — Siming SQLite preview-3 prerequisite

Status: **implemented, verified, and pushed; awaiting NuGet
publication/indexing**

Release candidate: **0.1.0-preview.3**

Hongxian preview-2 requires Siming SQLite's atomic `ExpectedHead` APIs. The
release is verified (51 tests and pack passed) and pushed at commit
**b40ec1c**; it is not yet published or indexed.

- Publish `Penghou.Siming.Sqlite` **0.1.0-preview.3**.
- Do not advance the Hongxian dependency gate until the package is available
  on NuGet.

## Gate 3 — Hongxian preview-2 readiness

Status: **implemented, reviewed, and pushed; waiting for Siming preview.3 and
NuGet publication/indexing**

Release candidate: **0.1.0-preview.2**

- Add in-repository, interface-driven provider-conformance reference coverage
  for the stable event, projection, catalog, lease, operation, and inspection
  contracts.
- Add public API baselines and package validation.
- Defer .NET 8 unless UUIDv7 support is simple, compatible, and justified by a
  real consumer.
- The implementation, review, verification, and release version are confirmed.
- NuGet publication/indexing remains the release action; do not advance the
  dependency gate until the package is available.

This gate does not include richer query APIs, collaboration publications,
branching, archives, encryption, or second-consumer features. Hongxian is not a
prerequisite for Marang execution; it becomes an optional evidence integration
after Marang's vocabulary is proven.

## Gate 4 — Marang control plane

Status: **queued — waiting for Zhinu preview.12, Siming preview.3, and
Hongxian preview.2 to be published and indexed**

- Freeze delegation identity, canonical request fingerprints, immutable public
  inputs, lifecycle invariants, budgets, artifacts, and normalized evidence.
- Define capability routing and a durable external-provider protocol with
  idempotent start, handle capture, observe, result, cancellation, and resume.
- Implement and exhaustively test the fixed workflow with fake agent, model, and
  deterministic providers.

## Gate 5 — Execution feasibility

Status: **queued**

- Prove a bounded Codex process provider in an isolated candidate workspace.
- Capture its thread identity early and reconnect without duplicate execution.
- Verify results with deterministic tests and an independent reviewer.
- Prefer A2A for later interoperable agent providers without making it part of
  the first vertical slice.

Failure at this gate triggers a product-design review before further workflow
infrastructure is added.

## Gate 6 — Durable integration and dogfood

Status: **queued**

- Map the proven Marang control plane onto the released Zhinu package.
- Add restart and ambiguous-external-operation tests.
- Add Hongxian only for useful session, participant, incident, decision,
  recovery, and unusual-event evidence.
- Expose the bounded MCP surface and delegate one small Marang improvement to
  Marang itself.

## Release discipline

For each gate:

1. implement and update the owning roadmap;
2. perform a separate design/security/API review;
3. run the complete relevant verification suite and pack checks;
4. confirm version and dependency metadata;
5. commit and push;
6. publish before advancing a downstream integration.
