# AAAW M2 qualification — 2026-10-14

M2 is complete for the experimental repository read/report profile on Windows x64. A returning operator reconstructs intent, active result authority, pending discussion/approval, revision explanations and unresolved provider conditions from durable state. Conversation replay is unnecessary. Exact approval, conservative impact validation, atomic activation, preservation and fencing remain owned by M1.

| Area | Result | Evidence | Limits |
|---|---|---|---|
| Durable catch-up | Passed: one owner-state snapshot derives intent, execution, decisions, blockers and delta | `SupervisorCatchUpTests`; normal fresh-process restart; [captured catch-up](checkpoint-review.json) | Pinned experimental SQLite adapter; recent output bounded to 16 revisions/32 meaningful changes |
| Pending discussion | Passed: question, missing information, affected work and offered choices survive restart | Discussion kill case; `SupervisorContinuityStoreTests` | Concise decision context, not transcript; discussion never activates a revision |
| Pending approval | Passed: exact proposal and durable receipt reconstruct; commit checks current authority | Proposal/approval kill cases; recovery approval restart; revoked-grant test | Displayed UI is not approval; expired/stale decisions require fresh review |
| Revision drill-down | Passed: durable trigger/references and conservative per-branch disposition rationale | `DrillDownAsync`; [scenario result](continuity-result.json) | Reuse at activation is claimed only with exact reviewed context; later completion is not retroactive reuse |
| Three successive revisions | Passed: one workflow retains R1→R2→R3→R4 and inspectable lineage | All seven continuity process cases | Finite independent branches and one join, not arbitrary graphs |
| Persistent unaffected work | Passed: inventory keeps one execution ID through all four revisions; one invocation | `InventoryExecutionStableAcrossRevisions`, `InventoryInvocationCount=1` | This completed repository evidence unit proves preservation; no general exactly-once local-code claim |
| Unresolved delegation | Passed: real provider start/capture gap reconstructs an explicit unknown condition without respawn | Unresolved kill case; nested killed provider fixture; [recovery result](recovery-result.json) | Deterministic file-backed provider; arbitrary provider reconnectability remains deferred |
| Recovery actions | Passed: Wait, captured-operation Observe, separately reviewed Replace; errors/interruption remain evidence | `DelegationRecoveryActionsTests`; three recovery process cases | Replace permits bounded read/report duplication; old external work may still exist. No Retry, uncertain result adoption or condition-bound cancellation |
| Fresh authority | Passed: current Cedar/Hufu evaluation at proposal, exact approval and commit; revocation denies cutover | Real Hufu/SQLite recovery tests and process probes | Existing workflow-wide cancellation is a separate current-scope command; no distributed revocation/resource transaction claim |
| Restart continuity | Passed: six supervisor kill barriers, normal fresh restart, three unresolved-recovery kill barriers | Final .NET 10 suite; nine distinct killed/resumed process receipts in [machine evidence](qualification.json) | Windows process-kill tests; power loss and other operating systems are not qualified |
| Scenario A | Passed: changing report/review requirements; security and public API assessment; dependent report replaced | README/SECURITY/API source snapshots; pending-approval restart; actual Fuwen/Zhinu completion | Repository evidence assessment, not automated security certification or implementation of a general Implement→Build→Test graph |
| Scenario B | Passed: added Linux/Windows declared-target assessment branches; .NET 8/10 evidence; explicit reuse | R3 structural change and completed results; [repository evidence](repository-evidence.json) | No Linux/Windows build execution is asserted by these assessments |
| Scenario C | Passed: actual unknown-provider failure evidence drives corrected R4 conclusions; old conclusions remain superseded | Nested process killed after physical Start/before capture; R4 result and trigger | Evidence-driven deterministic replanning; no autonomous semantic planner or external rollback |

## Exact sources and dependency consumers

| Repository | Qualified source commit | Branch / use |
|---|---|---|
| Marang | `6a1840c7fb7eb8e757c8a1d0085d67b08f61e9bd` | `codex/aaaw-m2-marang`; M2 implementation |
| Marang M1 baseline | `71d54fd0757d9682907afde1884db3d9d5b0cbe1` | Existing qualified M1 composition |
| Penghou.Zhinu | `07e1807cc4b867dd4f527445aa108f56135d37db` | Unchanged `codex/aaaw-m1-zhinu`; core/SQLite `0.2.0-preview.4-aaaw-m1.3` |
| Penghou.Qingniao | `f08971df33916be184fd1bebb5940ba2bd660a49` | Unchanged `codex/aaaw-m1-qingniao`; abstractions/core `0.1.0-preview.5-aaaw-m1.4` |
| Penghou.Fuwen | `603b2e78d3f3ff33e2152641ab0ca016a120552e` | Existing `0.1.0-preview.13` Fuwen/adapter packages |
| Penghou.Hufu | `c0ef75afda4ba2562e3252d4e54cd536e37f428d` | Existing `0.1.0-preview.6` core/SQLite package metadata; no implementation changes |
| Penghou.Hufu.Cedar | `3206c4447add6ef0821fb70f118b638598d9aac0` | Existing `0.1.0-preview.3` package metadata; no implementation changes |
| Penghou.Hongxian | `325d8011d4731092ced0cb5acab6301444840ab0` | Existing `0.1.0-preview.5` packages |

