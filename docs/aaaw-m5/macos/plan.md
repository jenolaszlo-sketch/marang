# Native macOS attempt after Linux freeze

Linux evidence was frozen in `a27cfba` before this work. Independent review 04
verified all 162 archived payload hashes, twelve barriers and the actual-state
control, and found no technical freeze blocker.

The first hosted attempt used `macos-15-intel`. Its native capture refusal is
valid evidence, but the complete consumer profile also depends on CedarSharp
1.0.0, whose macOS native asset is ARM64 only. Its authority-dependent failures
are an environment/profile mismatch, not evidence of a Hufu implementation bug.
Original counts, TRXs and logs are preserved under `attempt-a/`.

The corrected hosted run uses `macos-15`, an explicitly ARM64 image from the
[GitHub runner image catalog](https://github.com/actions/runner-images).
Actual `sw_vers`, image version, architecture and SDK/runtime inventory must be
captured from the job; the label alone is not OS evidence. SDKs 8.0.426 and
10.0.401 are requested. Physical `RUNNER_TEMP` is used as `TMPDIR` so the fixture
does not weaken the existing repository guard against symlink ancestors.
An explicit `uname -m == arm64` check prevents silent architecture drift. No
Hufu/CedarSharp package upgrade or implementation change is involved.

The exact nine hash-pinned packages in the existing qualification feed are
reused, with nuspec commit validation. Nothing is published to a registry.
The Marang qualification branch and these test-only inputs must be pushed to
run the authorized hosted qualification. The isolated Gagamba producer checkout
is not pushed or modified. Its exact unpublished commit is unavailable remotely;
the producer full macOS source suite is explicitly unexercised. Native package
controls are narrower evidence and must not be relabeled as that suite.

The current pinned Mac provider source refuses `LaunchCaptured` before dispatch.
Test-only native controls exercise ordinary launchd launch/stop with a nonce
marker, then captured launch refusal, absence of the target marker, preparation
discard and disposal. A passing refusal control does not qualify AAAW.

The same complete Marang .NET 8 and .NET 10 consumer suites run without a skip
filter, with exact counts and all failures retained. The shared canonical,
K4, twelve restart barriers and twice-fresh-process actual-state checks are
applied if artifacts are produced. Missing artifacts remain unexercised, never
passed. The job intentionally fails when the qualification is incomplete and
uploads the original bounded records, TRXs and artifact hash manifest anyway.

No Hufu/Gagamba implementation work, raw-process fallback, new orchestration
semantics or M6 work is authorized by this fixture.
