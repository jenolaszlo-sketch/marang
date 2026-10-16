# M6.1 corruption detection and fail-closed recovery

Status: **complete for the bounded repository profile** on Windows, WSL2 Linux
and macOS ARM64. Qualified application source:
[`e267f1c16b5cd7dc56ac33b65971be3d712fc673`](https://github.com/jenolaszlo-sketch/marang/commit/e267f1c16b5cd7dc56ac33b65971be3d712fc673).
The final evidence commit changes documentation and the Windows SDK inventory
gate only; application code, tests and consumer pins remain at this source.

Known-workflow recovery now inspects existing durable state before initializing
owners or entering execution. Missing, damaged, unsupported-shape or logically
inconsistent state refuses recovery with a bounded classification. Refusal
preserves uncertainty about external effects and does not reconstruct authority.
Healthy recovery retains the M5 contracts. This remains an experimental profile,
not a general production-readiness claim.

## Preserved M5 baseline

| Anchor | Immutable commit |
| --- | --- |
| Gagamba implementation | `0c1f05e199588622a5987f95a9149374f5bd858b` |
| Gagamba evidence | `2c8f4f7d7974510bac4baee9713acb6941b3ff09` |
| Marang qualified source | `b9df4b6fa9d1553b5f2df95dceebb699238ce935` |
| Marang final evidence | `5c3fb4b36e817b7abebd5e91cda8f0be4f7dd8e0` |

Original M5 tests, qualification scripts, package locks and historical evidence
are unchanged. The nine consumer package pins and their hashes match across all
three hosts; the pinned Gagamba runtime is `0.1.0-preview.4-gp-m5.1` at the
implementation anchor above. No Hufu or Gagamba implementation changed.

## Native results

Counts include the original regression suites and the new integrity cases.
Each cell is **passed / failed / skipped**.

| Platform | .NET 8 | .NET 10 | Original semantic controls |
| --- | --- | --- | --- |
| Windows x64 | 271 / 0 / 1 | 331 / 0 / 1 | canonical, K4, actual-state, 12 barriers passed |
| WSL2 Linux x64 | 272 / 0 / 0 | 332 / 0 / 0 | canonical, K4, actual-state, 12 barriers passed |
| macOS ARM64 | 271 / 0 / 1 | 331 / 0 / 1 | canonical, K4, actual-state, 12 barriers passed |

The only Windows/macOS skip is
`Distinct_case_sensitive_git_roots_have_distinct_repository_identities`.
Linux exercised and passed it. The canonical workflow covers eleven barriers;
the isolated partially applied K4 scenario completes the twelve. The
actual-state negative control remains separate. Valid completed mutations are
preserved without replay, stale dependent work is replaced from recovered
repository state, and unresolved states remain fail-closed.

Each native run started and ended clean at the exact qualified source. The
Windows post-run assessment below preserves those historical observations;
it does not claim a new run at the documentation/validator commit.
The [machine-readable matrix](matrix.json) links platform manifests, exact
package identities, host inventories, hashes and raw results:

- [Windows seal](evidence/windows/manifest.json).
- [Linux seal](evidence/linux/manifest.json), including delegated cgroup setup,
  empty/removed parent confirmation and source export identity.
- [macOS seal](evidence/macos/manifest.json), including job metadata/logs for
  [successful native run 38891066363](https://github.com/jenolaszlo-sketch/marang/actions/runs/38891066363).

All captured-file hashes were verified: 202 Windows, 202 Linux and 204 macOS
members. Platform manifests seal the raw ZIPs without repacking them. Raw
archives include TRX results, process receipts and scenario observations.

## Integrity and preservation evidence

There are **70 M6.1 cases on .NET 8 and 85 on .NET 10**, all passed on each
qualified host. The native owned-process compositions run on .NET 10; .NET 8
executes the complete supported store/consumer regression suite.

| Test class | Cases | Contract exercised |
| --- | --- | --- |
| `RepositoryEffectJournalIntegrityTests` | 21 | Projection/document identity, event digest/JSON/lifecycle, cumulative authority, missing history/projection, global sequence/allocator continuity, interleaved healthy histories and M5 effect states |
| `RepositoryRecoveryInspectorTests` | 37 | Physical and logical damage, pinned schema, revision/proposal/graph bindings, approvals, execution and supervisor continuity, provider index/attempt bindings, healthy pending/in-flight/committed/uncertain states |
| `RepositoryRecoveryAdmissionTests` | 12 | Required store/schema admission, damaged execution/evidence, empty-history allocator contradiction and cancellation propagation |
| `RepositoryRecoveryEntryProcessTests` | 6 | Two consecutive fresh owned recovery processes for each isolated missing, empty, malformed, truncated, revision-JSON and event-digest damage case |
| `RepositoryRecoveryInspectorProcessTests` | 9 | Fresh-process inspection of committed live-WAL state: three healthy controls and six corruption refusals |

Admission opens existing SQLite storage read-only in a consistent transaction;
it invokes no initializer or migration. Checks include physical integrity,
foreign keys, pinned schema marker 6 and declared document/digest/cross-record
invariants. Journal reads verify existing history before returning it; extending
an existing effect verifies its history before appending.

The recovery-entry refusals occur before workflow/checkout binding creation,
owner initialization, provider dispatch, validators, checkpoints, authority
publication or repository mutation. Repeated attempts refuse again. Gagamba
receipts establish owned-root cleanup and complete captured output. Fresh
inspector processes preserve the main database and existing live WAL bytes
exactly, including when refusing logical corruption. Healthy controls read
committed WAL content through the same boundary.

SQLite can create a zero-length WAL sidecar when opening a WAL-mode main file
whose WAL is absent. The tests record `absent-to-empty-created` separately,
require no frames, and retain exact main-file preservation. Existing WAL content
is byte-identical. Shared-memory coordination bytes are not a preservation
claim. Missing databases stay missing; no replacement store is created.

## Retained failures and qualification corrections

The [counterexample record](counterexamples.md) preserves development failures,
positive controls and source-review corrections. Candidate `4182968` was
rejected on all three platforms because the new journal check incorrectly
required SHA-256 for an existing opaque validation-output identity. The fix
follows the old writer contract; the old M5 regression test is unchanged.
Sequence-gap and empty-history counterexamples also remain archived. The
rejected reports are not relabeled as successful runs.

The first Windows attempt at `e267f1c` was invalidated when the host C: volume
filled. Its false report, failing TRXs and captured artifacts remain in
[host-space-failure.zip](evidence/windows/host-space-failure.zip). This was an
incidental qualification-host failure, not a controlled resource-exhaustion
experiment or evidence for M6.3. No recovery feature was added in response.
The same source was rerun successfully with only process-scoped `TEMP`/`TMP`
and child-process scratch checkouts on fixed NTFS
`D:\CodexQualification\MarangM6.1\e267f1c\temp`. The
[host profile](evidence/windows/host-temp-profile.json) records the change;
user/global environment settings were unchanged.

That completed Windows rerun passed every test, process, semantic, package and
source gate. The new qualifier nevertheless returned false because it also
required an installed SDK 8. The existing Windows M5 profile uses SDK 10 to
build both targets, with runtimes 8 and 10 installed. SDK 8 installation adds
no evidence to the completed builds. The correction requires SDK 10 plus both
runtimes and continues reporting `hasSdk8: false` accurately. This does not
claim Windows SDK 8 compiler qualification.

The [original false report](evidence/windows/original-qualification.json) and
its [raw archive](evidence/windows/windows-raw-evidence.zip) remain byte-for-byte
unchanged. The archive contains the original false report. The separate
[audited assessment](evidence/windows/qualification.json) records exactly the
three corrected result fields, original report SHA-256, corrected validator
SHA-256, audit-script SHA-256 and qualified application source. All other gates
were required to be true before reassessment; no application tests were
rerun or relabeled. The clean-source fields describe the original run.

Reproduce this metadata audit from a checkout whose application source and
consumer pins match `e267f1c` (the output is a separate derived assessment):

```text
python docs/aaaw-m6-1/evidence/windows/audit-windows-sdk-gate.py --original docs/aaaw-m6-1/evidence/windows/original-qualification.json --validator docs/aaaw-m6-1/evidence/windows/corrected-validator.py --source-root . --output assessment.json
```

Linux and macOS reports passed directly without this Windows-only correction.
They retain their actual SDK/runtime inventories. All original and derived
reports are sealed separately; the final matrix uses the explicit Windows
audited assessment.

## Review and boundaries

[Reviews 01–07](reviews/07-final-evidence-review.md) retain the design, source,
integration and final evidence audit in order. The
[contract](contract.md) and [operator guidance](operator-guidance.md) define
admission, refusal classifications and host obligations.

This is a dedicated pinned-profile trust unit under one controlled recovery
owner. Inspection is not authorization, proof of repository freshness, or
protection against arbitrary concurrent writers after inspection. Current
approval, authority, fencing and actual-state checks still apply. Additional
authority-store inspection checks physical SQLite integrity only; it does not
interpret or change Hufu internals.

Detection covers declared inconsistencies. It does not prove arbitrary
valid-but-false payloads or detect a coherent rollback/rewrite of every
redundant record and digest. There is no independent trusted commit-tip/tamper
anchor or raw-WAL recovery algorithm. Unsupported schema markers refuse;
no upgrade compatibility or migration is introduced.

No repair, migration, backup/restore, resource-exhaustion handling, soak,
generic compensation, new workflow semantics or foundation graduation was
added. M6.2, M6.3 and M6.4 remain planned and have not started.
