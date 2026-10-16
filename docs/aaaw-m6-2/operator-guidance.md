# Bounded profile compatibility and explicit upgrade

The M6.2 API applies to the experimental bounded repository profile qualified
by the MultiEffectProbe host. It is not automatic compatibility enforcement for
every Marang server/store entry point. An embedding host must enforce the same
ordering before constructing initializing owners or entering execution.

Use one controlled recovery owner. Existing source semantics are fixed by M6.1
anchor `5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29`, Zhinu schema 6 and unchanged
package pins. Do not infer compatibility from a package's version number alone.

For compatible read-only legacy admission, use
`RepositoryRecoveryInspector.InspectAsync`/`RequireAsync`. These accept only
the recognized implicit legacy or supported tagged profile and run the existing
physical/logical checks. They do not migrate, authorize execution or determine
repository freshness.

For a host that requires explicit profile 1, call
`RepositoryProfileCompatibility.RequireCurrentAsync(executionPath,
expectedWorkflowId, requiredStores, cancellationToken)` before entering the
existing host. A recognized legacy store refuses with an explicit-transition
requirement. No owner initializer may serve as an implicit upgrade.

To request the one supported transition, call
`RepositoryProfileCompatibility.UpgradeAsync` with those same known identities
and required store paths. It validates existing storage, atomically adds the
profile metadata and effect format tags, and returns `Upgraded` or
`AlreadyCurrent`. It performs no workflow execution or repository operation.
Afterward require current admission again; current approval, authority, fencing
and actual-repository reconciliation still govern execution.

If the upgrade process exits before success is reported, repeat the same
explicit upgrade against the existing files. The qualified pre-commit cases
remain legacy and the qualified committed case returns `AlreadyCurrent`.
Never reset a marker or delete a column to force retry. Partial combinations
and unknown formats refuse; they do not request automatic repair.

Missing, physically damaged or logically inconsistent state retains the M6.1
refusal classifications. Unsupported profile shape, owner, identity/digest or
record format also refuses admission. Keep uncertainty about external effects;
do not label the workflow new or infer that a missing receipt means a mutation
did not happen. Cancellation remains cancellation.

No downgrade operation is provided. An unchanged old binary predates the new
metadata and cannot be technically prevented by this additive DDL from opening
the store. Such a binary is not a qualified profile-1 writer. Do not run old and
new owners concurrently or treat old-binary startup as a supported rollback.

This batch supplies no backup/restore, repair, resource-exhaustion handling,
soak, generic predecessor migration or arbitrary external compensation. See
the [contract](contract.md) for scope and preservation limits; qualification
evidence is required before claiming this transition is complete.
