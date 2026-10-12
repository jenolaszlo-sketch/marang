# Marang: pending Penghou.Hufu integration

Status: **Pending integration; not implemented.** Recorded 2026-09-28.

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
Marang references `Penghou.Hufu` 0.1.0-preview.6 and removed its local
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

## Dependency and design home

Implementation depends on Hufu's reviewed contracts, durable-store semantics,
and a proven host/resource-broker enforcement path. Continue current correctness
work independently; do not add placeholder dependencies or infer security from
the existence of Hufu's scaffold.

Canonical design (links assume sibling checkouts):

- [Hufu architecture](../../Penghou.Hufu/docs/architecture.md)
- [Authority specification](../../Penghou.Hufu/docs/workflow-authority-spec.md)
- [Hufu implementation roadmap](../../Penghou.Hufu/docs/roadmap.md)
