# M6.2 counterexamples and development controls

Starting supported runtime: M6.1 qualification anchor
`5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29`. This record separates dirty-tree
development counterexamples from the later exact-source native qualification.
The original M5/M6.1 evidence is retained unchanged.

The first eight new profile-admission fixtures inserted unsupported or partial
profile metadata into otherwise healthy stores. Before wiring the format gate,
all eight failed because the existing inspector admitted the state: marker-only,
column-only, empty marker, future profile, unknown contract, unknown owner,
future record tag and duplicate marker. Their raw TRX/log are retained. These
are test-derived format fixtures, not genuine old-writer migration output.
After the gate was wired, the same eight tests passed and verified repeatable
refusal with unchanged main-database bytes.

Independent source review then found that matching manifest column names,
CHECK text and values could mask an incompatible table definition. The added
same-name malformed-schema test reproduced actual acceptance (one failed
test). The correction validates declared types, nullability, defaults, primary
key and hidden-column status with `PRAGMA table_xinfo`, and uses a 64-bit
version read. It does not repair that fixture or reinterpret its values.

The first attempt to run that case stopped at a test-only accessibility error:
a public theory exposed an internal checkpoint enum. The theory now accepts a
string and parses the internal enum. The next attempt stopped at two existing
test-analyzer cancellation requirements; file reads now use the test token.
Neither attempt ran the counterexample. The third attempt is the actual
one-case red result. All outputs are preserved separately.

The compatibility implementation's initial source review also caught a
case-sensitive mismatch after SQL normalization that would reject the valid
new effect-column constraint. It was corrected before current-format tests;
no passing runtime result is attributed to that intermediate code.

The first combined contract run reported 57 passed / 5 failed. Those five
failures compared snapshot records containing newly allocated dictionaries
using reference equality; they were test-oracle failures, not detected payload
changes. The assertions now compare catalog and typed-row contents with strict
ordering. The corrected focused run passed 65/65 on .NET 10 (including existing
journal/admission controls); the new migration/admission classes passed 32/32
on .NET 8. These focused passes do not substitute for native qualification.

These cases establish bounded decoder/admission gaps. They do not establish
that the old runtime promised support for future formats, that an arbitrary
future schema can be migrated, or that coherent removal of all version evidence
can be detected. The current transition recognizes only the exact supported
legacy profile and its one declared destination.

The first eight-case cross-version process run used genuine old-writer output
and candidate `51371d2`. It passed five cases and failed three; the complete
raw run and fixtures remain in `evidence/process-attempt-51371d2.zip`.
The actual partial-DDL and pre-commit interruption/retry cases passed, as did
completed-A upgrade with two fresh resumes and both repeated unsupported-state
refusal cases. Failures were kept separately:

- The transaction-begin marker was observed before its asynchronous write
  finished on Windows. Checkpoint evidence now publishes by temporary write
  followed by atomic rename.
- The post-commit retry correctly returned `AlreadyCurrent`; its test had
  incorrectly expected `Upgraded` for every checkpoint. The oracle now
  distinguishes committed and uncommitted interruption.
- The partial-B fixture migrated and completed its first recovery correctly.
  A second invocation of the original demonstration's normal path skipped its
  already-written K4 report and attempted final validation against partial
  state, which refused. Repeated partial-state recovery uses the existing
  supervisor catch-up APIs. No original M5 host behavior or
  workflow semantics changed to satisfy this test.

The updated cases also exercise profile-1 host refusal before explicit upgrade
and compare external authority storage and checkout state across migration.
Their acceptance remains pending a corrected process run and native matrix.

The second development process run passed seven cases and failed one; its raw
fixtures, TRX and compiled probe/test binaries are retained in
`evidence/process-run2.zip`. The reused M5 negative-control entry explicitly
forbids executor checkpoints and refused a partial-effect attempt before
reconciliation. It was designed for a different actual-state mismatch. The
final partial-state repeat check instead reconstructs the blocked operator
view through the existing graph supervisor and journal readers, without
constructing an executor, validator or authority publisher. All-table, external
authority/checkout and apply-log preservation remain required. Completed-A
still exercises two normal execution-host resumes.

The final partial-state catch-up and two nonempty provider-preservation cases
passed together on .NET 10 (3/3). Those two provider cases also passed on
.NET 8. Their raw results and the successful partial fixture are in
`evidence/corrected-focus.zip`. The K3 and K4 cases are now separately named
facts because their repeat operations differ. Full native qualification must
still execute all cases together from the exact frozen source.


The first clean native Windows attempt at `6f441fe` passed both runtime suites
and all 42 new .NET 10 cases, but the inherited formatter expected CRLF at two
wrapped lines while Git requires LF. An explicit matching formatter policy
passed a fresh LF-checkout check. The native attempts, including the interrupted
Ubuntu run and successful supplemental Mac run, remain in
`evidence/native-attempts-6f441fe.zip` with manifests and hashes. The complete
interrupted Linux clone is retained on task D: storage; the published subset
excludes duplicate source/Git objects and rebuildable output.

The first `f216844` Windows run passed formatting and .NET 8, then failed to
produce the K4 legacy fixture when effect A's NuGet restore could not write
vulnerability data to the full C: HTTP cache. It never reached migration for
that case. `evidence/windows-host-space-f216844.zip` preserves this invalidated
run. A fresh same-source run used D: for checkout, temporary files and HTTP
cache without disabling audit checks.

That D:-cache run was interrupted by host Modern Standby. Kernel-Power records
show standby beginning at 10:11:47 and return at 10:30:23 (local +08:00).
The unchanged M3 K7 and M4 K3 test deadlines elapsed across the interruption.
A separate new-test `AfterDdl` marker read hit a Windows sharing violation.
Atomic JSON publication remained intact, but file existence did not guarantee
immediate read availability. The marker reader now retries only Windows
sharing/lock codes 32/33 for at most two seconds. Other I/O errors, malformed
JSON, missing files and cancellation still fail; checkpoint and process
identity checks remain strict. A compiled-helper reproduction resolves a
100ms exclusive lock, refuses a permanent lock after 2001ms, and proves
missing-file/cancellation propagation. No original M5 timeout was increased.

The `f216844` Linux run also retains its unsuccessful outcome after the
user-authorized cgroup pause/resume. All eight new cross-version cases passed,
but an existing actual-state control's broker deadline expired across the
pause; cleanup remained confirmed. This is not a successful final native run
or a state-integrity regression. Raw reports, captures, selected native power
events and the marker reproduction are in
`evidence/qualification-corrections-f216844.zip`. These incidents do not
qualify resource exhaustion or host-sleep recovery. The next qualification
must run uninterrupted at the corrected, clean test source.
