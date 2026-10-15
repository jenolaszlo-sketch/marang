# Bounded semantic comparison: M5 incomplete

Windows and Ubuntu WSL2 passed the bounded canonical repository profile.
The supported macOS ARM64 consumer profile reaches an explicit native captured
launch refusal before the M4/M5 scenario starts. A passing ordinary launchd
control does not establish workflow restart equivalence.

| Guarantee in the canonical M4/M5 scenario | Windows | Ubuntu WSL2 | macOS ARM64 |
|---|---|---|---|
| Durable workflow/supervisor catch-up | PASS | PASS | Not exercised |
| Exact approval and revision continuity | PASS | PASS | Not exercised |
| Effect lineage recovered across restart | PASS | PASS | Not exercised |
| Valid A preserved without duplicate mutation | PASS | PASS | Not exercised |
| Stale B/C invalidated transitively | PASS | PASS | Not exercised |
| B2/C2 derived from actual S3/S4 repository bases | PASS | PASS | Not exercised |
| Replacement effects physically applied once | PASS | PASS | Not exercised |
| Accepted A/B2/C2 distinguished from historical B/C | PASS | PASS | Not exercised |
| Unrelated inventory preserved without replay | PASS | PASS | Not exercised |
| K4 partial/unresolved prerequisite blocks descendants | PASS | PASS | Not exercised |
| K12 actual running validation stopped with cleanup | PASS | PASS | Not exercised |
| Actual-state tamper blocks continuation across two fresh restarts | PASS | PASS | Not exercised |
| All twelve restart barriers verified | 12/12 | 12/12 | 0/12: launch blocked |
| Required native captured-launch profile | PASS | PASS | Unsupported by pinned provider |

Mac's passing M1–M3 and other unit tests remain useful consumer evidence. They
do not turn the missing canonical/barrier/negative-control rows into PASS.

| Platform / observed source | Framework | Passed | Failed | Skipped results | Total |
|---|---|---:|---:|---:|---:|
| Windows x64 / `b1ccd71` | .NET 8 | 201 | 0 | 1 | 202 |
| Windows x64 / `b1ccd71` | .NET 10 | 246 | 0 | 1 | 247 |
| Ubuntu WSL2 x64 / `46db7b6` | .NET 8 | 202 | 0 | 0 | 202 |
| Ubuntu WSL2 x64 / `46db7b6` | .NET 10 | 247 | 0 | 0 | 247 |
| macOS ARM64 / `25f4e54` | .NET 8 | 201 | 0 | 1 | 202 |
| macOS ARM64 / `25f4e54` | .NET 10 | 243 | 3 | 1 | 247 |

The single Windows/Mac skip is the test that requires a case-sensitive temporary
filesystem. Linux exercises it. TRX counters can omit that skipped result;
the table uses actual UnitTestResult outcomes. The three Mac failures are the
canonical, partial and actual-state process tests, not omitted tests.

All observed runs began and ended clean at their recorded revisions. Files
under `src/` and `tests/` are identical between these three observed sources;
the later changes affect qualification tools, a separate test-only native probe
and evidence. The pinned runtime packages are identical. The different observed
source revisions are reported explicitly, with no claim that the final evidence
commit itself was tested as a new application implementation.

Windows: SDK 10.0.401, runtimes 8.0.31/10.0.12. Linux: SDK 10.0.401,
runtime selection 8.0.32/10.0.12, native Ubuntu 26.04.1 WSL2 x86_64,
delegated cgroup v2, uid 1000. Mac: SDK 10.0.401, .NET 8.0.32 present and
native probe actually executing on .NET 10.0.13, macOS 15.7.9 ARM64, uid 501.
SDK installation does not force a particular installed runtime patch.

The failed Intel Mac attempt is preserved separately and excluded from the
supported-profile table: .NET 8 173/28/1 and .NET 10 195/51/1. It exposed the
CedarSharp ARM64-only Mac asset as well as the independent capture refusal.
The failed Linux consumer cleanup attempt and failed producer setup are also
preserved separately. No retries are aggregated into platform totals.

The native Linux producer evidence has separate scopes: Execution 20/0/0,
Runtime 14/0/0, delegated LifecycleRegressions 13/0/1 (root-only E10 skip),
strict conformance 6/0/0; separate root full Linux suite 42/0/0. Those are not
summed as unique tests. The complete exact producer Mac source suite remains
unexercised; the narrow package-based native controls are separately labeled.

Evidence supports Windows and this WSL2 repository-backed profile only. It
does not qualify arbitrary workflows/effects, distributed exactly-once behavior,
power loss, external concurrent writers, hostile isolation, Mac owner-death
cleanup or production readiness. Mac case-insensitive root alias spelling is
also unqualified; the bounded profile requires canonical exact root spelling.

See the [evidence ledger](evidence.md), [Linux freeze](linux/freeze.json),
[Windows machine record](windows/final/qualification.json),
[Mac machine record](macos/attempt-b/qualification.json) and
[remaining Mac gates](macos/remaining-gates.md).
