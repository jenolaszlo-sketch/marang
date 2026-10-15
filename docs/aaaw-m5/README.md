# AAAW M5: cross-platform qualification

Status: **M5 incomplete: Windows green, bounded WSL2 Linux qualified, macOS blocked**.

M5 qualifies the existing M1–M4 bounded repository workflow. Linux evidence must
be frozen before macOS qualification starts. Windows remains the regression
baseline. Historical M1–M4 evidence is unchanged.

- [Plan and boundaries](plan.md)
- [Observed evidence and pending checks](evidence.md)
- [Windows baseline](windows/baseline-summary.json)
- [Frozen Linux evidence](linux/freeze.json)
- [Independent Linux freeze review](reviews/04-linux-freeze-review.md)
- [Native macOS attempt plan](macos/plan.md)
- [Cross-platform comparison and exact counts](comparison.md)
- [Remaining macOS qualification gates](macos/remaining-gates.md)
- [Final independent review](reviews/07-final-review.md)

The supported ARM64 Mac retry passes all tests except the three M4/M5 process
scenarios (plus one reviewed filesystem skip per framework). The current
Gagamba Mac provider explicitly refuses bounded captured launch before target
dispatch, so the canonical scenario, twelve barriers and actual-state control
remain unexercised there. M5 cannot claim three-platform equivalence. No Hufu or
Gagamba implementation was changed to work around that refusal.

Completion requires the same causal scenario, all twelve restart boundaries,
actual repository-state negative controls, relevant native lifecycle tests,
both supported target frameworks, an independent review, commit-bound evidence,
and a clean worktree. A passing managed test or unavailable native prerequisite
does not establish qualification.
