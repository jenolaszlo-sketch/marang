# Marker reader and interrupted native-run review

Reviewer: existing Luna independent-review agent. Scope: actual retained
`f216844` Linux failure and the bounded test-only marker-reader correction.

The sole Linux failure is the existing actual-state reconciliation control.
Its nested TRX spans 10:01:50 to 10:35:54 local time, across the authorized
qualification pause and host standby. The broker command's deadline expired;
cleanup and output capture remained confirmed. All eight new cross-version
cases passed, source start/end stayed clean and the delegated cgroup was
removed. The run remains unsuccessful and must not be promoted to a final
qualification pass.

The marker helper retries only Windows sharing/lock IOException codes 32/33,
with a two-second bound and 25ms interval. Other I/O errors and cancellation
propagate. JSON parsing and exact checkpoint/process identity assertions remain
outside the retry and unchanged. The reviewer found no substantive blocker in
the exact helper and callsite. The compiled reproduction proves a transient
lock resolves at 143ms, a permanent lock refuses after 2001ms, and missing-file
and cancellation behavior remain strict. The competing Windows handle was not
identified; no antivirus attribution is made.

The unchanged Windows M3/M4 deadlines expired across recorded host Modern
Standby. No production behavior or existing test timeout is changed to make
those interrupted runs pass. The corrected source still requires a fresh,
uninterrupted native matrix.
