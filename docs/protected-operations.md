# Marang protected-operation inventory (AE-5B.2 input)

Status: **proposed** (drives the AE-5B.1 action-vocabulary decision).

Rule applied per operation: reuse an existing Hufu `AuthorityAction` only
when its semantics are **genuinely identical** to the Marang operation, not
merely similar. Analogy is not a mapping. Where no existing verb is identical,
the operation takes an explicit Marang-scoped action (AE-5B.1 appends it; a new
Hufu prerelease is published before Marang consumes it).

## Classification

| Operation | Surface | Class | Hufu action | Resource identity |
|---|---|---|---|---|
| `marang_ping` | MCP | public introspection | n/a | n/a |
| `GET /healthz`, `GET /readyz` | HTTP | public introspection | n/a | n/a |
| `marang_delegate` | MCP | acceptance operation (AE-5A, already gated) | — (Accept, not a resource action) | — |
| `marang_status` | MCP | protected read | `MarangReadDelegation` (new) | delegation |
| `marang_result` | MCP | protected read | `MarangReadResult` (new) | delegation result |
| `marang_wait` | MCP | protected read (polls status) | `MarangReadDelegation` (new) | delegation |
| `marang_inspect` | MCP | protected read (checkpoint facets) | `MarangReadDelegation` (new) | delegation checkpoint |
| `marang_get_artifact` | MCP | protected read (descriptors only, never bytes) | `MarangReadResult` (new) | delegation artifact |
| `marang_intervene` | MCP | protected write | `MarangIntervene` (new) | delegation checkpoint |
| `marang_cancel` | MCP | protected write | `MarangCancel` (new) | delegation |
| `GET /api/delegations` | HTTP | protected read (caller-scoped list) | `MarangReadDelegation` (new) | caller catalog view |
| `GET /api/delegations/{id}` | HTTP | protected read | `MarangReadDelegation` (new) | delegation |
| `GET /api/delegations/{id}/waiting` | HTTP | protected read | `MarangReadDelegation` (new) | delegation checkpoint |
| `GET /api/delegations/{id}/evidence` | HTTP | protected read (findings/artifacts) | `MarangReadResult` (new) | delegation result |
| `GET /api/delegations/{id}/artifacts/{artifactId}` | HTTP | protected read (descriptors only) | `MarangReadResult` (new) | delegation artifact |
| `POST /api/delegations/{id}/interventions` | HTTP | protected write (activate + approve) | `MarangIntervene` (new) | delegation checkpoint |
| `POST /api/delegations/{id}/cancel` | HTTP | protected write | `MarangCancel` (new) | delegation |
| `GET /api/delegations/{id}/run`, `GET /api/runs/{id}` | HTTP | protected read (run projection) | `MarangReadResult` (new) | delegation run |
| `GET /api/runs/{id}/journal` | HTTP | protected read (session journal) | `MarangReadResult` (new) | run session journal |
| `/mcp` transport | HTTP | transport (tools carry their own gates) | n/a | n/a |

Out of scope (non-goals, §25): delegation-execution pumping, workflow-step
runtime authorization, A2A delegation, Gagamba execution authority.

## Why no existing verb is reused

Existing Hufu actions (`ReadFile`, `ListDirectory`, `ReadMetadata`,
`PatchFile`, `Release`, `WriteFile`, `ExecuteProcess`) all govern
workspace-relative filesystem resources. Every Marang operation above governs
delegation-lifecycle resources (delegations, checkpoints, interventions,
result outputs, run projections). None is identical:

- Delegation-state reads are not `ReadMetadata`: different resource kind,
  different representation, different ceiling semantics. Similarity of the
  word "read" is not identity.
- `marang_intervene` is not `PatchFile`/`WriteFile`: it applies a fenced
  supervisor decision to a waiting checkpoint, not a byte mutation.
- `marang_cancel` is not `Release`: it requests durable cancellation of a
  live execution, not a grant release.
- Listing a caller's delegations is not `ListDirectory`: no directory exists.

Forcing any of these mappings would trade a Hufu contract change for a
dishonest authority semantic. The four new actions below are appended instead
(`AuthorityAction` is append-only; persisted numeric values are preserved).

## New Marang-scoped actions (AE-5B.1)

| Action | Genuine meaning | Covers |
|---|---|---|
| `MarangReadDelegation` | read lifecycle/supervision state of a caller-owned delegation | status, detail, waiting, wait-poll, inspect, list, run-by-delegation lookup |
| `MarangReadResult` | read execution outputs of a caller-owned delegation | result summary, evidence/findings, artifact descriptors, run projections, session journals |
| `MarangIntervene` | apply one fenced supervisor intervention to a waiting checkpoint | MCP intervene, HTTP approve |
| `MarangCancel` | request durable cancellation of a live delegation | MCP cancel, HTTP cancel |

Policy-meaningful joints: supervision reads, output reads, intervention, and
cancellation can each be granted or withheld independently. Two reads are kept
distinct because execution outputs (summaries, findings, locations) are more
sensitive than lifecycle metadata (state, revision). Artifact endpoints return
descriptors only, never bytes; byte access stays with the provider and out of
scope.

## Canonical resource identity

`AuthorityRequest` carries `(Context, Action, WorkspaceId, RelativePath,
RequestIdentity)`. Construction rules:

