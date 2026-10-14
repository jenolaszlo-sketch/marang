# ADR 0016: bounded adaptive branch admission

Status: accepted for the experimental M1 read/report profile, 2026-10-14.

The first adaptive proof needs to retain unaffected work that is still executing. A whole-run fork copies completed steps but restarts unfinished work; changing the single owning generation of a run would fence every branch. Neither behavior proves partial adaptation.

M1 therefore compiles one immutable Fuwen phase plan and admits each independent branch through the existing Fuwen–Zhinu factory. The optional runtime revision repository owns a durable manifest over these run identities. Unaffected executions retain their run and step identities. Changed branches receive fresh execution identities. One join may consume all independent branches; prerequisite completion is enforced inside the SQLite run-lease claim transaction, as well as by host dispatch.

Proposal, exact approval binding, replacement pending-run creation, affected-run generation fencing, revision activation and transition evidence use the same database owner. Fencing and activation commit together. A process killed after a fence write rolls the transaction back. Provider work may continue physically; only explicitly selected current run identities contribute to the current outcome. This fences result authority, not arbitrary external side effects.

Qingniao owns durable provider-attempt recovery semantics. Marang supplies a SQLite journal behind Qingniao's interface and projects its changes into Zhinu's generic execution-state witnesses in the same transaction. This settles the minimum M1 boundary left open by the M5 provider-handle recovery design: it does not move provider semantics into Hongxian or introduce a second engine in Marang.

Marang owns authoring validation, host policy, authenticated operator actions, exact approval evidence and the catch-up view. The host's configured policy determines approval requirements; a model or UI cannot choose to omit approval. Hufu's current request authorizer is checked at proposal and immediately before commit. Hongxian receives idempotent evidence delivery from the durable runtime outbox; delivery failures do not alter execution truth.

All new surfaces remain experimental. Existing run/generation contracts and the stable foundations are unchanged. Arbitrary graph partitioning, mutable cross-branch inputs, provider mutation recovery, cancellation, compensation, and general distributed authority/execution transactions are excluded. Future work must expand this profile explicitly rather than claiming that its partitioning is valid for every Fuwen operator.
