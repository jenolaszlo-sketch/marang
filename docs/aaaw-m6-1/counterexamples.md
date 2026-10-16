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

The final focused pre-freeze run passed 81/81 on .NET 10. It covers healthy
initial/pending/committed states, Applying, partial Blocked and ValidationFailed
effects, supervisor continuity, provider uncertainty, isolated physical/logical
damage, two consecutive fresh owned entry refusals for each of six damage
classes, and nine fresh-process WAL-backed inspections. This focused run does
not substitute for the full native M5/M6.1 qualification matrix.
