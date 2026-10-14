# Experimental M1 revision contract

`AdaptiveRevision` is an immutable persisted snapshot. Reusing an ID for different bytes fails. A proposal includes the parent, state digest, candidate, conservative change set, admitted dispositions, host review profile and impact summary. Its SHA-256 binds all of those facts. Persistence copies caller-owned arrays before asynchronous admission.

Logical ID recognizes intended continuity; revision-step ID labels the representation in the new revision. Run ID recognizes an actual execution. A preserved branch retains its run ID and exact workflow/input/definition contract. Changed, added or invalidated branches require new run IDs that cannot collide with another execution or workflow.

The vocabulary contains Preserved, Added, Removed, Modified, Superseded and DependencyChanged; the deterministic M1 comparer emits Preserved/Added/Removed/Modified/DependencyChanged. Supersession is represented by replacing an affected execution and retaining its historical revision. Admission currently supports Continue/AllowToFinish for equivalent preserved work and Fence for affected work. Cancel, SupersedeOnCompletion and RequiresManualDecision are reserved classifications and are rejected by automatic admission. No provider cancellation is implied by Fence.

The authoritative state digest includes revision content, durable run/step states, attempts, generations, results and generic execution witnesses. Heartbeat expiry timestamps are excluded. Qingniao journal transitions project a witness atomically; provider progress therefore cannot silently evade staleness checks. No untrusted event can install a witness or approve a revision.

Approval is recorded by the authenticated host, resolved from durable evidence, checked against current Hufu authority, and rechecked on commit. The runtime verifies pending proposal identity/digest, current parent/state and approval expiry inside its transaction. That transaction increments existing Zhinu generation fences on affected runs, retains completed evidence, creates new pending branch runs, records dependencies, stores the immutable revision and makes it active. A crash sees either the old state or the complete new state.

Replacement dispatch is an idempotent retry against reserved run IDs and exact input/definition bindings. The runtime lease boundary refuses a join until every required run has completed. Current outcome construction reads only active-manifest executions. Old outputs, provider callbacks and retries cannot be adopted by matching names.

Host Hufu checks are preflight; resource mutation needs its own final authority check. There is no general atomic transaction spanning concurrent authority-store revocation and arbitrary external effects. This proof is finite read/report work with scoped artifacts, not arbitrary side-effect rollback.
