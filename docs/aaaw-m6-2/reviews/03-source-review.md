# M6.2 source review 03 — compatibility gate

**Disposition: source blocker found; qualification remains pending.** This is a read-only review of the current M6.2 compatibility diff based on the exact `5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29` baseline. No tests or builds were run as part of this review. It is not a qualification result.

## Finding

`RepositoryProfileCompatibility.ValidateManifestSchemaAsync` validates the profile table's ordered column names and checks that normalized table SQL contains `CHECK(SINGLETON=1)` and `CHECK(FORMAT_VERSION=1)`. It does not validate the declared type, `NOT NULL`, or primary-key/uniqueness metadata for those columns. Consequently, a table with the same five column names, permissive/wrong declarations, the two check expressions, and one row containing the expected values can be admitted as profile 1 even though it does not have the declared singleton manifest schema. The current malformed-manifest test uses a different column list, so it does not cover this same-name malformed schema.

Before freezing, validate the manifest columns structurally with `PRAGMA table_info` (declared types, nullability and primary-key position, plus the intended order), and add a refusal case for a same-name table missing those constraints. Keep the check-constraint validation and exactly-one-row validation as separate checks.

## Reviewed design and source behavior

The selected transition remains tightly bounded: it recognizes only the qualified M6.1 legacy shape, adds the explicit profile marker and effect-record tag in one SQLite immediate transaction, preflights before write-open, repeats strict preflight while holding the write reservation, and validates the destination before commit. The exact legacy profile remains readable under legacy meaning; profile-1 journal writes explicitly store tag 1, and journal reads/writes flow through format inspection. Unknown/partial paired marker states refuse rather than being repaired or inferred as legacy. The exact-old-source process harness checks the old source commit and clean checkout before using its built writer, with K3/K4 preservation and migration restart/refusal cases.

The contract accurately limits compatibility: no downgrade enforcement against an unchanged old executable, no coherent-rewrite detection claim, no predecessor-schema support, no power-loss atomicity claim, and no migration/repair of Zhinu or Hufu internals. Keep the exact-source old-writer build and process tests as qualification prerequisites; their presence in source does not itself establish that the fixture has run or passed.

## Scope

No other source blocker was identified in the reviewed gate path. In particular, the existing SQLite metadata names are checked as a pair, unknown object type/partial states are rejected, the upgrade checks the format again under the immediate transaction, and the current writer uses an explicit record tag. This review does not approve the implementation until the manifest schema validation gap is corrected and the required cross-platform/process evidence is independently inspected.
