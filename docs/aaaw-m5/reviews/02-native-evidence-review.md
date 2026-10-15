# M5 independent native Linux evidence review (interim)

Audit target: Marang `b1ccd71de6625e7ab851cfb83adf0662e8f2683f`; runtime source `9fe127a98f682514138f4f5e333e47bcbeea8fe5`. Read-only review of retained WSL2 artifacts and the current Linux qualification ledger. No SDK tests, builds, or platform runs were started by this reviewer. This is not the final qualification review.

## Finding: Linux consumer qualification is not yet accepted

The current consumer ledger at `artifacts/aaaw-m5/linux-qualification/qualification.json` records a clean start/end at Marang `b1ccd71...`, both target frameworks, and passing test results: net8.0 202 passed, 0 failed, 0 skipped; net10.0 247 passed, 0 failed, 0 skipped. It also records successful build, format, package provenance, the canonical workflow, all eleven continuing barriers plus K4, and the actual-state negative control across two fresh probe processes. Its barrier record marks K12 validation as actually running and records one broker-owned command stopped explicitly.

Despite those green test results, the ledger correctly has `qualificationPassed: false`: the net10.0 command has exit code 0 and a green TRX, but `orphanedProcessGroupObserved: true` and termination `SIGTERM-group-after-leader-exit`. The qualifier treats that post-leader workload as a cleanup failure. This is an evidence/cleanup blocker, not evidence of a semantic test failure. Resolve what remained in the process group, then rerun or otherwise produce accepted cleanup evidence before freezing Linux. Do not summarize the consumer qualification as passed from the TRX counts alone.

## Corrected Gagamba runtime evidence

The earlier `linux/runtime` attempt is retained and must remain identified as superseded: Linux execution tests reported 26 passed / 16 skipped, and conformance reported 1 passed / 5 failed. The corrected record is `linux/runtime-corrected`; its source SHA is exactly `9fe127a...`, `usable` is true, cleanup is `Confirmed`, and mandatory skipped/unsupported counts are zero. It records Ubuntu 26.04.1 on WSL2, x86_64, .NET 10.0.12, cgroup v2, delegated mode, and platform `linux-cgroup-v2`.

The corrected non-root run records UID/GID 1000 and cgroup membership `/aaaw-m5-runtime-20261015-b`. Its exact results are Execution 20/0/0; Runtime 14/0/0; Linux lifecycle regressions 13 passed / 0 failed / 1 skipped; Conformance 6/0/0. The single skip is `E10_RootWslSiblingMigrationIsAnObservedContainmentLimit`, which is root-only by its name and is not silently counted as a pass. The separate root-only lifecycle retry reports 42 passed / 0 failed / 0 skipped. Runtime source and the Marang checkout show the intended commits and clean worktrees in read-only Git inspection. At audit time, the corrected runtime, consumer, and named Gagamba component cgroup parents were absent.

## Bounded interpretation

These records support the bounded Linux statement “tested on Ubuntu 26.04.1 under WSL2 with a delegated cgroup v2 subtree on this host,” once the Marang consumer cleanup gate passes. They do not establish a native Ubuntu VM profile. Conformance records `escapeResistant: Partial`, and the consumer ledger explicitly lists cgroup escape resistance, hostile multi-user isolation, and Gagamba owner-death cleanup as not exercised. Do not claim hostile isolation or general containment beyond the observed Gagamba lifecycle path.

The initial 26-pass/16-skip and 5-failure conformance run is a superseded environment/setup failure, not a product regression. Keep its log for history, but exclude it from corrected-run totals. No macOS evidence was inspected or produced.
