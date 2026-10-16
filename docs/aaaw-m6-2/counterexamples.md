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
