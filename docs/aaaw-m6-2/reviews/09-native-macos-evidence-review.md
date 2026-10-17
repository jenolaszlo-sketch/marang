# Native macOS evidence review

Reviewer: existing Luna independent-review agent. Scope: actual `f216844`
native macOS artifacts and the final evidence sealer. Overall M6.2 remains
pending Windows qualification and the user-paused Linux run.

The reviewer verified the following:

- Clean start/end at `f216844`; exact separate clean old writer `5dce00e` and
  its recorded binary hash; all M6.1 70/85 and M6.2 34/42 focused cases passed.
- Exact eight cross-version process cases passed. CI metadata identifies
  successful run `38992225302`, the correct head and successful `native-macos`
  job. The recorded host is macOS 15 ARM64.
- Every listed member hash in the outer and both nested capture ZIPs passed.
  K3/K4 legacy-fixture manifests bind the old source and their database/WAL
  file hashes. Original typed-cell snapshots before/after migration match.
- K4 catch-up retains partially applied/blocked B and prepared C blocked by B,
  declares no execution, and preserves the complete persistent-state snapshot.

No macOS evidence defect was found. The sealer was strengthened to bind the
exact Mac run, require ARM64, require the exact eight process-case names, and
verify each saved fixture file hash. It also requires both main stores in
legacy manifests and all original M6.1 focused cases. Historical M6.1-only
suite totals were initially mistaken for the integrated M6.2 totals during
review; the reviewer corrected that after inspecting actual native reports.
The integrated full-suite totals are 306/374, including new cases.

This review does not approve a three-platform completion claim while native
Windows/Linux qualification remains outstanding.
