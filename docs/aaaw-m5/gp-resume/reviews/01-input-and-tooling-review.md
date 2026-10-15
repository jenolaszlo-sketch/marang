# GP-M5 package resume — independent input and tooling review

Date: 2026-10-16. Review baseline: Marang M5 commit `85773fe5704c84702742c2b5e4f17fd229a914de`. Review scope: the uncommitted, tool/input-only candidate that repoints Marang M5 to the separately qualified Gagamba GP-M5 package set at `0c1f05e199588622a5987f95a9149374f5bd858b`, including the native macOS profile preflight changes. This is not a Marang M5 qualification result; the fresh three-platform, two-TFM matrix remains required.

## Review result

**Input and tooling changes are acceptable for the fresh matrix. No M5 semantic oracles were weakened.** The canonical C# implementation and test bodies are unchanged. The two test project edits change only the Gagamba package version. The macOS preflight is explicitly a package/profile control and records that it does not establish Marang M5 semantics.

## Package and source identity checks

The runtime lock lists all five Gagamba packages at version `0.1.0-preview.4-gp-m5.1`, commit `0c1f05e199588622a5987f95a9149374f5bd858b`, with the GP-M5 candidate SHA256 values. I opened the feed ZIP and independently hashed each matching NUPKG; all five values match the lock.

The four existing M1 NUPKG payloads in the feed are byte-for-byte unchanged from baseline `85773fe` (their payload SHA256 values also match the original feed). The feed replaces only the five old Gagamba entries with the five GP-M5 entries. The expected producer commit constant in `eng/qualify-aaaw-m5.py` changes to the same exact Gagamba commit; the validator logic, baseline minimum counts, and mandatory-barrier checks are unchanged.

The macOS workflow changes only the floating checkout/setup/upload action tags to the immutable action SHAs used by the GP-M5 native workflow. Its platform, runtime matrix, test commands, required semantic checks, evidence upload behavior, and qualification gate are unchanged. The macOS qualifier’s `notExercised` note now correctly distinguishes the separately qualified producer from the Marang semantic run and states that profile preflight does not prove M5.

## macOS preflight review

The revised test-only probe retains its ordinary launch/terminate positive control. It adds a valid captured finite command that checks NaturalExit(0), Complete capture, exact stdout and stderr bytes, and a fresh marker. Its negative control submits invalid and over-ceiling capture budgets, checks both target markers before discard and again after provider disposal, then confirms preparation discard and provider/scratch cleanup. Any unexpected captured start is tracked for termination and fails the probe. The output schema labels the result as profile controls only and leaves `canonicalQualification` as not performed.

The probe uses the package selected by the updated project reference and lock. It does not edit the canonical M5 scenario, restart barriers, actual-state control, test assertions, or shared qualification validators. No old M5 evidence was modified.

## Disposition and boundary

This review clears the package/feed/tooling candidate to proceed to the fresh full matrix after all tool changes are committed together. The probe restore/build was reported by its author as successful with zero warnings and zero errors; I did not run a build or native fixture as part of this read-only review. No test result from the old package set or the preflight controls can replace the required fresh Windows, bounded WSL2 Linux, and native macOS ARM64 M5 matrix for both .NET 8 and .NET 10.
