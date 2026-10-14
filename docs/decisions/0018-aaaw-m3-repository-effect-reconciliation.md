# ADR 0018: Bounded repository effect identity and reconciliation

Status: Experimental M3 host contract, 2026-10-14.

## Decision

Marang owns an explicit repository effect protocol composed with the existing Fuwen admission, Zhinu revision/execution owner, M2 supervisor projection and Hufu authority adapters. No stable foundation or authority producer is changed. Physical repository truth is reconciled before a write, including a corrective revision cutover. Fencing an old execution never reverses its physical effect.

An immutable intent identifies the effect, workflow, revision, logical branch, execution, attempt, canonical checkout root, authority scope, exact full base, target bytes, expected post-state and creation time. Canonical SHA-256 digests bind these fields. Replacing content creates a new effect and an approved successor revision; an old effect cannot be edited into its correction.

The journal shares the pinned Zhinu SQLite database, but its tables belong to the experimental host. Append-only events and the current record distinguish `Prepared`, `IntentCommitted`, `Applying`, `AppliedObserved`, `ValidationPassed`, `ValidationFailed` and `Blocked`. Preparation is not permission to write. Admission records fresh per-target Hufu evidence before durable intent; the writer requires committed intent and a second fresh authority check.

Reconciliation is deterministic and returns exactly four categories: full base means definitely not applied; full expected post means definitely applied; a mixture of before/after targets with unchanged HEAD, logical index and all other files means partially applied; everything else means unresolved. Only definitely-not-applied state may reach the writer, after exact active manifest, unexpired approval and fresh resource authority checks. Applied effects resume recording/validation without mutation. Partial/unresolved effects park, retain evidence and block conflicting scope writes. M3 supplies inspection and discussion, not a command that automatically clears this block.

The executor holds an OS checkout lease through observation, authority, mutation and exact-state validation. Proposal/activation and cancellation use the same lease. One host mutation coordinator may own a checkout scope. External editors do not participate in this lock; an exclusively controlled checkout is a required operational precondition. Full reobservation detects changes at the supported boundaries, but this is not a distributed resource/permission transaction.

The writer replaces one to four existing regular tracked allowlisted files (at most 1 MiB of before+after bytes) using flushed temporary files and individual renames. The entire patch is not atomic. Killing after one replacement intentionally demonstrates partial state. A crash with an orphan temporary file becomes unresolved rather than authorizing replay. Process restart is qualified; filesystem power-loss guarantees are not asserted.

A corrective effect requires an exact new base matching the observed prior post-state, a new effect identity and a normally approved revision. Recorded unique applied successor chains retain historical truth after correction. An old superseded invocation cannot overwrite its prior receipt with the newer checkout snapshot or produce another write.

## Fuwen boundary

The explicit `aaaw.repository-effect` descriptor declares a write. The original read/report descriptor, canonical definitions and M1 routing remain unchanged. M3 admission allows one independent effect branch with an immutable intent digest and the controlled authority marker, plus the existing independent read branches and optional single join.

The callable's idempotent/safe-retry declaration applies to the **keyed reconciliation protocol**, never to unkeyed file replacement. A retry observes the same effect identity, recognizes completed writes, or parks unsafe state. Host ports must dispatch this descriptor to `RepositoryEffectExecutor`; attaching the descriptor to an arbitrary writer violates the contract. The ordinary read authoring entry point rejects effect specifications. Every effect successor requires explicit approval; initial activation cannot admit an effect.

## Supervision and ownership

The effect journal projects semantic witnesses into existing owner state, invalidating stale proposals when meaningful effect state changes. Additional authority timestamps alone do not invalidate review. M2's meaningful-change feed includes physical effect events. The effect catch-up/drill-down adapter combines authenticated durable workflow truth with a locked checkout observation and distinguishes historical applied-then-corrected effects from current result authority. A view cannot grant replay permission.

Repository files remain physically changed after failed validation or logical cancellation. Validation records the exact state, target, command identity, outcome and output digest. A before/after observation prevents evidence from another state being accepted. Read inventory in the qualification refers to the immutable original source commit, so its preserved execution/result remains meaningful after working-tree corrections.

## Consequences and deliberate limits

This is an unpublished, non-packable host experiment. It does not graduate a package or establish general exactly-once external effects. No compensation, rollback, merge, rebase, arbitrary file creation/deletion, Git history mutation, publishing, deployment, database effects, distributed transactions or sandbox qualification is introduced. Hufu, Gagamba and the graduated foundations stay closed. A future broader repository profile requires separate contract review, migration and qualification.
