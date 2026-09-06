# Marang

[![CI](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/marang)](LICENSE)

Marang is an ASP.NET Core MVC MCP service that lets Codex and other remote
supervisors delegate bounded work, inspect progress and evidence, return at
supervision points, intervene, cancel, and retrieve an immutable result.

> Marang exposes delegation as a service. Qingniao makes delegation reusable.

```text
Codex / MCP client
        |
        v
   Marang.Server
 MVC + MCP + auth
        |
        v
 Penghou.Qingniao
 delegated-execution runtime
        |
  external actors
```

Marang owns remote transport, authentication and authorization, configuration,
request limits, dependency composition, service operations, diagnostics, and
lifecycle. It is a deployable product, not a reusable orchestration library.

## Qingniao

The transport-neutral delegation contracts and coordinator belong to:

- `Penghou.Qingniao.Abstractions`
- `Penghou.Qingniao`

Qingniao is a delegated-execution runtime, not a general workflow runtime. It
owns delegation identity, idempotent acceptance, lifecycle, provider
selection, budgets, cancellation, reconnect, supervision, evidence, bounded
repair, and result normalization. It does not own MCP, MVC, workflows, or
sessions.

This gives two valid integration modes:

```text
Embedded: Guyabano -> Penghou.Qingniao

Remote:   Guyabano or Codex -> Marang -> Penghou.Qingniao
```

Guyabano will dogfood the embedded API. A `Marang.Client` may be added later if
a real non-MCP remote client needs it.

## Penghou responsibilities

| Component | Responsibility |
| --- | --- |
| Marang | ASP.NET Core MVC/MCP service, remote security, configuration, operations, and diagnostics |
| Penghou.Qingniao | Reusable delegated-execution runtime and supervision lifecycle; not a general workflow runtime |
| [Penghou.Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) | Durable workflow execution, waits, signals, fencing, restart, and recovery |
| [Penghou.Fuwen](https://github.com/jenolaszlo-sketch/penghou-fuwen) | Typed artifact-driven workflow semantics and compilation |
| [Penghou.Hongxian](https://github.com/jenolaszlo-sketch/penghou-hongxian) | Session continuity, correlation, decisions, incidents, and recovery narrative |
| [Penghou.Siming](https://github.com/jenolaszlo-sketch/penghou-siming) | Canonical payload identity and verifiable append-only evidence |
| [Penghou.Baize](https://github.com/jenolaszlo-sketch/penghou-baize) | Model execution, structured output, tools, usage, and provenance |
| [Penghou.Cangjie](https://github.com/jenolaszlo-sketch/penghou-cangjie) | Demand-driven memory and immutable context snapshots |
| [Penghou.Hetu](https://github.com/jenolaszlo-sketch/penghou-hetu) | Code graph identity, context, impact, and ownership |

Zhinu decides what workflow step runs and how it resumes. Qingniao decides who
performs one delegated activity and what evidence came back. Marang makes that
capability safely accessible to a remote supervisor.

## Migration status

The initial delegated-execution runtime was implemented under the pre-release project names
`Marang.Abstractions` and `Marang`. Those packages are being superseded, not
maintained as compatibility contracts. The next change renames and extracts
them as Qingniao, then adds the `Marang.Server` executable.

See the [service architecture](docs/architecture.md), active
[roadmap](docs/roadmap.md), detailed pre-extraction
[Qingniao runtime architecture](docs/qingniao-runtime-architecture.md), and
[boundary decision](docs/decisions/0015-qingniao-extraction-and-marang-service-boundary.md).