- **Workspace**: taken from the host-owned catalog record for the delegation,
  never from caller arguments (supervision arguments carry no workspace at
  all). For the caller-scoped list (no single delegation), the workspace is
  the tenant scope `marang` and the path names the caller's catalog view.
- **RelativePath** segments (all lowercase; `NormalizePath` enforced):
  - delegation state: `delegation/{id:D}/supervision`
  - checkpoint: `delegation/{id:D}/checkpoint/{checkpoint:D}`
  - intervention: `delegation/{id:D}/checkpoint/{checkpoint:D}/intervention`
  - result: `delegation/{id:D}/result`
  - artifact: `delegation/{id:D}/artifact/{artifactId}`
  - run: `delegation/{id:D}/run` (by-delegation lookup) or `run/{run:D}`
  - journal: `run/{run:D}/journal`
  - list: `catalog/{caller}/delegations`
- **Case rule** (strict): resource segments that are not already canonical
  lowercase are **denied as invalid resources** — never folded. This avoids the
  fold-collision class documented in `HufuFileReader` (where Hufu
  lowercasing can collapse case-distinct store identities). Delegation,
  checkpoint, and run ids are GUID `D` format (already lowercase); caller
  labels are validated tokens; artifact ids must arrive lowercase or fail.
- **RequestIdentity**: fresh GUID per authorization attempt (`Guid.NewGuid()`
  `N` format), following the `HufuFileReader` precedent. No decision is
  reusable across attempts.
- If canonical identity cannot be established (unparseable id, unknown
  artifact, missing record, non-lowercase segment): **fail closed** before any
  authority evaluation.

## First protected side effect and gate insertion per operation

| Operation | First protected side effect | Gate insertion point |
|---|---|---|
| status/result/wait/inspect/get_artifact (MCP) | `catalog.Find` (caller-scoped existence oracle) | method entry, after caller + arg validation, before `Find` |
| intervene/cancel (MCP) | `catalog.Find`, then `ApplyInterventionAsync` / `CancelAsync` | same point; one gate covers the whole call (single resource) |
| detail/waiting/evidence/artifact/interventions/cancel (HTTP) | `ReattachedAsync` (may mutate via `catalog.ReattachAsync`), then runtime call | handler entry, after arg validation, before `ReattachedAsync` |
| list (HTTP) | `catalog.List` (enumerates caller state) | handler entry, before `List` |
| runs/run/journal (HTTP) | `ReattachedAsync`, then projection-source reads | handler entry, before `ReattachedAsync` |
| delegate (MCP) | acceptance path (already gated, AE-5A) | unchanged; never a resource gate (§17) |

## TOCTOU binding

- Delegation/checkpoint/artifact ids are parsed **once** from arguments; the
  authorized ids are the ids used downstream. Mutable request state (facets,
  limits, reasons, keys) is not authority-bound and must not affect which
  resource is accessed.
- `ActivateCheckpointAsync` + `ApplyInterventionAsync` (HTTP approve) run
  under one `MarangIntervene` authorization for the same checkpoint; both
  calls use the already-bound ids.
- Reads re-authorize on every call, including every wait-poll iteration that
  crosses a decision boundary (polling uses one authorization per
  tool-call invocation; a narrowed ceiling denies the next invocation, never
  the in-flight read).
- Intervention fencing (`expectedRevision`, checkpoint exactness) stays as
  defense in depth; it is not authority.

## Observability (wiring phase)

Coarse outcomes only (`resource_authority_permit/denied/expired/unavailable/
evidence_failed/unmapped/invalid_resource`), following the HC-1 denial-mapping
precedent. No credential material, no policy state in user-visible errors.

## Implemented wiring (AE-5B.2)

- `IMarangResourceAuthorizationGate` (Marang.Hufu) is the single shared
  abstraction. Every protected MCP tool and HTTP handler evaluates it before
  its first protected side effect; the handler signatures take the gate as a
  required seam (null/gate-less callers fail closed). The MCP tools receive it
  by constructor injection; the HTTP handlers receive it from the endpoint
  lambda (composed from DI) plus the credential from `HttpContext.Items`.
- `MarangResourceAuthorizationGate` verifies the credential first, then builds
  a fresh Hufu `AuthorityRequest` and evaluates current authority. Its context
  is `(tenant, caller, {delegationId}, revision "g0", fence "resource")` — the
  fence is **distinct** from acceptance's, so acceptance and resource decisions
  can never share a context. The revision `"g0"` mirrors the acceptance initial
  generation; the run id is the delegation under operation.
- Denial and unavailability map to the same `unknown delegation` / 404 shape
  callers use for unknown or forbidden delegations, so authorization never
  becomes an existence oracle. `ReattachAsync` is caller-scoped, so the record
  that supplies the workspace is already the caller's own.
- The caller-scoped catalog list (`GET /api/delegations`) uses workspace =
  `marang` (the tenant scope) and path `catalog/{caller}/delegations`; a host
  must allow the tenant scope in `Marang:Hufu:Resource:AllowedWorkspaces` for
  listing. Empty policy = deny all (fail closed).
- `marang_delegate` (acceptance) is deliberately **not** resource-gated: it
  consults only the acceptance gate. A resource permit never substitutes for
  acceptance and vice versa (§17, AE5B-Q03/Q04).

