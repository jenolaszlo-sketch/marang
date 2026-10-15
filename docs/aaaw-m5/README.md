# AAAW M5: cross-platform qualification

Status: **in progress; no cross-platform qualification claim yet**.

M5 qualifies the existing M1–M4 bounded repository workflow. Linux evidence must
be frozen before macOS qualification starts. Windows remains the regression
baseline. Historical M1–M4 evidence is unchanged.

- [Plan and boundaries](plan.md)
- [Observed evidence and pending checks](evidence.md)
- [Windows baseline](windows/baseline-summary.json)

Completion requires the same causal scenario, all twelve restart boundaries,
actual repository-state negative controls, relevant native lifecycle tests,
both supported target frameworks, an independent review, commit-bound evidence,
and a clean worktree. A passing managed test or unavailable native prerequisite
does not establish qualification.
