# M6.1 source-corrections follow-up

Date: 2026-10-16. Reviewed the pending journal correction relative to frozen
candidate `41829680bf68ca92d9fc72e82222804da360f8f6`. This is a source-only
review. The next focused runs and full qualification matrix remain pending.

## Sequence validation

The correction treats `repository_effect_events.sequence` as the table-wide
AUTOINCREMENT sequence used by the existing writer. It checks aggregate row
count/min/max against `sqlite_sequence`, requiring committed event numbers to
run from 1 through the current high-water mark. The check is global to the
table, while per-effect event reads remain ordered within each effect; it does
not incorrectly require interleaved per-effect histories to be contiguous.
The new tests exercise two interleaved effect histories, interior deletion of
an otherwise empty `Applying` event (with the projection still agreeing with
the final event), and tail deletion paired with a consistent projection
rewind. These cases address the sequence-history gap from the previous review.

Given the pinned table’s AUTOINCREMENT writer invariant, the global check is a
reasonable bounded detector for committed event deletion. It still cannot
detect a coherent rewrite that reconstructs all rows and the allocator state;
that remains within the contract’s stated tamper-detection limit.

## Validation-output compatibility

The correction restores the existing effect-journal validation rule: output
identity is opaque but must be nonempty. Requiring SHA-256 for this field had
caused the unchanged M5 superseded-applied replay test to fail, because that
writer legitimately persists a nonempty opaque identity. The updated
integrity check still binds the validation object to the event/projection
history, and the test mutates that identity to ensure disagreement is refused.
This correction matches the original writer contract and the retained M5
counterexample evidence.

## Disposition

The reviewed sequence fix and compatibility correction close the two source
findings reported for the frozen candidate. I found no remaining blocker in
these changes. Final acceptance still depends on the new focused runs passing
both TFMs, exact-source Windows/WSL2/macOS qualification, and final evidence
review. No qualification claim is made here.
