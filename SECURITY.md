# Security

Marang treats every remote request, repository value, provider response, and
model output as untrusted. A caller or model can propose work; it cannot enlarge
its own authority. Marang enforces the remote service boundary, while Qingniao
enforces delegated-execution invariants.

## Initial security invariants

- Marang authenticates remote callers and derives caller/tenant scope before
  mapping requests into Qingniao contracts.
- Marang applies endpoint, request, response, concurrency, rate, and
  diagnostic-redaction limits independently of provider budgets.
- MCP requests refer to host-approved workspaces by opaque identity.
- Path resolution stays within configured roots after canonicalization and link
  resolution.
- Workers operate in isolated candidate workspaces, not primary checkouts.
- Tools, commands, environment variables, network access, duration, output
  size, and concurrency are granted by host policy.
- Credentials and unrelated host files are unavailable to workers.
- Destructive commands, commits, pushes, and publication are denied by default.
- Qingniao accepts only host-registered executable providers; advertised
  capabilities are discovery input, never authorization.
- Subordinate agent providers do not receive the Marang MCP endpoint, preventing
  recursive delegation by default.
- External agent handles and provider events are persisted without copying
  authentication material into workflow artifacts.
- Opaque provider handles are reconnect identifiers, not credential containers,
  and must be redacted from service diagnostics.
- A2A Agent Cards are discovery metadata, not authorization. Endpoints and
  credentials come from host-controlled configuration, not delegation input.
- Remote artifact references are constrained by scheme, host, size, media type,
  redirect, timeout, filename, and integrity policy before materialization.
- Remote agents receive no repository content or workspace access without an
  explicit disclosure and authorization policy.
- A2A push callbacks remain disabled until authentication, replay protection,
  tenant correlation, and safe endpoint exposure are implemented.
- Repository instructions cannot modify policy, budget, profile routing, or
  acceptance criteria.
- Structured model output is schema-validated after any repair.
- Cancellation prevents later work but preserves prior evidence.
- Future-attention notifications are hints only; they cannot authorize work,
  alter state, extend budgets, or replace results.
- Supervisor interventions require host authentication, workspace authorization,
  and an expected revision; stale or replayed actions are rejected or treated
  as idempotent duplicates.
- Selective re-execution creates a new isolated `NodeGeneration` and cannot
  reopen or mutate terminal executions, results, or evidence.
- Logs and artifacts apply explicit secret redaction and retention policy.

Please report suspected vulnerabilities privately through the repository's
GitHub security advisory feature once the repository is published.
