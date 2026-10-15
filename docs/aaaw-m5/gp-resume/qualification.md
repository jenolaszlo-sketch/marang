# AAAW M5 completed bounded qualification

Date: 2026-10-16. Status: **COMPLETE for the exercised repository profile**.
Qualified Marang implementation/consumer source:
`b9df4b6fa9d1553b5f2df95dceebb699238ce935`, branch `codex/aaaw-m5-marang`.
All three runs began and ended at that clean, unchanged source revision.
This evidence is committed separately from the tested implementation.

Gagamba implementation: `0c1f05e199588622a5987f95a9149374f5bd858b`.
Gagamba evidence/review commit: `2c8f4f7d7974510bac4baee9713acb6941b3ff09`.
Its [GP-M5 qualification](https://github.com/jenolaszlo-sketch/gagamba/blob/2c8f4f7d7974510bac4baee9713acb6941b3ff09/docs/evidence/gp-m5/qualification.md)
completed before this Marang resumption. The five local candidate packages
remain `0.1.0-preview.4-gp-m5.1`, pinned by exact hashes and repository commits.
No package was published or graduated from preview.

The blocked history through `85773fe`, including the original Mac failures,
remains unchanged under its original evidence paths. This report adds the
successful resumption; it does not replace those observations. Marang production
code, canonical C# test bodies, assertions and barrier validators are unchanged.
The input/tooling review records the limited package and preflight changes.

## Fresh matrix

Counts are passed / failed / skipped, using actual TRX result outcomes.

| Native profile | .NET 8 | .NET 10 | Restart barriers | Actual-state control |
| --- | --- | --- | --- | --- |
| Windows x64, build 26200, native Job Objects | 201 / 0 / 1 | 246 / 0 / 1 | 12 / 12 | PASS |
| Ubuntu 26.04.1 WSL2 x64, delegated cgroup v2, UID 1000 | 202 / 0 / 0 | 247 / 0 / 0 | 12 / 12 | PASS |
| macOS 15.7.9 ARM64, launchd `gui/501` | 201 / 0 / 1 | 246 / 0 / 1 | 12 / 12 | PASS |

Every skip is the same test:
`RepositoryPortabilityTests.Distinct_case_sensitive_git_roots_have_distinct_repository_identities`.
The Windows and Mac temporary filesystems alias case-only names. Linux executes
the test and passes. No workflow/barrier/control test was skipped. TRX counters
can report zero `notExecuted` despite a skipped result; the actual result entries
are authoritative for this table.

SDK 10.0.401 is used across the matrix, with SDK 8.0.426 available on Linux and
Mac. Windows runtime inventory selects 8.0.31 / 10.0.12; Linux records installed
8.0.31, 8.0.32 and 10.0.12; Mac records 8.0.32 / 10.0.13. Native process/barrier
qualification uses the existing .NET 10 composition. The .NET 8 rows are the
complete supported regression suite. Gagamba's producer remains .NET 10 only.
The raw SDK/runtime inventories preserve the measured environment.

## Semantics established on each platform

- The canonical scenario completes with exact effect/state lineage, accepted
  A/B2/C2, historical B/C, and final workflow/local validation passed.
- A, B, C, B2 and C2 each have one physical application. Valid A is explicitly
  adopted without replay; unrelated inventory work executes once.
- Stale B/C are invalidated transitively, and B2/C2 use actual recovered
  repository states. The obsolete graph is not accepted as validated.
- All twelve restart boundaries have fresh context, used marker and confirmed
  real-provider stop receipts. K4 is the separate partial-application case;
  K12 requires an actually running broker-owned validation command and a
  confirmed explicit stop.
- K4 leaves the prerequisite PartiallyApplied and its dependent Prepared,
  named-blocked and physically unexecuted.
- The actual-state negative control detects external project-byte divergence
  and remains fail-closed across two fresh reconciliation processes. Mutation,
  validator and checkpoint execution stay blocked; B/C and the tampered state
  remain unchanged.

The new macOS preflight also passed ordinary native lifecycle control, exact
bounded stdout/stderr capture and invalid/over-ceiling pre-dispatch refusal.
It records provider disposal and scratch cleanup. Those controls support the
execution prerequisite; the unchanged semantic scenarios above qualify M5.

## Source, ownership and evidence

Windows uses the retained reproduction driver and independent semantic checker.
Linux uses a fresh Git clone from the exact source bundle, a fresh root-created
delegated cgroup, and the existing bounded qualifier. Its parent reached
`populated 0` and was removed. The initial bundle verification outside a Git
repository and the clone's missing-remote-HEAD warning are preserved as setup
observations; the exact checkout and all qualification gates subsequently pass.
Mac [native run 38820432354](https://github.com/jenolaszlo-sketch/marang/actions/runs/38820432354)
uses ARM64, a physical task temporary directory and immutable action revisions.
Every source-start/end pair binds to the same clean `b9df4b6` commit.

The four M1 package payloads remain byte-identical to the blocked baseline.
All five replacement Gagamba package hashes match the separately qualified
producer set on all three platforms. See [inputs](inputs.md), [matrix](matrix.json),
the preserved [Windows](windows/manifest.json), [Linux](linux/manifest.json),
and [Mac](macos/manifest.json) artifacts, and the
[final independent review](reviews/02-final-semantic-review.md) and its
[canonical-state clarification](reviews/03-canonical-state-clarification.md).

## Supported claim and limits

AAAW M5 is complete for the bounded repository profile on Windows, bounded WSL2
Linux and macOS ARM64. The exercised M1–M4 semantics survive the recorded restart
barriers: valid mutations remain preserved without replay, stale dependents are
replaced from actual recovered repository state, unresolved effects remain
fail-closed, and unrelated valid work remains preserved.

This remains an experimental repository host profile. It does not establish
production readiness, arbitrary external-system exactly-once behavior, generic
compensation/rollback, distributed transactions, arbitrary concurrent external
writers, power-loss/storage crash consistency, arbitrary remote execution,
hostile-code isolation, Mac owner-death cleanup or setsid containment. Mac and
Unix repository identity requires canonical exact root spelling; case-insensitive
alias spelling remains outside this qualification. No Hufu implementation,
restricted Linux workstream, new workflow feature or M6 work was added.
