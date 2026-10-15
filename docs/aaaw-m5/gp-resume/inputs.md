# M5 resumption after GP-M5

The blocked M5 history through `85773fe` is preserved unchanged. This resumption
uses the five exact Gagamba packages qualified by GP-M5; it does not change
Marang production code, canonical scenarios, restart barriers or assertions.

Gagamba implementation: `0c1f05e199588622a5987f95a9149374f5bd858b`.
Gagamba evidence/review commit: `2c8f4f7d7974510bac4baee9713acb6941b3ff09`.
Native producer run: [38818825522](https://github.com/jenolaszlo-sketch/gagamba/actions/runs/38818825522).
The producer worktree was clean and pushed before this consumer resumption.
GP-M5 independent reviews 04 and 05 passed for the bounded capture profile.

The candidate version is `0.1.0-preview.4-gp-m5.1`, with all five exact
hashes and repository commits in `eng/aaaw-m5-runtime-packages.json`.
The local feed snapshot retains the four original M1 packages byte for byte.
No NuGet package was published. No Hufu or restricted Linux implementation
changes are included in this batch.

The three Gagamba.Runtime package references and the producer-commit constant
change to these qualified inputs. The separate macOS prerequisite probe is
updated to expect valid bounded capture and retain invalid-contract refusal as
a negative control. It does not qualify AAAW itself. The unchanged M5 canonical,
K4 and actual-state tests remain authoritative. Hosted actions are pinned to
the immutable action revisions used by the GP-M5 run.

Required fresh matrix: Windows, delegated WSL2 Linux, and macOS ARM64, each
with the complete .NET 8 and .NET 10 suites. Qualification still requires all
twelve restart barriers, actual-state reconciliation, source identity and
cleanup evidence. Historical evidence remains under its original paths;
resumed results belong beneath this new evidence directory.
