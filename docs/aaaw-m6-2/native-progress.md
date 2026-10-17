# Native evidence checkpoint: M6.2 remains in progress

Previous candidate source: `f216844f251c1ff7ade8beda602b696b4a288b74`.
Exact old writer: `5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29`.
No application code changed from `6f441fe` to `f216844`; the latter binds the
formatter to the LF policy already required by Git.

| Platform | Current evidence | Status |
| --- | --- | --- |
| macOS ARM64 | [sealed native report](evidence/macos/qualification.json), [manifest](evidence/macos/manifest.json), [CI run 38992225302](https://github.com/jenolaszlo-sketch/marang/actions/runs/38992225302) | Passed clean at exact `f216844`: .NET 8 305/0/1 and .NET 10 373/0/1; all M6.2 34/42 cases, eight cross-version process cases, original M6.1 cases and M5 semantic controls passed. |
| Windows x64 | Earlier `6f441fe` runtime suites passed but formatting failed. The first `f216844` attempt passed formatting and .NET 8, then had one environment-caused K4 fixture failure. | The D:-cache rerun was invalidated by host standby plus a separate transient marker-read sharing violation. A bounded marker-reader correction is prepared; new clean qualification is required. |
| WSL2 Linux x64 | Initial Ubuntu run was interrupted by OS I/O errors. Ubuntu-only restart and `/bin/true` succeeded before a fresh `f216844` run. | The user authorized pause and later resume after freeing space. The run ended clean and removed its cgroup, with all eight cross-version cases passed but one existing control failing its elapsed deadline across the pause. New uninterrupted qualification is required. |

Full closure requires successful clean Windows and Linux runs, the exact
three-platform package/hash matrix, preservation checks and final evidence
review. M6.2 is not complete. M6.3/M6.4 have not started.

## Retained attempts

- [Native `6f441fe` attempts](evidence/native-attempts-6f441fe.zip) retain the
  failed Windows formatting run and raw captures, the successful supplemental
  macOS run, the interrupted Linux artifacts, and the clean-checkout LF-policy
  reproduction. The manifest declares exclusions for duplicate source/Git
  storage and rebuildable output in the interrupted Linux subset. The complete
  interrupted clone is preserved separately on task D: storage with its hash.
- [Windows `f216844` host-space failure](evidence/windows-host-space-f216844.zip)
  retains the unsuccessful full run. The old writer's effect A NuGet restore
  could not write vulnerability metadata under the full C: user cache. B/C
  remained blocked and the requested K4 marker was never reached. This failure
  occurred before migration in that case; it is not a migration counterexample.

These environment incidents do not qualify resource-exhaustion behavior or
expand M6.2. No repair, backup/restore or resource-recovery implementation was
added. The original Windows checkout is left untouched after the user's
instruction; validation and evidence preparation continue in fresh task
checkouts on D:.

The final marker-reader correction is test-only: Windows sharing/lock errors
32/33 may retry for at most two seconds. Strict JSON, checkpoint/PID checks,
all original scenario assertions and every existing test timeout remain intact.
See the [retained corrections](evidence/qualification-corrections-f216844.zip)
and [source review](reviews/10-marker-and-interruption-review.md).
