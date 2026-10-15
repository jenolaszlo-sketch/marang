# Interim macOS workflow review

Reviewed commit `9da97d7b71eac3d6b0756fce41b690cde6ea6f6b` read-only while Actions run `38749640292` was in progress. This is source review only; native macOS artifacts are pending.

## Findings

### P2 — Push path filter misses qualification inputs

`.github/workflows/aaaw-m5-macos.yml:4-9` restricts automatic pushes to the workflow, the macOS driver, and the probe. The driver imports `eng/qualify-aaaw-m5.py`, uses the shared bootstrap/package-lock machinery, and qualifies the Marang product and tests. Later changes to those inputs will not schedule this workflow automatically. The current run is unaffected because commit `9da97d7...` changed the workflow itself. Broaden the path list to cover the shared qualifier, bootstrap and package pins, solution/build files, and relevant source/tests, or remove the narrow path filter while retaining the branch restriction.

### P3 — Probe JSON can omit a scratch-cleanup failure

`eng/aaaw-m5-macos-probe/Program.cs:174-181` serializes `result["exitCode"]` before deleting the probe scratch directory. If `Directory.Delete` fails, the process changes its returned exit code to 1 after the JSON is already written, leaving `native-profile.json` with a stale zero and no cleanup-failure field. The outer driver still records the process failure, so this cannot make qualification pass; it makes the detailed probe artifact incomplete on that error path. Record cleanup outcome before writing the JSON, or rewrite the result after a cleanup failure.

## Current-run assessment

The workflow pins its runner family, .NET SDKs, checkout/upload action revisions, uses read-only repository permission and disables persisted checkout credentials. The driver’s final qualification gate includes both framework test results and the semantic-control checks; expected capture refusal is not enough to report M5 qualified. The probe tests a nonce-bearing ordinary launch/termination control, expects captured launch to fail before dispatch, checks the marker remains absent, and disposes its prepared domain. It does not claim full native Mac lifecycle qualification.

At review time, the GitHub run API confirmed event `push`, head SHA `9da97d7b71eac3d6b0756fce41b690cde6ea6f6b`, and status `in_progress`; checkout and SDK setup had succeeded and the qualification step was still running. No Mac result is inferred here. The unpublished producer’s complete Mac suite remains unexercised by this workflow.
