# M6.1 preliminary independent design review

Date: 2026-10-16. Baseline: `9638e2a37524ba6999c4e7df6016851578ec7fc8`; branch `codex/aaaw-m6-1-integrity`. This is an early read-only review of the dirty M6.1 candidate, not approval or qualification.

## Immediate gap: effect history is not yet in the inspector path

`RepositoryRecoveryInspector` checks physical SQLite integrity, required tables, `RepositoryRecoveryContents`, and optional additional database files. `RepositoryRecoveryContents.ValidateGraphs` checks graph and causal projections, graph validations, and prerequisite-authority records. It does not inspect `repository_effects` or `repository_effect_events`. The in-progress `SqliteRepositoryEffectJournal` read changes validate those rows when its APIs are called, but that is not equivalent to validation inside the pre-admission inspector. Unless the entry guard explicitly invokes a full integrity read before any initializing/recovery work, the required effect projection/event-history check is missing from the admission boundary.

Before a pass claim, add a read-only inspector check of effect documents, identity/index columns, event digests, lifecycle transition/history consistency and final-event-to-projection binding. Exercise event and projection corruption through the same known-workflow entry point, plus healthy initial, in-flight, partial, blocked and completed records where feasible.

## Scope leakage risk

The provider-recovery scan reads all rows from `adaptive_delegation_attempts`, `adaptive_provider_handles`, and `adaptive_branch_delegations`; the causal-effect scan reads all rows from `repository_causal_effects`. These tables are not filtered to the expected workflow. If the database can contain multiple workflows, unrelated inconsistent state can prevent recovery of a healthy workflow. Confirm the database’s ownership boundary or scope these checks to the requested workflow, and add a two-workflow isolation test.

## Qualification coverage still needed

The current entry-process tests cover only missing, empty, non-SQLite and truncated-header files. They assert refusal before workflow/checkout/authority side effects and preserve the damaged main file. The journal tests add several local corruption cases and healthy Applying, Blocked and ValidationFailed reads, but do not yet show the inspector rejecting logical corruption before admission. The contract also requires missing required tables/rows, malformed documents, digest mismatch and cross-record disagreement, fresh-process repeated refusal, and a healthy fresh-process recovery control through the same boundary. WAL preservation and sidecar observations need explicit evidence for WAL-backed databases.

## Positive observations and boundary

The inspector uses `SqliteOpenMode.ReadOnly`, private cache, disabled pooling and an explicit read transaction. It distinguishes missing, damaged, unavailable, missing-shape and inconsistent states; cancellation propagates; `CheckFile` refuses empty files and directories/reparse points. Those choices align with the contract, subject to actual WAL-byte and no-side-effect qualification. This review does not claim that the guard is wired at the required point yet; the root task notes that insertion before workflow/check-out/runtime initialization remains in progress.

No final M6.1 claim is supported until the guard is wired and the required fresh-process, preservation, healthy-state, platform-matrix and source/package-identity evidence is complete.
