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
no behavioral test ran. The test was corrected before rerunning. Root-case
identity is a suspected portability defect until the native assertion executes.

## Pending

- Native failing reproducer, classification and minimal correction, if confirmed.
- Shared scenario extraction and real Gagamba lifecycle integration.
- Actual-state negative controls and Linux full/native regression.
- Linux evidence freeze, followed by GitHub-hosted macOS qualification.
- Cross-platform comparison, Windows final regression and independent review.

GitHub CLI authentication was verified from the authorized host context. No
macOS workflow or qualification has started.
