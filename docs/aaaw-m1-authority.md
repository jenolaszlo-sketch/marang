# AAAW M1 revision authority adapter

`Marang.Hufu.AdaptiveRevisionAuthority` is a host adapter for evaluating one
immutable adaptive workflow proposal. It accepts only a bounded proposal
binding, a trusted Hufu context, and one explicit capability/resource pair. It
does not reference Zhinu proposal/runtime types, workflow SQL, or modify Hufu.

## Proposal binding

The host supplies `WorkflowId`, `ParentRevisionId`, `ParentStateDigest`, and
`ProposalDigest`. The canonical proposal digest must cover the workflow ID,
parent revision and parent state digest along with the full proposed change.
The host context must be authenticated independently; its run and revision IDs
must match the workflow and parent revision. The adapter passes the exact
proposal digest as Hufu's `AuthorityRequest.RequestIdentity`, so the recorded
decision is attributable to that proposal. The parent-state digest is also
carried through approval verification and must match exactly.

The adapter maps the finite Hufu resource capability names to existing
`AuthorityAction` values: `read-file`, `list-directory`, `read-metadata`,
`patch-file`, `release`, `write-file`, and `process.execute`. It canonicalizes
no caller path silently: the supplied path must already equal Hufu's canonical
relative path. A host must map proposal changes to the concrete scoped resource
and capability they require. The adapter does not add a workflow-revision
action or treat a policy reason string as an approval instruction.

## Outcomes and approval

Every evaluation asks the configured `IAuthorityRequestAuthorizer` for current
Hufu authority and requires a recorded, attributable Permit. Denial,
unavailability, malformed evidence, or identity mismatch returns `Denied`.
Hufu's Permit/Deny/Unavailable vocabulary does not itself express human
approval. The trusted host supplies `RequiresApproval` as a separate policy
decision. With a current Hufu Permit and no approval receipt, the result is
`ApprovalRequired` when that host policy requires approval, otherwise
`Automatic`.

Approval is a durable host receipt reference. The configured
`IAdaptiveRevisionApprovalVerifier` must resolve it from trusted state and
return the authenticated approver, tenant, evidence ID, approval time and
expiry. The adapter accepts that evidence only when tenant, workflow, parent
revision, parent-state digest, and proposal digest match the exact request, and
the evidence is currently valid. Caller-provided actor labels are never
credentials. Invalid or unavailable approval evidence fails closed as
`Denied`.

The host calls the adapter both when evaluating a proposal and immediately
before commit. Each call performs fresh current-authority evaluation; an old
Permit or approval result is not a dispatch token. Durable approval must be
revalidated against the current exact proposal. Runtime revision CAS, fencing,
approval persistence, and the commit transaction remain owned by Marang.Runs.
This Hufu preflight does not make resource effects atomic with Hufu revocation;
concrete resource providers retain their own checks and mutation-start
guarantees.

## Local evidence

`AdaptiveRevisionAuthorityTests` exercises the adapter against grants stored
in `Penghou.Hufu.Sqlite` and evaluated by the shipped
`Penghou.Hufu.Cedar.CedarAuthorityEvaluator`, including exact proposal
identity, current scoped grant checks, approval binding, a fresh check after
revocation, and malformed bindings/capabilities. Approval verification is
represented by a test host service; production hosts must provide authenticated
durable approval custody.
