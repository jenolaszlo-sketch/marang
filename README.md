# Marang

[![CI](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/marang/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/marang)](LICENSE)

Marang is an ASP.NET Core MVC MCP service that lets Codex and other remote
supervisors delegate bounded work, inspect progress and evidence, return at
supervision points, intervene, cancel, and retrieve an immutable result.

> Marang exposes delegation as a service. Qingniao makes delegation reusable.

## AAAW M5 portability baseline

**M5 is complete for the bounded repository profile across Windows, WSL2 Linux
and macOS ARM64.** The qualified scenarios preserve recovery and revision
continuity across all twelve restart barriers: valid mutations are preserved
without replay, stale dependents are replaced from recovered repository state,
and unresolved states remain fail-closed. This remains an experimental profile.

| Qualification anchor | Immutable commit |
| --- | --- |
| Gagamba implementation | [`0c1f05e`](https://github.com/jenolaszlo-sketch/gagamba/commit/0c1f05e199588622a5987f95a9149374f5bd858b) |
| Gagamba evidence | [`2c8f4f7`](https://github.com/jenolaszlo-sketch/gagamba/commit/2c8f4f7d7974510bac4baee9713acb6941b3ff09) |
| Marang qualified source | [`b9df4b6`](https://github.com/jenolaszlo-sketch/marang/commit/b9df4b6fa9d1553b5f2df95dceebb699238ce935) |
| Marang final evidence | [`5c3fb4b`](https://github.com/jenolaszlo-sketch/marang/commit/5c3fb4b36e817b7abebd5e91cda8f0be4f7dd8e0) |

The [qualification report](docs/aaaw-m5/gp-resume/qualification.md) preserves the
original counterexample, exact matrix, filesystem skip and independent reviews.
The proposed next work is [M6 operational recovery hardening](docs/aaaw-m6/plan.md),
split into bounded evidence batches. [M6.1](docs/aaaw-m6-1/contract.md) is in
implementation/qualification for corruption detection and fail-closed recovery
only; later M6 batches have not started.

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

The later [M5 bounded qualification](docs/aaaw-m5/gp-resume/qualification.md)
records the unchanged repository scenarios passing on Windows, delegated WSL2
Linux and macOS ARM64, including all twelve restart barriers and the actual-state
control. It retains the experimental scope and the original blocked Mac evidence.

M2 adds durable supervisor intent, pending discussions and checkpoints, current-state catch-up,
revision drill-down and explicit unresolved-delegation decisions. It preserves the M1 admission,
approval, fencing and provider-recovery contracts. The finite repository scenario continues through
four revisions while retaining one completed inventory execution. Its target assessments describe
repository declarations and evidence gaps; they do not assert Linux/Windows builds.

Use [the operator guide](docs/aaaw-m2/operator.md), [continuity contracts](docs/aaaw-m2/contracts.md)
and [ADR 0017](docs/decisions/0017-aaaw-m2-supervisor-continuity.md). Qualification uses
`eng/qualify-aaaw-m2.ps1` and the same pinned unpublished M1 packages. No stable foundation
or security-sensitive component implementation is changed, and no publication is required.

The [M2 qualification report](docs/aaaw-m2/evidence.md) records the supported claim,
all required probes, exact source/package evidence and the limits of supervisor continuity.

M3 introduces an experimental controlled repository effect protocol: immutable exact patches,
durable intent, physical-state reconciliation before replay, exact-state validation and separately
approved corrective revisions. A superseded result does not erase changed files. Partial or
unresolved effects block further mutation. See [the contract](docs/aaaw-m3/contracts.md),
[operator guidance](docs/aaaw-m3/operator.md) and [ADR 0018](docs/decisions/0018-aaaw-m3-repository-effect-reconciliation.md).
Reproduction uses `eng/qualify-aaaw-m3.ps1` and the unchanged pinned packages. No M3 package
release or push is part of qualification.

The [M3 qualification report](docs/aaaw-m3/evidence.md) records exact source/package identities,
all ten killed-process barriers, validation-driven correction and the controlled-checkout limits.

M4 coordinates multiple causal repository effects using the same bounded writer and pinned
packages. Its immutable graph separates current accepted prerequisites from physical history,
supports explicit upstream adoption and downstream replacement, and binds corrective work to
the actual checkout. See [the M4 contract](docs/aaaw-m4/contracts.md),
[operator guidance](docs/aaaw-m4/operator.md) and
[ADR 0019](docs/decisions/0019-aaaw-m4-causal-effect-coordination.md).
Reproduce with `eng/qualify-aaaw-m4.ps1`; this remains an experimental host profile without
package publication, rollback or general external-effect guarantees.

The [M4 qualification report](docs/aaaw-m4/evidence.md) records the accepted and physical effect
chains, all twelve kill barriers, exact source/package identities and the complete M1–M4 regression.
