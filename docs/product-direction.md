# Marang product direction

## Product

Marang is the remotely accessible product: an ASP.NET Core MVC service with an
MCP interface for Codex and other supervising agents. A caller can submit work,
leave, return, inspect bounded progress/evidence, intervene at a fenced
checkpoint, cancel, and retrieve an immutable result.

Marang succeeds when this interaction is secure, understandable, recoverable,
and operable. Its differentiators are the quality of its MCP supervision
surface, service policy, diagnostics, and composition of reusable Penghou
capabilities—not ownership of another general workflow runtime.

## Reusable capability

Penghou.Qingniao is the reusable delegated-execution runtime used by
Marang. It owns provider-neutral request/result contracts, identity, lifecycle,
routing, budgets, external-operation reconnect, evidence, supervision, bounded
repair, and aggregation. Guyabano can use Qingniao directly without running
Marang.

Qingniao is not a general workflow runtime. Fuwen owns compiled workflow
semantics and Zhinu owns durable execution. Hongxian and Siming retain temporal
session/audit evidence. Baize, Cangjie, and Hetu retain their existing model,
memory, and code-graph responsibilities.

## Initial experience

The first Marang experience remains deliberately small:

```text
MCP submit
  -> authenticated and authorized service request
  -> Qingniao delegation
  -> bounded provider execution and evidence
  -> status / optional supervisor interaction
  -> immutable result
```

The simple `marang_delegate` MCP operation maps to Qingniao's built-in
`Implement/1` preset. Advanced Fuwen workflows remain host-validated and are
not interpreted by Marang.

## Direct and remote consumers

- Guyabano first exercises Qingniao as an embedded library.
- Codex exercises the same behavior remotely through Marang's MCP surface.
- A typed `Marang.Client` is deferred until a real remote non-MCP consumer
  demonstrates the need.

## Non-goals

Marang is not a reusable `Marang.Core`, workflow engine, provider SDK, model,
session ledger, memory, or code graph. Qingniao is not an MVC/MCP service or a
replacement for Zhinu. Neither component grants tools, workspaces, credentials,
budgets, or publishing authority based on remote or model-generated claims.
