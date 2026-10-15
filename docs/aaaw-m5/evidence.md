# M5 evidence ledger

Status: **Linux qualified and frozen for the bounded WSL2 profile; macOS pending**.

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

## Pending

- Perform GitHub-hosted macOS qualification after the Linux freeze below.
- Complete cross-platform comparison and final independent evidence review.

GitHub CLI authentication was verified from the authorized host context. No
macOS workflow or qualification has started.

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