The final unchanged implementation working tree passed and was then committed at the Marang source above. The evidence follow-up captures results/documentation without changing implementation. Original checkouts and M1 feature branches remain untouched. No Hufu or Gagamba repository implementation was edited.

## Qualification

The final reproduction command was `./eng/qualify-aaaw-m2.ps1 -ReuseQualifiedPackages`, using .NET SDK 10.0.401, .NET 8.0.31 and .NET 10.0.12. It verified all four original M1 package hashes and NuGet producer commits, restored the actual package-consumer graph, built the complete solution with zero warnings/errors, passed formatting verification, and ran the full consumer suite.

- .NET 8: **143 passed, 0 failed, 0 skipped**.
- .NET 10: **183 passed, 0 failed, 0 skipped**.
- M1 baseline: all 119/149 prior consumer cases remain included and pass, including every existing M1 kill/recovery probe.
- M2 additions: 24 semantic/conformance cases on each framework, plus ten .NET 10 process cases (normal restart, six supervisor barriers, three recovery barriers).
- Hongxian mirroring retained 33 session events in the captured example and remained idempotent on replay.
- The readable operator console successfully reconstructed a completed R4 workflow from its durable fixture.

The final TRX files are `artifacts/aaaw-m2/final-consumer/Laszlos_LAPTOP-KBD8D8JG_2026-10-14_16_49_46_net8.0.trx` and `...16_49_48_net10.0.trx`. [Machine evidence](qualification.json) records their counters/checksums, all P1–P15 mappings, exact package hashes, durable fixture paths, killed/resumed process identities and limits. Failed exploratory artifacts are retained locally and are excluded from these results. CI is configured to run the suite and upload M2 artifacts; remote CI and other operating systems have not been claimed as verified.

The process barriers cover pending discussion, pending proposal, exact approved proposal before commit, between revisions, after fencing/before replacement completion, unreviewed checkpoint changes, unknown delegation, approved recovery and activated/fenced recovery. The recovered scenario completes its current report with one continuing workflow and a single inventory invocation. The separate recovery scenario starts its unknown provider once, leaves that old physical operation intact, atomically fences its execution, and completes an authoritative local replacement report after fresh approval.

## Contracts, policy and remaining boundary

[ADR 0017](../decisions/0017-aaaw-m2-supervisor-continuity.md) assigns only decision meaning and projections to the host. The new durable contracts are revision intent bound to the Fuwen objective and immutable revision digest; version-bound discussion with explicit dismissal; actor-scoped operator checkpoint; exact reviewed proposal context; and recovery decisions bound to delegation/execution, condition, options, actor, authority and resulting revision. [Contract/migration guidance](contracts.md) and the [operator guide](operator.md) describe initialization, backfilling M1 intent, replay and safe actions.

M1 execution/revision/approval semantics are unchanged. The sole modification to an existing host journal method makes its decoder internal for transactional read projection. The M1 producer packages and recently graduated Siming, Workflow.Abstractions, Cangjie and IO foundations remain unchanged. M2 host code is non-packable, stays experimental/preview, and requires **no package publication, release tag or stable graduation**. Future incompatible continuity-schema changes require explicit migration and conformance qualification.

Safety qualification corrected internal-provider versus branch-run identity conflation, Hufu fixture version-stream publication, stale-discussion operability, exact-proposal receipt reconciliation and interrupted-observation evidence. Workflow-wide cancellation was kept outside the condition-bound recovery action set because its existing owner command lacks an atomic expected-condition precondition.

Deferred work remains arbitrary external-effect rollback, compensation, arbitrary provider reconnection, full Qingniao supervisory-state recovery, automatic uncertain-effect adoption, distributed transactions, competing-revision semantic merge, collaboration, portfolio supervision and a visual designer.

The evidence supports the mission claim **within this finite repository read/report profile**: Penghou preserves enough durable supervisory context for an operator to return after the qualified host restart points, understand current adaptive workflow state, inspect why revisions occurred, resolve outstanding conditions safely and continue without manually reconstructing the conversation.
