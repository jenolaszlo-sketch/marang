# M6.1 source-review follow-up

Date: 2026-10-16. This follow-up preserves the historical findings in
`02-source-review.md`; it reviews the subsequent provider-binding and WAL-test
updates. It is a source assessment only, not a qualification result.

## Closed provider-binding finding

The current `ValidateProviderRecovery` validates both directions of the
execution-to-provider relationship. It checks each branch-binding row against
its indexed columns and document, then refuses every provider start-intent
whose delegation ID has no validated host branch binding. A binding without a
start intent remains admissible, preserving the crash window after host
acceptance/binding but before the provider pump. Focused tests cover both
states. This closes the provider-binding blocker recorded in the prior review.

## WAL sidecar observation

The file-only damaged-store cases may begin with a WAL-mode main database and
no sidecars. A read-only inspection can create a zero-length `-wal` sidecar.
The updated test records this as `absent-to-empty-created`, checks zero length,
and still checks exact main-database byte preservation. Physical malformed
file cases continue to require no WAL sidecar. The live-WAL process cases
compare existing main-database and WAL bytes exactly. Given the contract’s
explicit statement that a zero-length newly materialized WAL contains no
frames and is not database initialization, this observation is acceptable and
does not indicate original WAL content was rewritten. Reports should preserve
the distinction between absent-to-absent, absent-to-empty-created, and
pre-existing-WAL-byte-identical.

## Disposition

No remaining source blocker was found in the reviewed provider-binding
relationship or the described WAL preservation oracle. M6.1 remains unqualified
until the exact-source focused and full regression evidence, host/package
provenance, cleanup evidence, and final review are complete. The bounded
contract still does not claim detection of coherent rollback or malicious
rewrites that update all matching digests, nor generic Hufu-internal validation.
