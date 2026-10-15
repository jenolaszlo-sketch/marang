# Canonical-state wording clarification

This addendum clarifies the phrase “active-graph mutation blocking” in
`02-final-semantic-review.md`; it does not change that review or its verdict.

The canonical positive scenario finishes with the recovered valid graph
validated and unblocked: A, B2, and C2 are accepted, the workflow and local
validation pass, and each physical effect is applied once. The raw canonical
result records `activeGraphMutationBlocked: false`. In the shared semantic
summary, the assertion named `activeGraphMutationBlocked` is true because the
validator confirmed that expected false state. The earlier wording could be
read as saying the positive graph itself remained blocked; that was not the
intended meaning.

Blocking is the expected behavior in the distinct negative controls. K4 keeps
its partially applied prerequisite and named-blocks the dependent effect
without executing it. The actual-state-divergence control classifies the
project as stale and blocks mutation, validator, and checkpoint execution
across both reconciliation runs. Those controls remain fail-closed while the
positive canonical graph completes successfully.

I checked `qualification.md`, `matrix.json`, the Windows semantic report and
raw canonical result, the Linux qualification and evidence manifest, and the
macOS qualification/TRX/profile evidence. The platform counts in the
qualification report match the raw records: Windows and macOS each have 201
passed / 0 failed / 1 reviewed skip on .NET 8 and 246 / 0 / 1 on .NET 10;
Linux has 202 / 0 / 0 and 247 / 0 / 0. Native macOS process/barrier proof is
from the .NET 10 composition; .NET 8 is the full regression run. No factual
correction to those counts or this bounded process-proof statement is needed.

The clarification leaves the independent review disposition unchanged: the
fresh matrix satisfies the bounded M5 qualification gates, with the recorded
platform skip and excluded threat/failure cases.
