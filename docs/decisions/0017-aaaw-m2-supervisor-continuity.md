# ADR 0017: Durable supervisor continuity in the host

Status: accepted for the experimental AAAW M2 profile.

M1 owns immutable Fuwen plans, atomically activated Zhinu revision manifests, exact approvals and Qingniao provider-attempt recovery. A returning operator also needs the meaning of a pending decision and an explanation of which results still satisfy active intent. Reconstructing those facts from a chat transcript would create a second, unreliable authority path.

Marang retains bounded revision intent, pending discussion, operator checkpoints, reviewed proposal context and recovery-decision receipts beside the existing SQLite owner. It writes concise decision evidence to the existing adaptive outbox in the same transaction as each host-state mutation. Hongxian mirrors that outbox using existing idempotent event keys. It owns neither execution nor active-revision selection.

The supervisor state is a deterministic projection: cancelled; unresolved delegation; stale/denied/blocked; pending approval or ready to apply; discussing; completed; running. Atomic activation has no durable intermediate Applying state. A crash exposes either the old revision or the fully committed revision. State transitions depend on durable owner facts, never on previously displayed UI or model output.

Catch-up reads the pinned M1 owner schemas and host tables in one SQLite read transaction. This read-only adapter is deliberately experimental and version-bound to the exact source/package lock. It cannot mutate run rows, leases, approval receipts or provider observations. The M1 state digest queries are reused for host discussion checks inside the host write transaction. A future owner schema change requires adapter/conformance validation; this is not a stable public database API.

An intent binds both the immutable revision digest and Fuwen objective digest. The host records it while an exact candidate is pending, before activation, or explicitly backfills it from verified authoring input. Missing metadata appears as a blocker rather than an invented current goal. Metadata is immutable and exact replays are idempotent. Chat history and private model reasoning are unnecessary.

Discussion decisions bind the parent revision and authoritative execution/provider digest. Progress makes them stale. An authenticated operator may dismiss a stale discussion and open a new one; dismissal never applies its offered stale choice or changes workflow execution. A discussion choice is context for authoring, not approval. The existing deterministic proposal/approval/commit protocol remains the only activation path.

Reviewed proposal context retains which equivalent branches were completed when reviewed. Commit validates that same authoritative state. Drill-down can therefore explain explicit completed-result reuse without inferring it from completion that occurred later.

Recovery options are deterministic and profile-controlled. Wait persists an unresolved choice. Observe addresses only the already captured operation. Replace stages an exact reviewed revision, requires a fresh Hufu evaluation at proposal, approval and commit, and atomically fences the old execution through M1. It is available only for the admitted repository read/report profile, whose host ports exclude external mutations. The recovery action set omits cancellation because M1 cancellation is workflow-wide and has no atomic condition-version precondition. The existing separate workflow command retains its fresh authority semantics. Standalone retry, abandonment, result adoption and physical cancellation are not introduced. Fencing does not stop an external operation or undo its effects.

Qingniao's internal provider run identifier remains distinct from the Zhinu branch execution. Their immutable journal binding establishes the relationship. Recovery receipts record exact delegation/execution, condition digest, options, choice, actor, timestamp, proposal, authority outcome and resulting revision. If the host dies after activation but before receipt update, the exact committed owner proposal determines the outcome; the receipt cannot activate anything independently.

Operator checkpoints affect presentation only. An acknowledgment binds the revision and evidence watermark actually viewed. The current view reports selected results separately from superseded results and historical/unknown provider evidence. Recent changes and timeline output are bounded; any older revision remains available through drill-down.

No stable foundation, Hufu or Gagamba implementation is changed. General provider reconnection, full Qingniao supervisory-state rehydration, arbitrary external rollback, compensation, semantic merge, collaborative editing and distributed transactions remain deferred.
