# Marang service architecture

## Product boundary

Marang is a deployable ASP.NET Core MVC service that exposes controlled
delegation to Codex and other MCP-capable supervisors. It owns the remote
service boundary: MCP and HTTP transport, authentication, authorization,
configuration, dependency composition, background dispatch, diagnostics, and
service lifecycle.

Marang does not own the reusable delegation model. That capability is
`Penghou.Qingniao`.

```text
Codex or another MCP supervisor
              |
          MCP / HTTP
              v
     Marang ASP.NET Core service
       auth, policy, DTO mapping,
       operations and diagnostics
              |
              v
       Penghou.Qingniao
    delegated-execution runtime
              |
     +--------+---------+
     |        |         |
   agent    model   deterministic
  provider provider    executor
```

The dependency direction is always `Marang -> Penghou.Qingniao`. Qingniao
never references Marang transport or hosting types.

## Responsibility split

| Component | Authority |
| --- | --- |
| Marang | ASP.NET Core MVC/MCP service, remote identity, authentication, authorization, request limits, configuration, composition, operations, and service diagnostics |
| `Penghou.Qingniao.Abstractions` | Reusable delegation requests/results, identities, lifecycle, evidence, receipts, provider and supervision contracts |
| `Penghou.Qingniao` | Delegation acceptance, routing, budgets, provider coordination, cancellation, reconnect, bounded repair, evidence normalization, and result aggregation |
| Penghou.Zhinu | Durable workflow execution, steps, waits, signals, fencing, restart, and recovery |
| Penghou.Fuwen | Typed artifact-driven workflow semantics and compilation |
| Penghou.Hongxian | Session continuity, correlation, decisions, incidents, recovery, and append-only audit narrative |
| Penghou.Siming | Canonical payload identity and cryptographically verifiable append-only evidence |
| Penghou.Baize | Provider-neutral model calls, tools, structured output, usage, and provenance |
| Penghou.Cangjie / Penghou.Hetu | Memory/context snapshots and code-graph identity, impact, and ownership |

Qingniao is a delegated-execution runtime, not a general workflow runtime.
Zhinu answers which workflow step executes and how it resumes. Qingniao
answers who receives one delegated activity, under what authority and budget,
and what evidence/result returned. Marang answers how a remote supervisor can
access that capability safely.

## Two supported deployment models

Qingniao is intentionally useful without Marang:

```text
Embedded                             Remote

Guyabano                             Guyabano or Codex
    |                                      |
    v                                Marang.Client or MCP
Penghou.Qingniao                            |
                                           v
                                      Marang.Server
                                           |
                                           v
                                    Penghou.Qingniao
```

`Marang.Client` is deferred until a real non-MCP remote client is exercised.
Guyabano should first dogfood Qingniao directly so the reusable API is shaped by
a second consumer rather than by Marang alone.

## Request path

1. Marang authenticates the remote caller and resolves its tenant/client scope.
2. Service policy authorizes the requested workspace capability, disclosure,
   provider profile, budget ceiling, and operation.
3. Transport DTOs are mapped to Qingniao contracts; transport credentials and
   ambient paths never cross that boundary.
4. Qingniao accepts the request idempotently, delegates the bounded activity,
   and returns lifecycle, evidence, and result contracts.
5. Zhinu supplies durability when the selected workflow requires it;
   Hongxian/Siming preserve session and audit evidence without becoming
   execution truth.
6. Marang maps bounded status, result, intervention, and artifact views back to
   MCP or HTTP responses and records operational diagnostics.

## Target solution boundary

```text
src/
  Penghou.Qingniao.Abstractions/  reusable, packable contracts
  Penghou.Qingniao/               reusable delegated-execution runtime
  Marang.Server/                  non-packable ASP.NET Core MVC/MCP executable

tests/
  Penghou.Qingniao.Tests/
  Marang.Server.Tests/
```

Provider and persistence integrations remain in these projects until multiple
consumers or implementations justify separate packages. Possible later
packages include `Penghou.Qingniao.Codex`, `Penghou.Qingniao.Zhinu`, and
`Marang.Client`; none is created speculatively.

## Security boundary

Marang authenticates remote callers and performs service-level authorization,
rate/size limiting, tenant isolation, endpoint hardening, and safe diagnostic
redaction. Qingniao enforces delegation invariants, capability/budget bounds,
workspace references, provider authorization, idempotency, immutable evidence,
and reconnect semantics. Neither layer treats provider capability claims as
authority or places credentials inside workflow artifacts or opaque handles.

## Migration state

The initial delegated-execution contract/runtime work was implemented and released under the
pre-release names `Marang.Abstractions` and `Marang`. Those packages are not a
compatibility commitment and will be superseded by
`Penghou.Qingniao.Abstractions` and `Penghou.Qingniao`; no compatibility shim is
planned while there are no external consumers. The detailed pre-extraction
runtime design remains in
[qingniao-runtime-architecture.md](qingniao-runtime-architecture.md).

## Non-goals

- Marang is not a reusable orchestration library or provider SDK.
- Qingniao is not an MCP/HTTP service, general workflow runtime, or session
  database.
- Marang and Qingniao do not replace Codex, Fuwen, Zhinu, Hongxian, Siming,
  Baize, Cangjie, or Hetu.
- Remote requests cannot grant themselves providers, tools, filesystem scope,
  credentials, budgets, or promotion authority.
- `Marang.Client` and provider-specific packages are not created until real use
  demonstrates a stable reusable boundary.
