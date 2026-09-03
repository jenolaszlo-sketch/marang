# Protocol boundaries

## Decision

Marang uses standard protocols at external boundaries where they preserve the
product's semantics:

```text
supervising agent
      |
     MCP
      v
    Marang
      |
    Zhinu
      |
      +--> A2A --------> interoperable external agents
      +--> process ----> local/headless agents
      +--> Baize ------> bounded model execution
      +--> host --------> deterministic tools
```

- **MCP is the primary northbound agent interface.** A supervising agent asks
  Marang for an outcome and observes its lifecycle.
- **A2A is the preferred southbound external-agent interface.** Marang delegates
  one bounded activity to an independently implemented agent.
- **Zhinu owns durable workflow execution.** Protocol state is evidence and an
  integration concern; it is not Marang's outcome model.

> Protocols are adapters. The durable outcome model is Marang.

Health, configuration, authentication, and operator administration may use
host-native interfaces. “MCP northbound” does not mean every operational API
must be exposed as an agent tool.

## Northbound MCP

Marang does not identify or special-case the supervising product. Codex, Claude
Code, OpenCode, Cursor, or another MCP-capable agent uses the same outcome API:

- `marang_delegate`
- `marang_status`
- `marang_result`
- `marang_cancel`
- `marang_get_artifact`

Requests contain an objective, acceptance criteria, constraints, budget, and
required evidence—not a low-level workflow graph. Artifact retrieval is bounded
and authorization-aware. Large or sensitive artifacts return metadata and an
approved reference rather than being copied into the model context by default.

## Southbound A2A

A2A is a natural adapter because its standard concepts already include Agent
Cards, stateful Tasks, Messages, Parts, Artifacts, lifecycle updates, streaming,
and cancellation. This design was reviewed against the official
[A2A 0.3.0 specification](https://a2a-protocol.org/v0.3.0/specification/), but
an implementation must explicitly pin and test its supported version. Marang
does not implement A2A itself; it uses a conforming client behind the
execution-provider boundary.

Marang maps one workflow activity to one external A2A Task. Internal workers or
subagents remain opaque:

```text
Marang -> A2A Task -> external coding agent -> private worker tree
```

An A2A task receives only the minimum objective, criteria, constraints,
workspace capability, and artifact references required for that activity. Its
reported success means the external execution finished; Marang still validates
the resulting candidate and decides whether the delegation succeeded.

```text
A2A task completed != Marang outcome accepted
```

## Capability discovery and selection

Agent Cards can advertise skills and protocol capabilities. They are discovery
input, not authority. Initial routing performs simple matching:

```text
execution requirement
    -> configured, authorized providers
    -> advertised compatible capability
    -> deterministic policy selection
```

Host configuration owns endpoint allowlists, logical agent identity,
authentication, execution profiles, data-disclosure policy, and priority. A
request cannot introduce an arbitrary Agent Card URL or elevate an agent merely
because it claims a skill.

Suggested Marang-side concepts are `AgentId`, endpoint reference, capability
snapshot, protocol/version profile, execution policy reference, and
authentication reference. Credentials never enter workflow artifacts.

## Durable task correlation

A2A work may outlive a connection or Marang process. Persist the relationship:

```text
DelegationId
WorkflowReference
WorkflowStepId
ExecutionAttemptId
ExternalAgentId
ExternalTaskId
ProtocolVersion
```

The provider must expose the A2A Task identity before Marang treats submission
as safely reconnectable. Recovery uses Task observation or subscription rather
than starting another task. A direct A2A Message with no durable Task identity
may be accepted for short, read-only work, but it is not sufficient for the
initial restart-safe coding activity.

A2A status remains available in provider receipts while Marang maps it onto a
small execution state. Transport failure, authentication failure, unsupported
capability, remote task rejection/failure/cancellation, timeout, and invalid
result remain distinguishable for retry and escalation policy.

If an agent requests more input, authorization, or interaction, version 1 maps
that condition to `NeedsSupervisor` with evidence. Marang does not invent an
answer or silently enlarge the task.

## Artifact normalization

A2A Artifacts and Parts are normalized into Marang artifact envelopes containing
producer, schema, media type, immutable content identity, candidate revision,
and provenance. The workflow does not depend on protocol-native object shapes.

Remote file references are untrusted. Adapters enforce scheme and host policy,
content length, media type, content hash where available, timeouts, redirect
limits, and safe filenames before materialization. Repository data is never
uploaded solely because an agent advertises a compatible capability.

## Workspace semantics

A2A does not imply shared filesystem access. An execution profile declares one
of the host-supported workspace exchanges:

- a local bridge resolves an opaque workspace capability;
- a remote agent receives an authorized immutable repository revision and
  publishes a patch/candidate artifact;
- a bounded artifact bundle is disclosed under explicit policy.

Marang must never send an ambient local path to a remote agent and assume it has
the same meaning or authority.

## Versioning and delivery

The A2A adapter pins a tested protocol version and records it per execution.
Protocol upgrades require conformance tests for task-state mapping, artifact
normalization, streaming order, cancellation, errors, and authentication.

Initial delivery should prefer authenticated polling or streaming under the
existing outbound connection. Push notifications are deferred until Marang can
authenticate callbacks, prevent replay, correlate tenant/session identity, and
operate a safely exposed callback endpoint.

## Alternative providers

A2A is preferred, not universal. Process, SDK, HTTP, library, Baize, and
deterministic adapters remain valid where they provide the narrowest practical
integration. Vendor-specific code stays outside Marang core.

A Codex process adapter is still the shortest first dogfood path. A future A2A
bridge may wrap a valuable non-A2A agent, but only if it provides genuine
interoperability value. Possible packages, created only when proven necessary,
would follow the product namespace: `Marang.Execution.A2A`,
`Marang.Execution.Process`, or `Marang.Execution.Codex`.
