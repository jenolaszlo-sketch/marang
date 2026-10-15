# M5 qualification inputs

`consumer-feed.zip` is a test fixture containing the exact four unpublished
M1 consumer packages retained by M2–M4, and five local lifecycle packages built
from unchanged Gagamba source `9fe127a98f682514138f4f5e333e47bcbeea8fe5`
with the distinct qualification version `0.1.0-preview.4-aaaw-m5.1`.

This snapshot makes isolated Linux and hosted runner consumer validation
reproducible without modifying producer workstreams or publishing packages to a
registry. It is not a supported product feed. All nine identities, producer
commits and SHA-256 hashes are pinned in `../aaaw-m2-packages.json` and
`../aaaw-m5-runtime-packages.json`. No version is resolved by floating range.

From the repository root, run `python eng/bootstrap-aaaw-m5-feed.py`. It validates
every archive entry and package hash before writing the ignored local feed.
The qualifier independently verifies hashes and embedded nuspec provenance.

The runtime packages reference only the lifecycle contract and Windows/Linux/
macOS providers. The separate Gagamba Linux M1 security workstream is absent.
