# Remaining macOS qualification gates

The native attempt established a real execution-profile gap: the pinned
Gagamba Mac provider accepts the negotiated lifecycle requirements but refuses
bounded captured launch before dispatch. AAAW's M4/M5 owner requires that
captured launch, including complete output and confirmed termination receipts.
Ordinary launchd launch/stop success does not satisfy it.

This is a Gagamba execution-boundary question. A Marang permissive fallback,
mock, or reduced restart scenario would not resolve it. No Hufu/Gagamba
implementation was changed in this M5 work. Any implementation must remain
in the separately authorized component workstream.

Before M5 can claim macOS qualification:

1. Qualify a supported native Mac bounded-capture path with immutable package
   identity. Preserve refusal before dispatch for unsupported requests and
   ownership across launch, stop, output overflow/drain and cleanup failures.
   Do not promote the existing partial process-group guarantees to owner-death
   cleanup, escape resistance or hostile isolation.
2. Make the approved exact producer source available to the native runner
   through that workstream, and run its relevant native lifecycle/conformance
   suites. The current package-only controls are narrower; unavailable source
   must not be replaced by a different producer revision or mislabeled suite.
3. Rerun the unchanged Marang canonical scenario on the supported ARM64/Cedar
   profile: all twelve barriers, actual-state tamper control across two fresh
   restarts, physical and accepted lineage, A/inventory preservation, actual
   replacement bases, no duplicate mutations and K4 partial blocking.
4. Rerun both complete target-framework suites and affected Windows/Linux
   regressions for the changed runtime packages. Keep exact counts, reviewed
   filesystem skips, immutable package hashes and clean source identity.
5. Freeze the new Mac evidence and independently review the final comparison.

The Intel Cedar failure was a separate runner-selection mismatch. The existing
package supports macOS ARM64; selecting that profile requires no authority
adapter, CedarSharp or Hufu implementation change. It does not resolve the
captured-launch gap.

No new workflow primitives, compensation, arbitrary rollback, owner-death
semantics or M6 operational-readiness work is part of these gates.
