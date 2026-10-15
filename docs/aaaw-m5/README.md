# AAAW M5: cross-platform qualification

Status: **Linux qualified for the bounded WSL2 profile; macOS qualification in progress**.

M5 qualifies the existing M1–M4 bounded repository workflow. Linux evidence must
be frozen before macOS qualification starts. Windows remains the regression
baseline. Historical M1–M4 evidence is unchanged.

- [Plan and boundaries](plan.md)
- [Observed evidence and pending checks](evidence.md)
- [Windows baseline](windows/baseline-summary.json)
- [Frozen Linux evidence](linux/freeze.json)
- [Independent Linux freeze review](reviews/04-linux-freeze-review.md)
- [Native macOS attempt plan](macos/plan.md)

Completion requires the same causal scenario, all twelve restart boundaries,
actual repository-state negative controls, relevant native lifecycle tests,
both supported target frameworks, an independent review, commit-bound evidence,
and a clean worktree. A passing managed test or unavailable native prerequisite
does not establish qualification.
