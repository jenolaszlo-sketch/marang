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

## MCP tools

`Marang.Server` exposes these tools over MCP (`/mcp`), backed by the composed
Qingniao runtime:

- `marang_ping` — connectivity probe.
- `marang_delegate` — accept one bounded unit of work and pump it bounded steps.
- `marang_status`, `marang_result`, `marang_cancel` — null-safe reads and
  fenced cancellation.
- `marang_wait`, `marang_intervene`, `marang_inspect`, `marang_get_artifact` —
  supervisor-gated tools fenced on exact checkpoint and revision; stale
  actions are reported, never retried.

## Authentication

Loopback requests skip authentication for local development. Everything else
requires an API key in the `Authorization` header (`Bearer <key>`); a missing
or wrong key returns 401 without touching delegation state. Keys and per-caller
workspace roots come exclusively from host configuration and are never
committed or logged:

```powershell
$env:MARANG__APIKEYS__ALICE = "<key-material>"
$env:MARANG__ALLOWEDWORKSPACEROOTS__ALICE__0 = "workspace"
```

Rotation is an environment change plus restart; no code change. The caller
identity derived from the presented key is what reaches delegation scopes —
tools take no caller argument.

## Migration status

The initial delegated-execution runtime was implemented under the pre-release project names
`Marang.Abstractions` and `Marang`. Those packages are superseded, not
maintained as compatibility contracts: the runtime now lives in
`Penghou.Qingniao` (published previews, consumed by `Marang.Server`), and the
`Marang.Server` executable serves MCP plus health.

See the [service architecture](docs/architecture.md), active
[roadmap](docs/roadmap.md), detailed pre-extraction
[Qingniao runtime architecture](docs/qingniao-runtime-architecture.md), and
[boundary decision](docs/decisions/0015-qingniao-extraction-and-marang-service-boundary.md).

## Experimental adaptive workflow milestone

The M1 branch demonstrates a bounded durable repository read/report workflow that can preserve
unaffected executions while an approved revision fences and replaces affected work. It composes
Fuwen, Zhinu, Qingniao, Hufu/Cedar and Hongxian. It remains an experimental host profile;
the existing server UI does not expose general workflow editing.

M1 consumes unpublished packages built from the exact source revisions in
[the source lock](eng/aaaw-m1-sources.json). Use checkouts at those commits to build and qualify:

```powershell
./eng/qualify-aaaw-m1.ps1 -ZhinuCheckout ../zhinu -QingniaoCheckout ../qingniao
```

Add `-FullSourceQualification` for both complete source suites. Package-only preparation uses
`-PackagesOnly`, followed by restore with `NuGet.AAAWM1.Config`. No packages are published.

For a console demonstration with Approve, Reject, Cancel and Discuss actions:

```powershell
dotnet tests/Marang.RestartProbe/bin/Release/net10.0/Marang.RestartProbe.dll --phase adaptive --dir artifacts/aaaw-m1/interactive --interactive true
```

The demo reads this repository, writes only its own evidence directory, and uses a deterministic
observable provider through Qingniao's real lifecycle. See the [architecture](docs/aaaw-m1/architecture.md),
[revision contract](docs/aaaw-m1/revision-contract.md), [recovery limits](docs/aaaw-m1/delegation-recovery.md)
and [deferred work](docs/aaaw-m1/deferred.md).

The [M1 qualification report](docs/aaaw-m1/evidence.md) records exact source commits,
package hashes, test results, all crash barriers and the limits of the supported claim.

M2 adds durable supervisor intent, pending discussions and checkpoints, current-state catch-up,
revision drill-down and explicit unresolved-delegation decisions. It preserves the M1 admission,
approval, fencing and provider-recovery contracts. The finite repository scenario continues through
four revisions while retaining one completed inventory execution. Its target assessments describe
repository declarations and evidence gaps; they do not assert Linux/Windows builds.

Use [the operator guide](docs/aaaw-m2/operator.md), [continuity contracts](docs/aaaw-m2/contracts.md)
and [ADR 0017](docs/decisions/0017-aaaw-m2-supervisor-continuity.md). Qualification uses
`eng/qualify-aaaw-m2.ps1` and the same pinned unpublished M1 packages. No stable foundation
or security-sensitive component implementation is changed, and no publication is required.
