# M6.1 final evidence review

Date: 2026-10-16. Reviewed qualified application source
`e267f1c16b5cd7dc56ac33b65971be3d712fc673` and staged evidence under
`artifacts/aaaw-m6-1/final-materials`. The staged matrix reports all platforms
passed. This review confirms the source, test gates, package provenance, and
preservation records; it does not expand the M6.1 contract.

## Qualification and controls

The Windows, WSL2 Linux, and native macOS ARM64 records all identify source
`e267f1c16b5cd7dc56ac33b65971be3d712fc673`, with clean start/end observations.
Full test suites passed on both target frameworks. Windows and macOS each
record the one reviewed case-sensitive-filesystem skip; WSL2 Linux records no
skips. The M6.1 suites report 70 cases on .NET 8 and 85 on .NET 10 for each
platform. All twelve M5 barriers and the canonical, K4, and actual-state
controls verify on all three platforms.

The Windows SDK gate correction is a post-run assessment only. The host had
SDK 10.0.401 and .NET 8.0.31 / 10.0.12 runtimes; the clean-source run built and
tested both target frameworks. SDK 8 installation was not needed for that
build. The original Windows qualification report remains false and is
preserved unchanged. Its exact SHA-256 matches the report embedded in the
unchanged original raw capture archive. The derived report records the narrow
assessment change, original report hash, audit-script hash, validator hash,
application source identity, and explicitly says no tests were rerun. The
corrected validator only removes SDK 8 presence as a gate and records SDK 10
as the build SDK; both runtime requirements remain.

I independently checked the staged platform manifests against their files
and verified the inner capture-manifest hashes and byte counts: 202 captured
files for Windows, 202 for Linux, and 204 for macOS. Each platform is bound to
the same qualified source. All nine pinned packages match across the platform
manifests by package ID, version, source commit, and SHA-256. The macOS job
record identifies run `38891066363`, `macos-15`, and ARM64; its checkout,
.NET setup, qualification, and artifact-upload steps succeeded. The Linux
wrapper records a clean source export, successful qualification, and removed
cgroup parent.

The separate Windows full-C-volume incident remains explicitly failed and
preserved. The successful run uses task-specific NTFS temporary storage; the
incident note states that this is not a resource-exhaustion qualification or
application recovery feature. Failed first-candidate reports are also
preserved as history and are not counted as passing evidence.

## Scope and limits

The final matrix does not change the M6.1 bounded claim: SQLite physical and
specified logical checks refuse the tested damaged states before recovery
admission, while healthy pinned-profile states remain admissible. It is not a
claim of cryptographic tamper resistance, detection of a coherent rewrite or
rollback of all redundant evidence, generic Hufu-internal validation,
resource-exhaustion handling, repair, migration, or restore. The original M5
test sources, M5 qualification scripts, package locks, and pinned package
configuration remain unchanged by the application source diff.

No remaining qualification-evidence blocker was found. The Windows correction
is sufficiently transparent and reproducible for the staged report because
the raw false result is preserved, the application run and tests remain
unchanged, and the correction addresses only the invalid SDK-presence gate.
Final M6.1 evidence review: **PASS within the stated bounded scope**.

## Closure-document cross-check

The closure language in `docs/aaaw-m6-1/qualification.md`,
`docs/aaaw-m6-1/contract.md`, and `docs/aaaw-m6/plan.md` is accurate when read
with its stated bounded-profile qualifier: the supported Marang profile is
complete, while M6.2 through M6.4 remain planned. The qualification record
reports the failed first candidate and incidental C-volume incident as
failures, states that the Windows SDK correction is a post-run gate audit,
and avoids resource-exhaustion, tamper-resistance, Hufu-internal, migration,
or production-readiness claims. Those limits agree with the contract and
platform evidence.

The durable reviewer-facing artifacts are the [qualification record](../qualification.md),
[machine-readable matrix](../matrix.json), [Windows seal](../evidence/windows/manifest.json),
[Linux seal](../evidence/linux/manifest.json), [macOS seal](../evidence/macos/manifest.json),
and [M6.1 contract](../contract.md). The qualification record currently links
to `reviews/07-final-evidence-review.md`; copy this staged review to
`docs/aaaw-m6-1/reviews/07-final-evidence-review.md` before the closure commit
so that link resolves.
