# M5 evidence ledger

Status: **M5 incomplete: Linux frozen, Windows green, macOS blocked by captured launch**.

## M5.0: unchanged Windows baseline (observed)

Source: clean `7833bbe1e8d84e7b128e520ff96d7a4e9e9f94b7`, branch
`codex/aaaw-m5-marang`. Windows 11 build 26200, x64, SDK 10.0.401,
Microsoft.NETCore.App/ASP.NET runtimes 8.0.31 and 10.0.12.

`./eng/qualify-aaaw-m4.ps1 -ReuseQualifiedPackages` restored the pinned packages
and built with zero warnings/errors, then failed its format gate at
`AdaptivePlanAuthoring.cs` lines 282–283. This attempt did not run tests. The
failure log is retained; the overall qualifier is not reported as passed.

The tests were then executed separately on the same unchanged clean source:

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1'
$env:VSTEST_CONNECTION_TIMEOUT='180'
dotnet test tests/Marang.Tests/Marang.Tests.csproj -c Release --no-build --no-restore --logger trx --results-directory artifacts/aaaw-m5/windows/baseline-tests
```

| Framework | Passed | Failed | Skipped |
|---|---:|---:|---:|
| .NET 8 | 201 | 0 | 0 |
| .NET 10 | 245 | 0 | 0 |

The original M4 capture validator accepted all twelve native kill boundaries,
physical A/B/C/B2/C2 application counts of one, inventory count one, accepted
A/B2/C2, historical B/C, exact physical lineage and final canonical validation.
This is the historical Windows raw-process fixture, not evidence of an M5
Gagamba integration. Source stayed clean through capture.

Artifacts: [summary](windows/baseline-summary.json),
[detailed semantic capture](windows/baseline-m4-qualification.json),
[qualifier log](windows/m4-baseline.log),
[test log](windows/baseline-tests.log). TRX records are stored alongside them.

## Linux preflight (observed; canonical scenario not exercised)

Existing Ubuntu 26.04.1 LTS on WSL2, x86_64, kernel
6.6.114.1-microsoft-standard-WSL2, glibc 2.43, cgroup v2. Task-owned checkout
resides on the native Linux filesystem under `/home/gaga`, not the Windows
mount. Qualification account is uid/gid 1000. A task-local SDK 10.0.401 and
ASP.NET/Core runtimes 8.0.31 and 10.0.12 were installed without replacing the
system SDK. These are environment observations, not native runtime acceptance.

The first scratch regression build failed xUnit cancellation-token analyzers;
no behavioral test ran. Subsequent builds identified a missing exact 8.0.31
Linux apphost pack in the baseline xUnit executable project's restored graph.
The task-local SDK 8.0.426 and an explicit dependency-only restore of
`Microsoft.NETCore.App.Host.linux-x64` 8.0.31 supplied the missing prerequisite;
no product/test-project workaround was applied.

### D1: checkout-root identity collision (observed)

Classification: Marang platform assumption, not a Zhinu/Gagamba/Hufu defect.
The native regression creates two real sibling Git checkouts named `repo` and
`REPO`, proves the filesystem distinguishes them, commits different tracked
bytes, observes different file hashes, then requires distinct repository IDs.
On production source `7833bbe`, it failed specifically at the identity assertion:
0 passed / 1 failed / 0 skipped. [Log](linux/root-case-before.log),
[TRX](linux/root-case-before.trx), [SDK/runtime inventory](linux/reproducer-dotnet-info.txt).
The source was dirty only with the new reproducer test, not a production change.

Cause: unconditional uppercasing collapsed distinct Unix roots into the same
repository ID and lock path. The bounded correction preserves the historical
Windows uppercase hash and uses exact root spelling on Unix. Existing preview
Unix journals are not migrated; the first qualified Unix profile uses fresh
controlled checkouts/journals and canonical exact root spelling. Case-insensitive
Unix alias spelling remains unqualified until native macOS work. The identical
native regression after the correction passed 1/1 with no skips:
[log](linux/root-case-after.log), [TRX](linux/root-case-after.trx).
Windows full regression subsequently passed for the changed source, as recorded
below.

## M5 actual-state control development (observed, Windows)

The new fixture kills the workflow at K3 after persisted A validation, changes
the allowlisted project bytes externally, and starts two fresh reconciliation
processes. The first real lifecycle run correctly failed the test's precondition:
the deliberately reduced execution environment omitted Windows machine-wide
SDK/NuGet paths, so A had failed restore rather than validated. The assertions
were retained. After restoring the required Windows environment variables, the
fixture passed 1/1 through the pinned Gagamba runtime: persisted A stayed
ValidationPassed, actual bytes/state remained divergent and unchanged across
both restarts, B/C stayed Prepared without writes, and supervisor blocking was
visible. These development runs are not the final commit-bound qualification.

## Clean-source Windows M1–M5 regression (observed)

Commit `b1ccd71de6625e7ab851cfb83adf0662e8f2683f`, clean at both ends.
SDK 10.0.401, Windows x64 build 26200, runtime 8.0.31 / 10.0.12.
Restore, serial solution build (zero warnings/errors), and format verification
passed. Complete suites passed with the expected case-insensitive-filesystem
skip reported separately:

| Framework | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| .NET 8 | 201 | 0 | 1 | 202 |
| .NET 10 | 246 | 0 | 1 | 247 |

The skipped test is `Distinct_case_sensitive_git_roots_have_distinct_repository_identities`;
Windows temporary paths alias case-only directory names. Linux executed it.
All three M4/M5 native process tests passed, including all twelve barriers,
confirmed Gagamba stops and the actual-state divergence control across two
fresh processes. [Machine record](windows/final/qualification.json),
[reproduction driver](windows/final/reproduce.ps1),
[raw artifact archive](windows/final/captured-artifacts.zip).

## Linux consumer attempt A and SDK cleanup correction (observed)

Clean source `b1ccd71`: .NET 8 passed 202/202 and .NET 10 passed 247/247, with
zero skips/failures. All canonical, K4 and actual-state assertions verified.
The qualification correctly remained **failed**: its isolated .NET 10 process
group retained a child after the test leader exited. The driver signaled only
that owned group and the delegated parent was confirmed empty afterward.
[Original failed attempt and runtime records](linux/attempt-a-captured-artifacts.zip).

Classification: SDK/test-runner environment, not an AAAW semantic or foundation
defect. A minimal `dotnet run` console build reproduced the failure and captured
the still-live `Roslyn/bincore/VBCSCompiler` in its exact process group before
cleanup. The same control with `UseSharedCompilation=false` passed without any
post-leader process group. Before/after diagnostic records are retained in the
archive. The correction passes that MSBuild property to **all** qualification
SDK invocations, including legacy consumer tests, and records command PID/group.
The strict orphan/output/cleanup gates are unchanged. The full rerun passed below.
No C# production or test semantics changed after the Windows-qualified source.

Raw M5 evidence is stored without Git line-ending normalization so original file
hashes remain verifiable. Historical M1–M4 evidence is unchanged.

## Remaining gates

- Qualify the required native Mac captured-launch profile in its component workstream.
- Rerun Mac canonical/K4/twelve-barrier/actual-state qualification, relevant
  producer suites and affected regressions with immutable packages.
- Complete the all-platform semantic comparison after Mac passes. Current
  comparison explicitly records Mac as unexercised for these guarantees.

GitHub CLI authentication was verified from the authorized host context before
the hosted macOS attempts recorded below.

## M5.4: Linux evidence freeze (observed)

Clean source `46db7b6ffd5374cbe1aa43610f84975f1333638f`, branch
`codex/aaaw-m5-marang`, run `20261015T095508Z-78037adba5`, completed
2026-10-15 10:00:40 UTC. Source stayed clean and unchanged. Native Ubuntu
26.04.1 WSL2 x86_64, glibc 2.43, nonroot uid 1000, physical Linux checkout,
SDK 10.0.401 (8.0.426 also installed), selected runtimes 8.0.32 and 10.0.12.
The exact 8.0.31 apphost package is a build prerequisite, not the selected net8
runtime. Restore, build (zero warnings/errors), format and both complete suites
passed. No observed post-leader process group, truncated output or unconfirmed
cleanup occurred. The wrapper reported `populated 0`, `frozen 0`, removed its
own delegated cgroup parent and exited zero.

| Framework | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| .NET 8 | 202 | 0 | 0 | 202 |
| .NET 10 | 247 | 0 | 0 | 247 |

All twelve barriers, canonical A/B/C to approved A/B2/C2 lineage, actual S3/S4
replacement bases, preservation of A/inventory, no duplicate physical mutations,
K4 partial blocking and K12 live validation stop passed through the pinned native
Gagamba runtime. The tamper control retained persisted A validation while actual
bytes diverged; two fresh recovery processes blocked B/C without mutation.

Native producer source `9fe127a98f682514138f4f5e333e47bcbeea8fe5`
remained unchanged. Separate nonroot delegated runs passed Execution 20/0/0,
Runtime 14/0/0, LifecycleRegressions 13/0/1 (root-only E10 excluded), and strict
conformance 6/0/0. A separate root full Linux suite passed 42/0/0, including
E10 and fixed-path fixtures; those fixture cgroups were removed empty. Original
failed setup attempts and consumer attempt A remain preserved and are not
aggregated into the passing counts.

[Freeze manifest](linux/freeze.json), [complete machine record](linux/qualification.json),
[162 original payloads and hash manifest](linux/final-captured-artifacts.zip).
Archive SHA256: `d01cd0c0ac77df0f89f7bf6c0de7e2ee2c8b7a441070b74a054486bf1922454b`.

Supported claim: the exercised bounded M1–M4 repository workflow and M5 actual-state
control survive native process restarts on this WSL2 host with the recorded
delegated cgroup v2 lifecycle/capture profile. This does not qualify full Linux
VM deployment, owner-death cleanup, cgroup escape resistance, hostile multi-user
isolation, power loss, arbitrary concurrent writers or general exactly-once
external effects. Windows passed on the identical C# source/test content at
`b1ccd71`; subsequent changes affected qualification environment and evidence.
Linux evidence was captured and made reviewable before any macOS work began.

## Native macOS attempt A: Intel profile mismatch (observed)

Run [38749640292](https://github.com/jenolaszlo-sketch/marang/actions/runs/38749640292),
clean source `9da97d7b71eac3d6b0756fce41b690cde6ea6f6b` at both ends,
native macOS 15.7.9 x64, uid 501, image `20260824.0482.1`, SDK 10.0.401.
The native probe actually ran on .NET 10.0.13; the runtime inventory includes
8.0.32 and multiple installed patches. SDK version is not a runtime patch pin.
Restore, build, formatting, package validation and native probe compilation
passed. Every command had complete capture, confirmed process-group cleanup
and no observed orphan; the complete test commands correctly exited one.

| Framework | Passed | Failed | Skipped results | Total |
|---|---:|---:|---:|---:|
| .NET 8 | 173 | 28 | 1 | 202 |
| .NET 10 | 195 | 51 | 1 | 247 |

TRX `Counters.notExecuted` says zero despite one actual `NotExecuted`
UnitTestResult in each file. The table counts the actual skipped outcomes;
original TRXs are retained. The skip is the case-sensitive-root test on this
case-insensitive temporary filesystem, not a skipped native restart test.

Native ordinary launchd preparation, nonce-bearing launch, explicit termination
and terminal cleanup passed. A fresh captured preparation was accepted, but
`LaunchCaptured` refused with `bounded macOS launchd capture is unavailable; no
target dispatched`. Its marker stayed absent, discard succeeded, and no raw
fallback ran. The three M4/M5 process tests failed before the first barrier;
canonical/K4/twelve-barrier/actual-state coverage is **not exercised**.

Separately, authority-dependent tests returned `cedar.native-unavailable` or
downstream authority denials. The pinned CedarSharp 1.0.0 archive contains
`osx-arm64` and no `osx-x64` native asset. Hufu.Cedar's packaged release guidance
explicitly qualifies ARM64 Macs and excludes Intel qualification. Classification:
runner-selection/environment mismatch, not a Hufu implementation defect. The
corrected attempt uses the existing ARM64 profile without changing Hufu,
CedarSharp or their versions. [Dependency profile](macos/dependency-profile.json).

[Original machine record](macos/attempt-a/qualification.json),
[native controls](macos/attempt-a/native-profile.json),
[raw artifact archive](macos/attempt-a/captured-artifacts.zip),
[job log](macos/attempt-a/macos-job-a.log). The archive contains four verified
payloads; SHA256 `c7cb4dab0c0b03888ca050662a3c9b5d9bb10697cb4c20b75bcd8cb95d6a40f0`.
This attempt remains failed. It is not folded into a successful platform total.

Independent review 05 identified a narrow future-trigger gap and late scratch
cleanup reporting issue in the harness. Both were corrected in `b087472`;
original review and first-run evidence are preserved. The ARM64 retry at
`25f4e54` also records native asset hashes and actual skipped-result counts.

## Native macOS attempt B: supported ARM64 profile (observed)

Run [38751625577](https://github.com/jenolaszlo-sketch/marang/actions/runs/38751625577),
clean source `25f4e540c3a24af47f031daaacea34cf58cb080b` at both ends,
native macOS 15.7.9 ARM64, uid 501, image `20260907.0337.1`, SDK 10.0.401,
native probe runtime .NET 10.0.13. The runtime inventory includes .NET 8.0.32.
All restore, package checks, build, format and native profile control commands
passed. Full build had zero warnings/errors. Every command, including the
failing test command, had complete output capture, confirmed process-group
cleanup and no observed orphan. Scratch cleanup was recorded before probe JSON.

| Framework | Passed | Failed | Skipped results | Total |
|---|---:|---:|---:|---:|
| .NET 8 | 201 | 0 | 1 | 202 |
| .NET 10 | 243 | 3 | 1 | 247 |

The .NET 8 suite passes with its explicitly reviewed case-insensitive-filesystem
skip. .NET 10's only failed tests are the three `MultiEffectProcessTests`:
canonical revisions, K4 partial fixture and the new actual-state negative
control. They fail in the required captured lifecycle launch path before the
workflow barriers execute. No assertion, approval, authority check or barrier
was removed or filtered out.

All prior Cedar/authority failures disappear on the supported architecture.
The job records the actual `osx-arm64` dylib as 7,654,256 bytes, SHA256
`290fcce995f70cd963e024d991fe7f9e2806fde81add9749304ce222b904258b`, matching
the immutable CedarSharp 1.0.0 native asset in local-cache corroboration.
No Hufu/CedarSharp implementation or package version changed.

Ordinary launchd prepare, nonce-bearing launch, explicit stop and terminal
cleanup passed. Fresh captured preparation was accepted; actual captured launch
returned the same explicit `unavailable; no target dispatched` refusal. The
target marker stayed absent, discard succeeded and scratch cleanup passed.
This confirms the Gagamba profile gap independently of Cedar and leaves M5
**incomplete**. It does not establish the unexecuted Mac AAAW semantics.

[Machine record](macos/attempt-b/qualification.json),
[native controls](macos/attempt-b/native-profile.json),
[four verified raw payloads](macos/attempt-b/captured-artifacts.zip),
[job log](macos/attempt-b/macos-job-b.log). Archive SHA256:
`eb1a4e5934014aa252df72dc8b98263164cfef0d21cb7b9fb39d6adc0cd0dee4`.
The full pinned producer Mac source suite is separately **not exercised**:
the exact producer revision is unavailable remotely. Package controls must not
be presented as that full suite. [Remaining gates](macos/remaining-gates.md).

## Final independent review and disposition

[Review 07](reviews/07-final-review.md) independently verified the ARM64
archive/payload hashes, native Cedar asset bytes, clean source bindings, exact
test outcomes, capture refusal, cleanup, zero exercised Mac barriers and the
bounded comparison. It found no claim mismatch. Original reviews 01–06 remain
preserved. Review findings about workflow triggers and cleanup reporting were
corrected and exercised by attempt B.

M5 is **incomplete**, with the Mac captured-launch profile as the immediate
blocker and the exact producer Mac source suite separately unexercised. Both
Mac jobs correctly concluded failure; no green CI or all-platform equivalence
is claimed. No Hufu/Gagamba implementation was changed, no package was published to a registry,
and no M6 work was started. The qualification branch and evidence were pushed
to support the user-selected GitHub-hosted native runs.

Final evidence-only commits do not change the source qualified by the recorded
runs. `src/` and `tests/` are identical between Windows `b1ccd71`, Linux
`46db7b6` and supported Mac `25f4e54`. The only product change since M4 is the
isolated checkout-root identity correction (with native failing/passing proof),
plus formatter-only layout in `AdaptivePlanAuthoring.cs`. Native qualification
does not justify further foundation implementation inside this workstream.
