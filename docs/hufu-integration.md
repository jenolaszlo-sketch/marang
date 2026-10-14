# Marang: pending Penghou.Hufu integration

Status: **Pending integration; not implemented.** Recorded 2026-09-28. Later
sections record implemented slices (Hufu-owned delegation visibility, and the
AE-5A.2 delegation-acceptance authority); read those as the current state.

Penghou.Hufu is the new reusable authority library and authority-store boundary.
It currently contains a buildable scaffold and design documents, with no public
authority API, enforcement implementation, or persistent authority store.
This note records future consumer work; it does not announce a package dependency,
a shipped security guarantee, or an additional current-release acceptance gate.

Hufu will own reusable grants, envelopes, authority requests and decisions,
attenuation, revocation, and durable authority records. Hosts retain identity,
policy, credentials, resource resolution, and approval surfaces. Zhinu retains
execution state and recovery; existing budget services retain accounting.

## Marang's planned integration

- Compose Hufu and its authority-store adapter through trusted host configuration.
- Expose bounded MCP/HTTP surfaces to inspect authority needs, submit permitted
  decisions, inspect their application status, and explain blocked work.
- Authenticate the actor, tenant, and approval rights independently of planner
  messages; bind every decision to the exact request digest and expected state.
- Transport partial grants, denial, expiry, and revocation with idempotent command
  identities and protected audit references.
- Resume Qingniao/Zhinu only through their authoritative admission and execution
  contracts; distinguish a recorded grant from successful workflow activation.

Marang retains transport, authentication, host policy/composition, and operational
limits. It must not create a second grant store, planner, or workflow activation
engine. An MCP approval response or supervisor wake-up is not itself authority.

## Completion evidence

Wrong-tenant, unauthorized, stale, or repeated conflicting decisions are rejected.
Duplicate valid commands are harmless. Operators can see the exact requested
delta and whether it was granted, applied, blocked, or superseded, without secret
material leaking through responses or logs.

## Delegation visibility authority moved into Hufu

Recorded after the M5.5 restart-safe observation slice and completed as a
behavior-preserving migration. Delegation visibility authority (the
`(tenant, subject, delegation, generation)` binding with Observe/Cancel/
Intervene operations, validity windows, idempotent publish commands, and
terminal revocation) is now **Hufu-owned**: `Penghou.Hufu.
IDelegationVisibilityStore` / `DelegationVisibilityBinding` with the
`Penghou.Hufu.Sqlite.SqliteDelegationVisibilityStore` durable implementation.
Marang references `Penghou.Hufu` (now `0.1.0-preview.8`; see AE-5A.2 below) and removed its local
`Marang.DelegationAuthority.DelegationVisibility` /
`SqliteDelegationVisibilityStore`.

Marang retains exactly what is host-owned:

- authentication and stable principal mapping,
- the durable `AcceptedDelegation` product-metadata descriptor,
- acceptance identity and the ordered accept-then-expose protocol, and
- operator read composition (cache/read path).

Cross-record agreement checks stay in Marang; the `Guid`↔`D`-string conversion
happens only at the store boundary. Behavioral equivalence is proven by the
unchanged Marang suite: two-process restart reattachment, caller isolation,
replay/conflict, expiry/revocation, and historical-read reconstruction all
pass against the Hufu-owned store. Store-level publish/revoke/check semantics
are qualified in the Hufu suite. No acceptance-identity, restart, control-
continuity, projection/journal, authentication, or metadata-ownership
semantics changed.

## AE-5A.2: delegation-acceptance authority (implemented, 2026-10-14)

Marang now consumes the published Hufu packages from nuget.org at
`0.1.0-preview.8` — `Penghou.Hufu`, `Penghou.Hufu.Cedar`,
`Penghou.Hufu.Biscuit` and `Penghou.Hufu.Biscuit.Sqlite` (which pins
`BiscuitSharp 0.1.0-preview.2`). No local feed, no source reference and no
project reference to a Hufu repository.

**Invariant:** no durable acceptance-side effect occurs unless Hufu has first
returned an accepted `DelegationAcceptanceOperation.Accept` decision for that
request. The gate runs at the very start of
`MarangDelegationCatalog.AcceptAsync`, before `_descriptors.EnsureAsync`, the
visibility publish and any in-memory acceptance mutation. The credential is
carried in HTTP metadata (`X-Marang-Delegation-Credential`) into
`HttpContext.Items` as a typed `MarangDelegationCredential`; it is never a tool
argument. Credential verification (`IMarangDelegationCredentialVerifier`)
precedes authorization; a signature never authorizes on its own, and acceptance
authority is never inferred from resource/workspace authority or from `Derive`.

Acceptance policy and evidence live in `Marang.Hufu`:
`HufuDelegationAcceptanceEvaluator` (host policy over the caller's live
current-authority snapshot plus host-configured acceptance bounds) and
`HufuDelegationAcceptanceRecorder`, composed by `MarangDelegationAcceptanceGate`
with the Hufu `CurrentDelegationAcceptanceAuthorizer`.

### Qualification (Marang suite, .NET 8/10)

| Case | Result |
| --- | --- |
| accepted delegation (valid credential + current authority) | proceeds; evidence recorded; durable descriptor created |
| missing credential | denied before any durable state |
| tampered/invalid credential | rejected before the Hufu decision |
| valid credential + denied current authority | denied; no durable state |
| current-authority workspace ceiling removed (narrowing) | denied; no durable state |
| unavailable current authority | `Unavailable`; no durable state |
| acceptance evidence failure | fail closed; no durable state |
| retry with same request key | re-evaluates current authority each use |
| Biscuit credential verifier | well-formed envelope `Verified`, null `Missing` |
| credential header codec | round-trips; malformed rejected |

Package-graph proof: a fresh isolated-cache restore of `Marang.Hufu` with a
nuget.org-only configuration resolves `Penghou.Hufu`, `Penghou.Hufu.Cedar`,
`Penghou.Hufu.Biscuit` (all `0.1.0-preview.8`) and `BiscuitSharp/0.1.0-preview.2`
with zero project references.

### Concrete conflict and residual

The invariant makes a **live Hufu current-authority source a required host
dependency**, which Marang did not previously own. Resolved for this slice:
`Marang.Server` registers a fail-closed
`IMarangDelegationAcceptanceGate` by default (never accepts; no bypass), and a
host must register a real gate composed from its own `IAuthoritySnapshotSource`.
The acceptance gate is a required `MarangDelegationCatalog` constructor
parameter, so no construction path accepts work without it. Residual: the
production Server does not yet compose a live Hufu authority source, so
end-to-end acceptance in the default Server configuration fails closed until a
host supplies one; the acceptance proof runs against a host-supplied source in
the Marang suite. Resource/execution operations inside an accepted delegation
remain independently authorized (AE-1); `Accept` confers no resource rights.

## Dependency and design home

Implementation depends on Hufu's reviewed contracts, durable-store semantics,
and a proven host/resource-broker enforcement path. Continue current correctness
work independently; do not add placeholder dependencies or infer security from
the existence of Hufu's scaffold.

Canonical design (links assume sibling checkouts):

- [Hufu architecture](../../Penghou.Hufu/docs/architecture.md)
- [Authority specification](../../Penghou.Hufu/docs/workflow-authority-spec.md)
- [Hufu implementation roadmap](../../Penghou.Hufu/docs/roadmap.md)
