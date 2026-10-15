# M5 evidence ledger

Status: **qualification in progress**. Observations below do not establish Linux
or macOS canonical qualification.

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
The strict orphan/output/cleanup gates are unchanged. Full Linux rerun pending.
No C# production or test semantics changed after the Windows-qualified source.

Raw M5 evidence is stored without Git line-ending normalization so original file
hashes remain verifiable. Historical M1–M4 evidence is unchanged.

## Pending

- Complete the strict Linux rerun after the reproduced SDK environment fix.
- Freeze coherent Linux evidence, then perform GitHub-hosted macOS qualification.
- Complete cross-platform comparison and final independent evidence review.

GitHub CLI authentication was verified from the authorized host context. No
macOS workflow or qualification has started.
