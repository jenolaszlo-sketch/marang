# M6.1 frozen-candidate prequalification review

Date: 2026-10-16. Reviewed frozen candidate
`41829680bf68ca92d9fc72e82222804da360f8f6` against baseline
`9638e2a37524ba6999c4e7df6016851578ec7fc8`. This review covers source scope
and qualification harnesses. The Windows, WSL2 Linux, and native macOS matrix
was still running at review time; this is not a qualification result.

## Scope and compatibility

The baseline diff changes the existing effect journal, adds the integrity
inspector and tests, adds the recovery-entry guard to the restart probe, and
adds M6.1 documentation and qualification tooling. The M5 qualification
scripts, original M5 tests, package lock/config files, pinned package files,
and M5 evidence files are unchanged in the source diff. The effect-journal
change is observable on public reads: it now validates projections and event
history transactionally, which is the intended M6.1 detection behavior. It
does not add repair, migration, restore, or new workflow semantics.

The Windows runner requires a clean committed source tree before running,
records that commit, runs the pinned feed bootstrap, restore/build/format and
both full test frameworks, then passes runner errors into the validator. A
failed runner command prevents a pass even if a stale TRX happens to remain.
The validator checks exact expected M6 test-case counts from test source,
rejects unexpected failures/skips, requires the reviewed single filesystem
skip at most, validates M5 semantic controls, and requires clean unchanged
source at the end. The process-artifact validator checks fresh per-scenario
directories, typed refusal, missing admission markers/authority store,
unchanged DB evidence, and two distinct lifecycle receipts with confirmed
cleanup and complete capture.

On Linux/macOS the wrapper invokes the unchanged M5 qualification entrypoint,
uses its returned run identity and test-result paths, adds M6 test/evidence
validation, and relies on the M5 source-state checks for clean start/end.
Python bytecode generation is disabled for the wrapper. The macOS workflow
pins its checkout, .NET setup, and artifact-upload actions to immutable commit
SHAs, verifies ARM64, and uploads raw qualification evidence. No accidental
change to the M5 test or package-lock surface was found.

The file-only damaged-store oracle correctly allows only absent-to-absent or
absent-to-zero-length-WAL behavior for the described cases, checks exact main
DB preservation, and records the sidecar disposition. Separate live-WAL
subprocess cases require exact main DB and existing WAL byte equality. The
owned-process refusal verifier requires two distinct correlations, nonzero
exit, confirmed cleanup, and complete output capture.

## Remaining source question

The effect-event reader orders by the SQL `sequence` column but does not read
or validate the sequence values. The lifecycle checks alone may not detect a
deleted interior event when the remaining stages are still a legal transition
(for example, `IntentCommitted` directly to `AppliedObserved` is allowed).
That leaves a sequence gap without necessarily changing the final projection
or event. Because M6.1 claims bounded detection of missing append-only effect
history, require a contiguous sequence check and an isolated interior-event
deletion fixture, or explicitly narrow the contract to the missing-history
forms that are tested. This is the one unresolved source coverage question I
found; I reported it to the implementation owner for disposition before final
qualification.

## Disposition

Harness structure and M5 scope preservation look sound from source review.
Final acceptance remains pending resolution of the sequence-history question
and completion of the exact-source multi-platform qualification evidence,
package/source provenance, cleanup receipts, and final review. No M6.1
qualification claim is made here.
