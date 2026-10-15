# Interim review: first native macOS attempt

Reviewed the preserved attempt-A evidence read-only. This is an interim result; the ARM64 rerun is still in progress, so final Mac qualification remains pending.

## Provenance and archive integrity

The retained run record identifies GitHub Actions run `38749640292`, event `push`, head `9da97d7b71eac3d6b0756fce41b690cde6ea6f6b`. The qualification record reports that the checkout began and ended clean at that exact commit. Its host record and `sw_vers` output agree on macOS 15.7.9, x86_64 (`osx-x64`); the test-only probe reports the same x64 architecture.

The outer `captured-artifacts.zip` SHA-256 is `c7cb4dab0c0b03888ca050662a3c9b5d9bb10697cb4c20b75bcd8cb95d6a40f0`. I checked its embedded manifest: all four payload entries are present and match their recorded SHA-256 and byte counts. The payload hashes are:

- `qualification.json`: `0c919600bf654d66338e81093fa6cbe0de16e7f2b0f834f26385fd7c2e82b987`
- `native-profile.json`: `a4fd4c41aa13c1ee25eaa798d8f9895e9a67a85f364ea788003416301c16aa47`
- `m5-net8.0.trx`: `83521d8e32b9b554750db3da4f15f84656a40540ecfcfb28f447cd9e4dbda38c`
- `m5-net10.0.trx`: `d341d9537d2966bbe682cc5e31a67c64484bb4e5b30087fade2ff5958cf5557a`

The runtime package identity in the run record pins `Gagamba.Runtime` and its provider packages to producer commit `9fe127a98f682514138f4f5e333e47bcbeea8fe5`, version `0.1.0-preview.4-aaaw-m5.1`; `Gagamba.Execution.MacOS` has SHA-256 `4e7edd2431395323b3bac53102ca4700e0783551f2fb54aec32fa6ca0b95b63c`. The exact unpublished producer source suite was not run on macOS, as the qualification record states.

## What the native probe established

The ordinary launch control wrote and verified its nonce marker, then returned a confirmed termination and completion. The captured-launch control returned `Failed` with `bounded macOS launchd capture is unavailable; no target dispatched`; the marker was absent before cleanup, discard succeeded, and the marker remained absent. The probe correctly records these narrow controls as passed while keeping `canonicalSupported` false. This is direct evidence of the M5 blocker, not an M5 pass.

## Consumer-suite failures have two separate causes

The unfiltered Marang runs reported net8.0: 173 passed, 28 failed, 1 skipped, 202 results; and net10.0: 195 passed, 51 failed, 1 skipped, 247 results. Qualification is false. In both TRX files, the skipped result is `Distinct_case_sensitive_git_roots_have_distinct_repository_identities`, with outcome `NotExecuted`. The TRX summary counter says `notexecuted=0`, while the qualifier’s parsed result list correctly retains the one skipped test; the corrected driver now records the result count explicitly.

Three net10.0 `MultiEffectProcessTests` fail in `LifecycleBrokerOwner.StopRootAsync` with `root terminal state was not confirmed: LaunchFailure/Confirmed`. Their root run uses `OutputCaptureOptions`; read together with the native probe’s pre-dispatch refusal, these are capture/lifecycle failures. They are distinct from the authority-suite failures.

The remaining 28 net8.0 and 48 net10.0 failures are consistent with the Intel Cedar dependency mismatch. A local inspection of the exact `CedarSharp` 1.0.0 package (`99cc3659e750f3d5187d7b8c35e84359811a7af1247b3673d893eafc679bf835`) found native assets for `linux-x64`, `win-x64`, and `osx-arm64`, with no `osx-x64` asset. `Penghou.Hufu.Cedar` 0.1.0-preview.3 depends on that exact CedarSharp version and records producer commit `3206c4447add6ef0821fb70f118b638598d9aac0`; its local package SHA-256 is `bbb67c997b158f3a75dd178a757bc14056c3c73261df1f8c0ddad84a6a31307f`. The failing Cedar test reports that the expected Hufu grant was not issued, and numerous dependent tests then fail because current authority denies the operation. These failures should not be attributed to captured launch.

The first-run qualification archive itself records the M1/runtime package hashes, but not CedarSharp or Hufu.Cedar hashes; those two dependency hashes above were independently read from the local NuGet cache. The corrected ARM64 driver is intended to include Cedar asset provenance in its run record.

## Qualification gate

The first run records canonical and partial state as unobserved/unverified, all K1–K12 barriers as unverified, and the actual-state control as unobserved/unverified. It does not convert the narrow probe success into qualification. No source defect is inferred from this x64 run: it is blocked both by expected macOS captured-launch refusal and by testing the Cedar release profile on unsupported macOS x64. The ARM64 rerun is pending and must be reviewed before a final Mac statement.
