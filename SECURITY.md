# Security

Marang treats repository content and worker-model output as untrusted. A model
can propose work; it cannot enlarge its own authority.

## Initial security invariants

- MCP requests refer to host-approved workspaces by opaque identity.
- Path resolution stays within configured roots after canonicalization and link
  resolution.
- Workers operate in isolated candidate workspaces, not primary checkouts.
- Tools, commands, environment variables, network access, duration, output
  size, and concurrency are granted by host policy.
- Credentials and unrelated host files are unavailable to workers.
- Destructive commands, commits, pushes, and publication are denied by default.
- Subordinate agent providers do not receive the Marang MCP endpoint, preventing
  recursive delegation by default.
- External agent handles and provider events are persisted without copying
  authentication material into workflow artifacts.
- Repository instructions cannot modify policy, budget, profile routing, or
  acceptance criteria.
- Structured model output is schema-validated after any repair.
- Cancellation prevents later work but preserves prior evidence.
- Logs and artifacts apply explicit secret redaction and retention policy.

Please report suspected vulnerabilities privately through the repository's
GitHub security advisory feature once the repository is published.
