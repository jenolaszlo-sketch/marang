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

## Non-goals

No new workflow semantics, foundation redesign, generic compensation, arbitrary
effects, distributed exactly-once guarantee, hostile-code containment claim,
concurrent external-writer support, host power-loss claim or production-readiness
work. No Hufu or Gagamba implementation changes for fixture convenience.
