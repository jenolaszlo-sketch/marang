# M6.2 source review 05 — readiness for process qualification

**Disposition: source path is ready for process qualification, subject to one evidence-oracle correction or narrower claim.** This is a read-only source review of the current shared tree. No commands that build or run tests were executed. It is not process or cross-platform qualification.

## Corrections reviewed

- The current profile manifest is checked with `PRAGMA table_xinfo` for exact ordered columns, types, nullability, primary-key positions, and absence of hidden/generated columns. Cell storage classes are explicitly checked before typed reads; effect-record rows must have SQLite `integer` storage and value 1. This closes the prior malformed-schema/cell-type source findings.
- `AfterDdl` now occurs immediately after the `ALTER TABLE` that adds `record_format`, before creation/insertion of the profile manifest. `BeforeCommit` occurs only after the complete destination has passed inspection. The process checkpoints therefore distinguish a partial uncommitted DDL state from a complete validated-but-uncommitted state.
- Upgrade still runs strict M6.1 admission before write-open, obtains an immediate transaction, repeats admission under its writer reservation, and validates profile 1 before commit. The current host phase calls `RequireCurrentAsync` before the existing resume path; the contract limits that host guarantee to explicit profile-1 callers and leaves library consumers responsible for calling the gate.
- The old-writer bootstrap checks out the exact M6.1 anchor in a detached worktree, verifies clean source before and after the build, records package resolution and binary hash, and refuses a pre-existing fixture path. The process tests use that built old writer for K3/K4 fixtures, record source/binary hashes, verify preservation across migration, repeat current-profile resume, and use fresh process stop/refusal cases with cleanup and output-capture receipts.
- The qualification wrapper validates exact source cleanliness/commit, reuses the unchanged M6.1 qualifier, checks expected test counts from source against TRX, collects only fresh process-case directories, and seals selected evidence with a manifest. Windows delegates to the unchanged M6.1 Windows runner; macOS pins checkout/setup/upload actions to immutable commits and asserts ARM64.

## Remaining evidence-oracle caveat

`PersistentStateFingerprintAsync` includes checkout HEAD, status text, and bytes for tracked files. It does not hash contents of untracked files that already exist in the controlled checkout. Changing such a file leaves both its status path and the tracked-file inventory unchanged, so the refusal test could miss that specific change. Either include all regular checkout files (and explicitly handle symlinks) in the fingerprint or scope the assertion/documentation to tracked checkout contents plus Git status.

No additional transition-logic blocker was found in this source pass. Once the checkout fingerprint claim is aligned, process qualification may begin; pass/fail must be based on the fresh evidence runs, not this review. Cross-platform qualification and final claims remain pending.
