# M6.1 freeze-integration source review

Date: 2026-10-16. Exact source reviewed: `e267f1c16b5cd7dc56ac33b65971be3d712fc673`.
This is a source-only review while the final Windows, WSL2 Linux, and native
macOS qualification runs are active.

The integration calls the journal-wide event-sequence/allocator check after
the pinned schema check and before logical-content validation or effect-ID
enumeration. This closes the empty-history case where retained
`sqlite_sequence` evidence otherwise escaped inspection because no effect IDs
were available to iterate. It runs within the same read-only transaction as
the rest of inspection. The journal's public verified reads also run the
check, while bulk journal reads perform it once and then validate each record.

The check is global to the event table, matching the existing AUTOINCREMENT
writer; it does not impose contiguous sequence numbers per effect and therefore
accepts interleaved histories. The added admission test starts from a healthy
empty event history, adds only allocator high-water evidence, confirms refusal,
and compares the source database bytes. No repair, renumbering, initialization,
or broader recovery behavior is introduced by this integration.

The diff from `41829680bf68ca92d9fc72e82222804da360f8f6` is limited to this
inspector call, focused sequence counterexamples/tests, and associated contract
and evidence updates. No additional changes to the original M5 tests,
qualification scripts, package pins, or M5 evidence were found in this delta.

No blocker was found in the reviewed integration. Final M6.1 acceptance remains
pending the exact-source platform matrix and final raw-evidence/provenance
review; this note is not a qualification claim.
