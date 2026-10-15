# M5 independent Linux freeze review

Review target: Linux consumer run `20261015T095508Z-78037adba5`, qualified Marang commit `46db7b6ffd5374cbe1aa43610f84975f1333638f`; evidence archive `docs/aaaw-m5/linux/final-captured-artifacts.zip` (SHA-256 `d01cd0c0ac77df0f89f7bf6c0de7e2ee2c8b7a441070b74a054486bf1922454b`). Read-only audit. No tests or builds were started by this reviewer.

## Result

I found no blocker to freezing the bounded Linux evidence. The archive manifest marks the qualification successful and lists 162 payload files. I verified every recorded payload SHA-256 and byte count; all 162 matched. The outer archive SHA-256 also matches the supplied value.

The run ledger binds a clean start and end to commit `46db7b6...`. It records 202/202 net8.0 and 247/247 net10.0 tests passed, with no failures or skips. Restore, build, formatting, package provenance, and both test commands passed. Every qualification command has `cleanupConfirmed: true`, `orphanedProcessGroupObserved: false`, complete output drain, and exit code 0. The `UseSharedCompilation=false` correction is recorded in the environment. The prior failing run remains preserved separately and was not included in this successful run.

The canonical M4/M5 path is verified with A/B/C/B2/C2 each physically applied once, active accepted effects A/B2/C2, inventory invoked once, valid state lineage, and preserved historical B/C. The separate K4 partial-effect case is verified. All twelve K1–K12 barrier entries have confirmed explicit root stops and verified receipts. At K12 the archived `ready.txt`, `challenge.txt`, and `proof.txt` agree with each other and the validation context: PID 12197 answered the fresh challenge before VSTest. The same barrier receipt shows the broker-owned validation command stopped with `ExplicitStop`, confirmed cleanup, and complete capture.

The negative control’s two archived observations confirm that actual allowlisted project bytes changed after A’s persisted `ValidationPassed` state. Both fresh reconciliation runs saw a state digest different from A’s expected state while A remained passed; the actual file hash remained the tampered hash, B/C stayed prepared, mutation remained blocked, and neither validator nor execution checkpoint ran. The two observations retained stable repository and tampered-byte hashes.

The delegated consumer cgroup parent is absent after completion, and the wrapper reported `populated 0` and `frozen 0` before removing it. The report should describe this as one Ubuntu 26.04.1 WSL2 x86_64 host using a delegated cgroup v2 subtree. It does not establish a native Ubuntu VM, hostile multi-user isolation, cgroup escape resistance, Gagamba owner-death cleanup, or general exactly-once behavior.

## Recommendation

Freeze Linux after the main evidence ledger records this exact run ID, qualified commit, archive SHA, runtime profile, per-framework counts, and the wrapper’s empty/unfrozen/removal receipt. I found no qualification blocker before macOS can start once that ledger update is committed. M5 as a whole remains incomplete until the same semantic scenario is qualified on macOS and the final cross-platform review is complete.
