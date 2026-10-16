# M6.1 counterexamples and controls

Baseline production behavior: Marang
`9638e2a37524ba6999c4e7df6016851578ec7fc8`. The baseline runs added test fixtures
before wiring the admission check or changing the effect journal. They are not
full clean-source qualification runs. Original TRX/logs and retained probe
artifacts are in `evidence/development-counterexamples.zip` with SHA-256 records.

| Baseline | Actual observation | Bounded correction |
| --- | --- | --- |
| Effect journal: 13 failed corruption cases, 3 passed healthy controls | Public reads accepted projection/index disagreement, bad event digests/JSON, missing history/projection and inconsistent current history. The failing test names and assertions are retained in the original 16-case TRX. | Transactional verified reads compare index/document identity, event digests/lifecycle and final history state, including cumulative authority evidence. Writes verify existing history before extending it. |
| Known recovery entry: 4 failed cases | Missing and empty execution stores were initialized, including creation of execution/authority databases, before the old scenario refused its later precondition. Non-SQLite and truncated-header cases already failed through SQLite, after entering the workflow/checkout path; these are earlier-admission/classification counterexamples, not proof that all four dispatched a mutation. | Inspect known state before workflow markers, checkout work, initializing owners or authority publication; return a typed bounded refusal. Missing state stays missing. |

An earlier direct probe invocation without its required Gagamba lifecycle owner
failed at setup and created no store. It is retained as a setup failure, not a
corruption counterexample.

Development tests additionally exposed a Windows evidence-reader sharing error
when hashing an open SQLite file; the test reader now shares read/write/delete
access while the application inspector remains read-only. Two later entry
assertions incorrectly assumed a read-only WAL-mode inspection could never
materialize an empty WAL sidecar. The corrected oracle records
`absent-to-empty-created` explicitly, requires zero length/no frames, and keeps
exact original main-file preservation. Existing live WAL bytes must compare
exactly. Original failing TRX files are retained rather than replaced by passes.

A temporary SQLite/Git fixture initialization failed with disk I/O during an
overlapping development run. The exact cause was not established; the recovery
fixture collection now runs without parallel test collections, and final native
qualification must exercise that configuration. An intermediate build also
caught an undefined helper while a concurrent validator edit was incomplete;
no tests ran from that build. Neither event is classified as a corruption fix.

Independent source review found and closed additional admission gaps: effect
history absent from the guard, graph/causal/journal intent disagreement, missing
execution/continuity tables, provider handle-index reciprocity and provider
attempt-to-branch binding. The reviews are preserved in order under `reviews/`.
Bindings accepted before the provider pump, and uncaptured or non-reconnectable
provider states, remain admissible without guessing external operation state.

The first candidate's focused pre-freeze run passed 81/81 on .NET 10. It covers healthy
initial/pending/committed states, Applying, partial Blocked and ValidationFailed
effects, supervisor continuity, provider uncertainty, isolated physical/logical
damage, two consecutive fresh owned entry refusals for each of six damage
classes, and nine fresh-process WAL-backed inspections. This focused run does
not substitute for the full native M5/M6.1 qualification matrix.

## First frozen native attempt

Candidate `41829680bf68ca92d9fc72e82222804da360f8f6` was rejected by full
qualification on Windows, WSL2 Linux and macOS ARM64. All new M6.1 test classes
and process-artifact gates passed. The original canonical/K4/actual-state
scenarios and twelve barriers passed. One unchanged M5 regression test failed
on each framework on every platform:
`Superseded_applied_effect_replay_preserves_its_physical_receipt_after_successor`.

The new journal reader had incorrectly required a SHA-256 validation output
identity. The existing journal writer permits any nonempty output identity,
including the historical test's `output` value. The correction follows that
existing contract; the original M5 test is unchanged. Graph-level validation
already requires SHA-256 and retains that requirement. The failed native
reports, raw results, cleanup and source identity are retained in
`evidence/rejected-4182968.zip`; macOS run
[38888331658](https://github.com/jenolaszlo-sketch/marang/actions/runs/38888331658)
remains a failure, not a passing portability result.

Review also identified missing journal event-sequence checks. A first copied
M5-state experiment deleting an Applying event was already refused because its
authority delta no longer matched the projection; that experiment is a
successful negative control, not the sequence counterexample. The isolated
journal fixtures instead remove an empty-authority Applying event whose loss
leaves a legal transition, or delete the last event and consistently rewind
the projection. Retained global numbering/allocator evidence is the remaining
recorded contradiction in those fixtures. Numbering is checked globally;
interleaved per-effect event numbers are valid. No history is repaired or
renumbered, and a coherent rewrite of both events and allocator remains outside
the detection claim.

Against unchanged candidate production code, those two isolated deletion tests
failed to obtain a refusal, while the interleaved healthy control passed
(2 failed / 1 passed). Their original TRX and console output are included in
`rejected-4182968.zip`. An initial stalled restore was cancelled and retained as
setup evidence; the completed baseline run is `net10-run2`.

A final integration counterexample used a healthy initial workflow with no
effects, then retained a positive event allocator watermark while leaving the
event/record sets empty. The inspector incorrectly skipped the journal check
because it had no effect IDs to enumerate (1 failed test). It now runs the
global check unconditionally. The corrected focused run passed all 85 M6.1
cases plus the unchanged M5 replay control (86/86); the sequence correction and
old replay control also passed their four-case focus on both frameworks.
Original baseline and corrected TRX files are in `evidence/corrections.zip`.
