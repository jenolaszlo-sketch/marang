# M5 independent cleanup-correction review (interim)

Review target: qualification-only correction at `46db7b6ffd5374cbe1aa43610f84975f1333638f`, with semantic source at `b1ccd71de6625e7ab851cfb83adf0662e8f2683f`. Read-only review of the script diff, the archived failed attempt, and the before/after reproducer records. No SDK tests, builds, or qualification runs were started by this reviewer.

## Assessment

The cleanup correction is supported by a direct, bounded reproducer and preserves the strict gate. The archived failed attempt is still recorded as `qualification: false`; its all-green TRX counts are not upgraded into a passing qualification.

`linux-cleanup-diagnostic/before.json` records a successful `dotnet run` leader exit but an observed leftover process in the same process group: `/home/gaga/.../Roslyn/bincore/VBCSCompiler`, PID 455, PPID 331, PGID/session 431. The runner then sent SIGTERM and confirmed the group empty. Its `ok` result remains false because an orphan was observed. The same console reproducer with `UseSharedCompilation=false` is recorded as `ok: true`, with no pre-cleanup process snapshots; `compiler.json` records the same successful control. The archived manifest covers 160 payload files and all 160 SHA-256 and byte-count checks matched during this review.

The correction at `eng/qualify-aaaw-m5.py:641-645, 672-679, 705, 723` puts `UseSharedCompilation=false` into the explicit environment for qualification commands, including both full consumer test invocations, records the command PID/process-group ID, and records the applied setting. `eng/aaaw-m5-linux-delegated.sh` also forces that setting in its child environment. This addresses the reproduced Roslyn compiler-server lifetime. The script still marks a command unsuccessful if it ever observes a post-leader process group, even if later cleanup empties it; no orphan, capture, or cleanup condition was relaxed.

The correction commit changes no files under `src` or `tests` relative to `b1ccd71`; it changes qualification tooling and evidence. The Windows final record remains explicitly bound to `b1ccd71`, so this cleanup-only correction does not retroactively alter its source qualification.

## Linux freeze gate

The new correction explains the prior cleanup failure, but does not itself qualify Linux. At review time, the full Linux rerun at `46db7b6...` is still pending. Freeze Linux only after its ledger reports `qualificationPassed: true` with clean source at both ends, successful net8.0/net10.0 consumer runs, no unexplained skips, no post-leader process group, complete output capture, confirmed cleanup, all restart barriers and actual-state controls verified, and the delegated cgroup parent confirmed empty and removed. Keep the evidence bounded to Ubuntu under WSL2 with the observed delegated cgroup v2 profile on this host; it does not establish a native VM or hostile-isolation guarantee.

No macOS evidence was reviewed or produced.
