# M6.2 source review 06 — source-freeze readiness

**Disposition: no source blocker remains in the bounded transition path; final format/build verification and all qualification runs are still pending.** This read-only review covers the current shared source and test harness. I did not run tests or builds.

## Reviewed corrections

The manifest/schema checks now validate exact ordered profile columns through `table_xinfo`, declared affinity, nullability, primary-key position, absence of hidden columns, profile constraints and exactly one row. They verify manifest cell storage classes before typed reads, and require every effect `record_format` cell to be SQLite integer 1. `AfterDdl` is placed between the record-tag `ALTER TABLE` and manifest creation; `BeforeCommit` is after full destination inspection. These changes address the findings in reviews 03–05.

The process refusal fingerprint now inventories `git ls-files --cached --others --exclude-standard -z`, includes file contents and symlink targets, and compares the Git head/status. It intentionally excludes ignored build files; claims should retain that scope. The old closed-writer fixture copies the original execution/Hufu main databases and WAL files with a hash manifest before migration or derived corruption. K3/K4 and migration-stop cases retain typed-cell/catalog snapshots for existing rows/schema, allowing the expected profile additions to be compared independently of the raw fixture copy.

The qualification wrapper is bounded to this one transition and invokes the unchanged M6.1 qualification, validates expected test counts, source cleanliness and fresh process directories, then seals captured evidence. Current-profile enforcement is explicitly scoped to qualification host phases that request profile 1: those phases call `RequireCurrentAsync` before the existing resume/initializer path. No claim is made that every Marang library consumer or every server entry point universally selects profile 1; library callers remain responsible for invoking the gate. The legacy-compatible reader remains a separate supported mode with its original semantics. This matches the approved M6.2 boundary.

## Remaining status

The new evidence helpers and source diffs have not yet been frozen in a clean source commit in this review context. Formatting/build checks and the exact-source old-writer bootstrap remain prerequisites. No application correctness, process atomicity, cross-version preservation or cross-platform qualification claim follows from this source review. Those claims require inspection of the fresh Windows, WSL2 Linux and macOS ARM64 artifacts.
