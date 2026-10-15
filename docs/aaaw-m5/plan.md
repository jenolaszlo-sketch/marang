# M5 qualification plan

## Baseline

Start from clean Marang commit `7833bbe1e8d84e7b128e520ff96d7a4e9e9f94b7`
on branch `codex/aaaw-m5-marang`. The original M4 checkout is retained separately.
Create the M5 evidence directories before qualification and preserve historical
evidence.

## Ordered activities

1. Capture the unchanged Windows build, format gate, both regression suites and
   canonical M4 results. Retain failed attempts separately from passing runs.
2. Share scenario constants, restart boundaries and semantic assertions wherever
   practical. Confine native execution setup to test fixtures.
3. Reproduce Linux defects before changing production code. Keep a failing
   regression and classify each correction. Preserve Windows identity/history.
4. Run the canonical scenario through the existing Gagamba lifecycle boundary.
   A test-owned runtime must retain responsibility for subprocesses when the
   workflow process is deliberately killed. No raw-process fallback may qualify
   a missing runtime mechanism.
5. Add actual-state divergence after persisted local completion. Restart twice;
   verify unchanged tampered bytes, no descendant mutation, explicit blocking,
   retained lineage and authoritative revision.
6. Execute Linux M1–M5, native lifecycle/conformance, all applicable restart
   barriers, and both target frameworks. Freeze coherent Linux evidence.
7. Only then prepare and run GitHub-hosted native macOS qualification using the
   same semantic assertions and supported lifecycle provider.
8. Rerun Windows regression, compare platform guarantees and skips, obtain an
   independent review, retain its original findings and dispositions, and record
   the final clean source revision.

## Execution boundary

Gagamba runtime source and packages must be identified independently. The cached
`0.1.0-preview.4` runtime package is from producer commit
`5e6a30f4afc36568044fda2161ee84149823e2a3`; it does not contain the newer
orchestrator/capture boundary inspected at
`9fe127a98f682514138f4f5e333e47bcbeea8fe5`. Matching version strings are
insufficient. A local qualification package must carry its exact producer commit
and hash. Gagamba production source and the separate Linux M1 security workstream
are outside this implementation scope.

The runtime lifecycle API does not establish filesystem/network sandbox grants.
Record its negotiated capabilities and limitations. The M3/M4 repository protocol
continues to own approval, exact effect identity, actual-state reconciliation and
bounded writes. Do not infer survival of the test owner itself from deliberately
killing only the workflow process.

The shared test owner brokers nested git/dotnet invocations over a current-user
local pipe, fences admission before stop, confirms child cleanup, then stops the
workflow root. K12 uses a nonce challenge answered by an inline MSBuild task in
the actual validation invocation before VSTest, with release withheld. This
replaces the earlier immediate-after-start PID observation with actual executing
target code. An opaque runtime handle or a pending task alone is not liveness
evidence. The same scenario/barrier definitions and semantic assertions serve
all native profiles.

Exact input packages are retained as a hash-checked qualification fixture in
`eng/aaaw-m5/consumer-feed.zip`; the bootstrap and qualifier validate all nine
entries. This avoids rebuilding unrelated producers or publishing a package.
The separate Linux M1 security assembly is not part of this feed.

For Linux, use a native filesystem clone owned by an unprivileged account with
SDKs/runtimes 8 and 10. An operator can invoke the isolated delegation bootstrap:

```sh
sudo bash eng/aaaw-m5-linux-delegated.sh /absolute/clone /absolute/dotnet m5-unique-id python3 eng/qualify-aaaw-m5.py
```

The bootstrap refuses an existing parent, drops privileges before running the
qualifier, and removes only its own empty cgroup after checking cleanup. The
qualifier requires clean committed source at both ends, records each command,
and retains pass/fail/skip details and fresh per-barrier receipts. Windows and
macOS results must be recorded separately.

## Non-goals

No new workflow semantics, foundation redesign, generic compensation, arbitrary
effects, distributed exactly-once guarantee, hostile-code containment claim,
concurrent external-writer support, host power-loss claim or production-readiness
work. No Hufu or Gagamba implementation changes for fixture convenience.
