# M1 delegation recovery

Acceptance is not durable execution. The host binds acceptance to the exact Zhinu execution before the first Qingniao pump can start a provider. Qingniao's durable provider decorator then persists an intent before calling Start and captures the handle before forwarding it to the runtime. SQLite enforces exact attempt identity/fingerprint and global provider-handle uniqueness. Observations and terminal results are immutable or validated monotonic progress.

| Recovered evidence | M1 behavior |
|---|---|
| No acceptance/binding or operation intent | No provider operation was admitted; normal bounded admission may proceed |
| Binding exists but no intent | Explicit unresolved state; no automatic second acceptance |
| Intent exists, no captured handle | Unknown: prior start may have occurred; automatic respawn is blocked |
| Exact handle and observable provider | Replay exact intent to bind observation, then observe the same task without Start |
| Exact handle, provider cannot reconnect | KnownNotReconnectable, requiring resolution |
| Completion already persisted | Return the retained terminal result without contacting or starting a provider |

Recovered operation results can feed a step only if its execution remains selected by the active manifest and Zhinu's existing step lease/fence accepts the commit. Results of affected operations remain historical. The integrated probe deliberately collects an old provider's late completion after revision and verifies that the new outcome excludes it.

The real Qingniao public lifecycle is used for initial delegation. Recovery covers the same provider attempt through Qingniao's durable operation protocol; it does not deserialize the full in-memory supervisory runtime, candidate correction loop or intervention registries. Observation is distinct from new execution permission. Resume and provider cancellation are deliberately unsupported by this M1 decorator.

The deterministic qualification provider retains tasks in files so a fresh process can observe them. This proves the supported observable-provider profile, not reconnectability of every live Codex process or exactly-once provider execution. A process kill between provider Start and handle capture separately proves safe unresolved recovery with one start and no blind respawn.
