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
Marang contracts and in-memory supervision control plane
        |
        v
Codex execution-provider spike
        |
        v
Marang + Zhinu durability
        |
        v
Marang + Zhinu + Hongxian durable supervision/session correlation
        |
        v
MCP dogfood
```

Engineering work on Zhinu, Siming, and Hongxian may run in parallel, but
packages are reviewed and released in dependency order.

## Gate 1 — Zhinu runtime release

Status: **complete — implemented, reviewed, pushed, published, and indexed on NuGet**

Release candidate: **0.1.0-preview.12**

- Complete the OS-level process-loss test immediately before and after the
  durable loop state-commit boundary.
- Add provider-conformance coverage for composed durable loop behavior.
- Audit the surfaces Marang will consume: restart, cancellation, signals,
  artifacts, fencing, stable operation identity, and replay-safe external
  effects.
- Fix only concrete release blockers discovered by that audit.
- The implementation, review, verification, and release version are confirmed.
- NuGet flat-container confirms `Penghou.Zhinu` **0.1.0-preview.12** is
  published, downloadable, and indexed for exact-version restore. Gallery
  search and symbols may lag and are non-blocking.

This gate does not include Zhinu's declarative workflow, activity catalogue,
policy compiler, AI activity, or Fuwen-adapter roadmap.

## Gate 2 — Siming SQLite preview-3 prerequisite

Status: **complete — implemented, verified, pushed, published, and indexed on NuGet**

Release candidate: **0.1.0-preview.3**

Hongxian preview-2 requires Siming SQLite's atomic `ExpectedHead` APIs. The
release is verified (51 tests and pack passed), pushed at commit **b40ec1c**,
and is now published and indexed on NuGet.

- `Penghou.Siming.Sqlite` **0.1.0-preview.3** is available on NuGet.

## Gate 3 — Hongxian preview-2 readiness

Status: **complete — implemented, reviewed, pushed, verified, published, and
indexed on NuGet**

Release candidate: **0.1.0-preview.2**

- Add in-repository, interface-driven provider-conformance reference coverage
  for the stable event, projection, catalog, lease, operation, and inspection
  contracts.
- Add public API baselines and package validation.
- Defer .NET 8 unless UUIDv7 support is simple, compatible, and justified by a
  real consumer.
- The implementation, review, verification, and release version are confirmed.
- The clean public restore/build/test/example/pack/isolated-consumer CI run
  (**33756201901**) passed against Siming preview.3.
- NuGet flat-container confirms `Penghou.Hongxian` **0.1.0-preview.2** is
  published, downloadable, and indexed for exact-version restore. Gallery
  search and symbols may lag and are non-blocking.

This gate does not include richer query APIs, collaboration publications,
branching, archives, encryption, or second-consumer features. Hongxian is not
an executor, but session continuity/correlation is required for the real
durable supervisory slice; pure Marang tests may use fakes.

## Gate 4 — Marang control plane

Status: **contract freeze complete — Siming preview.4 consumption remains; in-memory slice next**

Marang CI is green, and the required dependency versions are available for
exact-version restore. This gate covers the control-plane implementation; it
does not require gallery search or symbol indexing to complete.

Marang Batches 1–7 are implemented and the public contract is frozen. Consume
Siming preview.4 for the remaining Batch 5B canonical-fingerprint adapter after
that exact package is published, then implement the in-memory supervision slice.

The Gate 0.5 capability audit is the only reason to open an upstream package
gate: if an audited semantic is generally reusable, implement, test, and
release it in the owning primitive first. Marang pauses only when that released
capability is a required blocker; otherwise it records the result and uses a
small adapter. Do not create premature package work to compensate for an
unaudited or merely inconvenient API.

The current audit leaves explicit future upstream gates:

- **Siming canonical JSON v2** is implemented and verified upstream as
  `penghou-canonical-json-v2`, with Siming preview.4 prepared for publication.
  Marang Batch 5B waits for that exact package instead of copying or
  reinterpreting the canonicalization and logical SHA-256 identity contract.

- **Fuwen P0** blocks accepting supervisor-authored or advanced Fuwen plans
  until deep immutability, authoritative reference/binding/type/acyclicity/
  resource validation, revision lineage, supervisor/checkpoint external-input
  nodes, and typed context requirements are released and audited. Marang's
  provider-neutral lifecycle work may proceed before that gate.
- **Zhinu P0** blocks durable Milestone 4 until signal-consumption fencing,
  stale artifact-publication fencing, and generic external-operation handle
  persistence are fixed, released, and safely consumable. Do not add a Marang
  workaround for these durable execution semantics.
- **Hetu/Cangjie audit:** current snapshot, repository, and index-publication
  identities are usable through Marang's reference-only adapter. Their P1
  upstream gaps remain in the owning roadmaps and block richer impact-driven
  re-entry integration only; they do not block the in-memory slice or this
  context contract.
- **Baize audit:** bounded in-memory model execution and provenance are
  reusable. Two Baize P0 tool-integrity gaps block authoritative complex-tool
  integration; durable ordinary completion also blocks until provider-native
  external-operation handles are available. Do not add Marang workarounds for
  those provider semantics.

After Batch 7, implement and exhaustively test both the `marang_delegate`
preset and the advanced Fuwen workflow seam with fake agent, model, context,
and deterministic providers.

## Gate 5 — Execution feasibility

Status: **queued**

- Prove a bounded Codex process provider in an isolated candidate workspace.
- Capture its thread identity early and reconnect without duplicate execution.
- Verify results with deterministic tests and an independent reviewer.
- Prefer A2A for later interoperable agent providers without making it part of
  the initial implementation slice.

Failure at this gate triggers a product-design review before further workflow
infrastructure is added.

## Gate 6 — Durable integration and dogfood

Status: **queued**

- Map the proven Marang supervision control plane onto the released Zhinu
  package, including waiting, wake, intervention, and generation recovery.
- Add restart and ambiguous-external-operation tests.
- Integrate Hongxian as the session/correlation authority for the durable slice,
  including participant, incident, decision, recovery, and unusual-event
  evidence; keep Zhinu authoritative for execution state and reconcile through
  idempotent saga/forward-reconciliation steps. Each outbox is atomic only with
  its owning store; no outbox spans the Zhinu and Hongxian SQLite databases.
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
