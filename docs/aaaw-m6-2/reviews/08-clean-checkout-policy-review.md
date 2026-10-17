# Clean-checkout policy and partial catch-up review

Reviewer: existing Luna independent-review agent, after source `6f441fe`.

The first native Windows clean checkout exposed a formatter line-ending
expectation in inherited `AdaptivePlanAuthoring.cs`. Git stores and checks out
C# with LF, while the Windows formatter requested CRLF for two wrapped lines.
The development checkout happened to contain those two CRLFs; Git normalization
hid that physical difference. No application change is attributed to this
qualification failure.

The proposed root `.editorconfig` contains only `root = true` and a C#
`end_of_line = lf` policy. The reviewer found it consistent with the existing
`.gitattributes` policy and found no need to alter the inherited source. The
policy must still pass formatting in a fresh LF checkout and native qualification.

The reviewer also inspected the final partial-state catch-up composition.
`RequireCurrentAsync` runs before read-only supervisor/effect projections are
constructed. The verifier and C# assertions require B to remain partially
applied and blocked, C prepared with B as prerequisite, a nonempty observed
state, declared no execution, unchanged complete store/checkout fingerprint,
and unchanged mutation apply log. No source blocker was found. This review
does not claim that pending native runs passed.
