# Final independent review of M5 evidence

Reviewed the Windows, Linux and both macOS evidence records and the final README, evidence ledger, comparison and remaining-gates document. This review did not run builds or SDK tests.

## Independent checks

- Attempt B is tied to run `38751625577`, source `25f4e540c3a24af47f031daaacea34cf58cb080b`, with clean start and end states. Its outer captured-artifacts archive SHA-256 is `eb1a4e5934014aa252df72dc8b98263164cfef0d21cb7b9fb39d6adc0cd0dee4`; all four manifest payload hashes and byte counts match the archived TRX, probe and qualification JSON files.
- The machine record reports native macOS 15.7.9 ARM64, .NET SDK 10.0.401 and probe runtime .NET 10.0.13. The ordinary nonce launch, explicit termination and completion passed. Captured launch refused before target dispatch, its nonce marker remained absent, preparation discard succeeded, scratch cleanup was confirmed, and the probe reported `canonicalSupported=false`.
- Attempt B records .NET 8 as 201 passed / 0 failed / 1 skipped / 202 results and .NET 10 as 243 / 3 / 1 / 247. The skipped case is the case-sensitive-root test and is explicitly counted from result rows despite the TRX counter’s `notExecuted=0`. The only failed tests are the three recorded `MultiEffectProcessTests`; their captured lifecycle root launch fails before the canonical, K4 partial and actual-state scenarios reach their barriers.
- The recorded `osx-arm64` CedarSharp 1.0.0 dylib hash and length (`290fcce9…b904258b`, 7,654,256 bytes) exactly match the local NuGet cache asset. The package hash matches `dependency-profile.json`. Attempt A’s Intel failure is kept separate and is not combined with ARM64 totals.
- The Gagamba runtime/provider package identities remain pinned to source commit `9fe127a98f682514138f4f5e333e47bcbeea8fe5`, version `0.1.0-preview.4-aaaw-m5.1`. I confirmed there are no `src/` or `tests/` diffs between the recorded Windows `b1ccd71`, Linux `46db7b6` and macOS `25f4e54` commits.
- Attempt B’s machine record leaves canonical and partial scenarios, K1–K12, and the actual-state control unobserved or unverified, and `qualificationPassed` is false. The exact unpublished producer Mac source suite remains unexercised.

## Documentation assessment

The README, evidence ledger, comparison and remaining-gates document agree with the machine records. The comparison marks Mac scenario rows unexercised and describes the captured-launch limitation. It keeps the Intel Cedar package mismatch distinct from the ARM64 Gagamba captured-launch refusal, and distinguishes the bounded WSL2 result from a full Linux VM or broader isolation claims. The remaining gates require the native capture path, exact producer source suite, unchanged canonical scenario, all twelve barriers, actual-state controls, and independent review before a Mac qualification claim.

I found no evidence mismatch or overclaim blocking documentation commit/push. M5 remains intentionally incomplete: Windows and the recorded bounded WSL2 profile passed; macOS ARM64 reached the native probe but cannot launch the captured workflow root with the pinned provider. This review does not qualify the missing Mac semantics or the producer’s unrun Mac source suite.
