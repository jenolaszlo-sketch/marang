# GP-M5 resumption — independent semantic evidence review

Date: 2026-10-16. Candidate: Marang commit `b9df4b6fa9d1553b5f2df95dceebb699238ce935`. This review assesses the fresh Windows, WSL2 Linux, and native macOS ARM64 evidence for the unchanged M5 scenarios using the GP-M5 runtime packages. It is bounded to the recorded inputs and tests; it is not a general security certification.

## Disposition

**The fresh M5 matrix passes with the documented platform-specific test skip. No blocking qualification gate remains.** Each platform records the exact candidate commit as clean and unchanged at start and end. Each run validates package provenance, canonical M4 workflow, partial-state K4 behavior, the actual-state negative control, and all twelve restart barriers. The Windows and macOS suites each skip only the case-sensitive sibling-root portability test because those filesystems cannot represent the distinction; Linux runs it successfully.

The evidence supports the tested Marang M5 reconciliation and lifecycle path, with the qualified Gagamba producer, on these hosts. The macOS native process proof is a .NET 10 process-test result; its .NET 8 run supplies the regression suite. This review does not extend to owner-death cleanup, setsid escape containment, hostile isolation, power loss, or general exactly-once effects.

## Exact source and package inputs

All three qualification records identify source `b9df4b6fa9d1553b5f2df95dceebb699238ce935`; Windows and macOS recorded a clean Git worktree at start and end, and Linux used a fresh Git clone from a bundle, recording clean and unchanged start/end content. The Linux evidence manifest records the source bundle SHA256 and an unchanged/clean result.

The five Gagamba package inputs are version `0.1.0-preview.4-gp-m5.1`, all built from producer commit `0c1f05e199588622a5987f95a9149374f5bd858b`. Their hashes, recorded in each qualification’s package provenance, are:

| Package | SHA256 |
| --- | --- |
| Gagamba.Execution | `8a93d8fbcfb43d695ea8d65db3603b98e2e9c00750a662829c6374c0a8be4c0c` |
| Gagamba.Execution.Linux | `7fa53482b389a12630d649ba43cafb09b6a2f61d7a45fd6b77df63a5379df1cc` |
| Gagamba.Execution.MacOS | `912d8033d66feb9fe45c50fa0e2aa862ee77790b8d62f8dc86d65b2138be7ac8` |
| Gagamba.Execution.Windows | `1d57fd0bbf4ae6830be49fe6c4c4339a26a6349fbd5308d39e167c0d7decef00` |
| Gagamba.Runtime | `b141f8775185864f491110c0f22684a58a7bbd02d0481ae74776a0961b954311` |

The four M1 package payloads remain the pinned inputs recorded by the M1 lock and Linux evidence. No package was published as part of this resumption.

## Matrix results

| Host | .NET 8 | .NET 10 | Semantic controls and source state |
| --- | --- | --- | --- |
| Windows x64 | 201 passed, 1 reviewed filesystem skip | 246 passed, 1 reviewed filesystem skip | Canonical M4, K4, actual-state control, 12/12 barriers; clean exact source start/end |
| WSL2 Linux x64, delegated cgroup v2 | 202 passed, 0 skipped | 247 passed, 0 skipped | Canonical M4, K4, actual-state control, 12/12 barriers; cgroup parent removed; clean fresh clone unchanged |
| macOS ARM64 | 201 passed, 1 reviewed filesystem skip | 246 passed, 1 reviewed filesystem skip | Canonical M4, K4, actual-state control, 12/12 barriers; native profile controls passed; clean exact source start/end |

The Windows and macOS skip is `RepositoryPortabilityTests.Distinct_case_sensitive_git_roots_have_distinct_repository_identities`. Linux exercised that test with native case-sensitive filesystem semantics. The skipped-test dispositions are explicit in the platform records, and the mandatory baseline counts and semantic gates still pass.

The canonical workflow confirms its expected fixture source and recovery sequence, all five logical effects exactly once, and active-graph mutation blocking. K4 confirms partial application and blocks its dependent effect without replay. The M5 actual-state negative control observes unexpected actual state, keeps state and project bytes stable across both observations, blocks mutation, validator, and checkpoint calls, and classifies the project as stale. K1–K12 each have verified barrier context, used marker, and lifecycle stop receipt; K12 records the expected running-state validation and explicit stop.

## Native macOS profile boundary

The macOS ARM64 profile probe separately passed ordinary launch/termination, bounded-capture positive control with exact stdout/stderr and natural exit, and invalid/over-ceiling pre-dispatch refusal controls with markers absent both before discard and after runtime disposal. It recorded provider disposal and scratch cleanup. The probe explicitly labels these as package/profile controls and says they do not establish Marang M5 semantics; the semantic claim above comes from the actual Marang matrix and canonical process evidence.

The provider’s recorded capability profile remains limited: unit termination and recursive membership are partial; owner-death cleanup and escape resistance are absent; kernel-owned lifecycle is absent. Qualification therefore does not claim same-process-group containment against `setsid`, owner-death cleanup, hostile isolation, power-loss recovery, or general exactly-once external effects.

## Evidence reviewed

- Windows: `artifacts/aaaw-m5/windows-gp-m5/qualification.json` and `semantic-qualification.json`.
- Linux: `artifacts/aaaw-m5/linux-gp-m5/qualification.json` and `evidence-manifest.json`; raw process and TRX evidence is preserved in the recorded qualification archive.
- macOS ARM64: `artifacts/aaaw-m5/macos-gp-m5/aaaw-m5-macos-b9df4b6fa9d1553b5f2df95dceebb699238ce935/qualification.json`, `native-profile.json`, and both framework TRX files.

The review found no remaining blocker to the bounded M5 qualification claim above. The excluded threat and failure cases remain outside this claim.
