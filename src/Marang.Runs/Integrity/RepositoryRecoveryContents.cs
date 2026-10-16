using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Integrity;

/// <summary>Logical admission checks for the pinned M5 repository profile. This type is read-only.</summary>
internal static class RepositoryRecoveryContents
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static async Task ValidateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid workflowId, CancellationToken ct)
    {
        try
        {
            var workflow = await One(connection, transaction,
                "SELECT active_revision,pending_proposal,cancelled FROM adaptive_workflows WHERE id=$w", ct,
                ("$w", workflowId.ToString("D"))).ConfigureAwait(false);
            if (workflow is null) throw Invalid("Expected workflow row is missing.");
            var activeRevisionId = Required(workflow[0], "active revision");
            var cancelled = workflow[2] switch { "0" => false, "1" => true, _ => throw Invalid("Workflow cancellation flag is malformed.") };

            var revisions = new Dictionary<string, AdaptiveRevision>(StringComparer.Ordinal);
            var revisionProposals = new Dictionary<string, string?>(StringComparer.Ordinal);
            await using (var cmd = Query(connection, transaction,
                "SELECT revision_id,document,proposal_id FROM adaptive_revisions WHERE workflow_id=$w ORDER BY rowid", ("$w", workflowId.ToString("D"))))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var rowId = reader.GetString(0);
                    var revision = Deserialize<AdaptiveRevision>(reader.GetString(1), "revision");
                    AdaptivePlanAuthoring.VerifyArchive(revision);
                    if (revision.WorkflowId != workflowId || revision.RevisionId != rowId || revisions.ContainsKey(rowId))
                        throw Invalid("Revision identity columns disagree with the archived document.");
                    revisions.Add(rowId, revision);
                    revisionProposals.Add(rowId, reader.IsDBNull(2) ? null : reader.GetString(2));
                }
            }
            if (revisions.Count == 0 || !revisions.ContainsKey(activeRevisionId))
                throw Invalid("Active revision is absent from the complete workflow history.");
            var active = revisions[activeRevisionId];

            var expectedDependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (revisionId, revision) in revisions)
            {
                var proposalId = revisionProposals[revisionId];
                if (revision.ParentRevisionId is null)
                {
                    if (proposalId is not null) throw Invalid("Initial revision unexpectedly references an activation proposal.");
                }
                else
                {
                    if (!revisions.ContainsKey(revision.ParentRevisionId) || proposalId is null ||
                        !await ProposalActivated(connection, transaction, workflowId, proposalId, revision, ct).ConfigureAwait(false))
                        throw Invalid("Revision parent/proposal activation binding is incomplete or contradictory.");
                }
                await ValidateRevisionRuns(connection, transaction, workflowId, revision, expectedDependencies, ct).ConfigureAwait(false);
            }
            await ValidateDependencyProjection(connection, transaction, workflowId, expectedDependencies, ct).ConfigureAwait(false);

            // Proposals are immutable documents; status is deliberately allowed to be any established M5 state.
            var proposals = new Dictionary<string, AdaptiveProposal>(StringComparer.Ordinal);
            await using (var cmd = Query(connection, transaction,
                "SELECT id,workflow_id,digest,document,status FROM adaptive_proposals WHERE workflow_id=$w", ("$w", workflowId.ToString("D"))))
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetString(0); var rowWorkflow = reader.GetString(1); var digest = reader.GetString(2);
                    var proposal = Deserialize<AdaptiveProposal>(reader.GetString(3), "proposal");
                    var status = reader.GetString(4);
                    if (rowWorkflow != workflowId.ToString("D") || proposal.ProposalId.ToString("D") != id ||
                        proposal.Candidate.WorkflowId != workflowId || AdaptiveRevisionContracts.Digest(proposal) != digest ||
                        status is not ("Pending" or "Approved" or "Committed" or "Rejected" or "Obsolete") || !proposals.TryAdd(id, proposal))
                        throw Invalid("Proposal identity or digest columns disagree with the archived document.");
                    AdaptivePlanAuthoring.VerifyArchive(proposal.Candidate);
                    if (proposal.Candidate.ParentRevisionId is null || !revisions.ContainsKey(proposal.Candidate.ParentRevisionId))
                        throw Invalid("Proposal parent revision is absent from workflow history.");
                    var expectedChanges = AdaptiveRevisionContracts.Compare(revisions[proposal.Candidate.ParentRevisionId], proposal.Candidate);
                    if (proposal.Changes is null || proposal.Changes.Length != expectedChanges.Length ||
                        proposal.Changes.Select(x => x.LogicalId).Distinct(StringComparer.Ordinal).Count() != expectedChanges.Length)
                        throw Invalid("Proposal change set is incomplete or ambiguous.");
                    foreach (var change in expectedChanges)
                    {
                        var supplied = proposal.Changes.SingleOrDefault(x => x.LogicalId == change.LogicalId);
                        if (supplied is null || supplied.Kind != change.Kind || !Enum.IsDefined(supplied.Disposition) ||
                            (status == "Committed" && supplied.Disposition == AdaptiveDisposition.RequiresManualDecision) ||
                            (status == "Committed" && (change.Kind == AdaptiveChangeKind.Preserved
                                ? supplied.Disposition is not (AdaptiveDisposition.Continue or AdaptiveDisposition.AllowToFinish)
                                : supplied.Disposition != AdaptiveDisposition.Fence)))
                            throw Invalid("Proposal change classification or disposition does not match its parent and candidate.");
                    }
                }
            }

            var pendingId = cancelled ? null : (string?)workflow[1];
            if (pendingId is not null && (!proposals.TryGetValue(pendingId, out var pending) ||
                pending.Candidate.ParentRevisionId != activeRevisionId ||
                await ProposalStatus(connection, transaction, pendingId, ct).ConfigureAwait(false) is not ("Pending" or "Approved")))
                throw Invalid("Workflow pending-proposal pointer is not bound to an admissible proposal.");
            if (cancelled && workflow[1] is not null) throw Invalid("Cancelled workflow retains a pending-proposal pointer.");

            await ValidateGraphs(connection, transaction, workflowId, revisions, proposals, active, ct).ConfigureAwait(false);
            await ValidateApprovals(connection, transaction, workflowId, proposals, ct).ConfigureAwait(false);
            await ValidateProviderRecovery(connection, transaction, ct).ConfigureAwait(false);
            await ValidateContinuity(connection, transaction, workflowId, revisions, proposals, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Durable repository documents or bindings are inconsistent.", ex);
        }
    }

    private static async Task ValidateRevisionRuns(SqliteConnection c, SqliteTransaction t, Guid workflowId,
        AdaptiveRevision revision, ISet<string> expectedDependencies, CancellationToken ct)
    {
        var runIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var branch in revision.Branches)
        {
            var runId = branch.Execution.Id.ToString("D");
            if (!runIds.TryAdd(branch.LogicalId, runId)) throw Invalid("Revision has duplicate logical branch identity.");
            var owner = await One(c, t, "SELECT workflow_id FROM adaptive_run_ownership WHERE run_id=$r", ct, ("$r", runId)).ConfigureAwait(false);
            if (owner is null || owner[0] != workflowId.ToString("D")) throw Invalid("Branch execution ownership row is missing or mismatched.");
            var run = await One(c, t, "SELECT workflow_name,workflow_version,input_json,input_type,output_type,definition_fingerprint,authorization_provider_id,authorization_binding_id FROM workflow_runs WHERE id=$r", ct, ("$r", runId)).ConfigureAwait(false);
            if (run is null || run[0] != branch.Execution.WorkflowName || run[1] != branch.Execution.WorkflowVersion ||
                run[2] != branch.Execution.InputJson || run[3] != branch.Execution.InputType || run[4] != branch.Execution.OutputType ||
                run[5] != branch.Execution.DefinitionFingerprint || run[6] != branch.Execution.AuthorizationProviderId || run[7] != branch.Execution.AuthorizationBindingId)
                throw Invalid("Branch execution identity does not match its durable workflow run.");
        }
        foreach (var branch in revision.Branches)
            foreach (var dependency in branch.Dependencies)
                expectedDependencies.Add(runIds[branch.LogicalId] + "\0" + runIds[dependency]);
    }

    private static async Task ValidateDependencyProjection(SqliteConnection c, SqliteTransaction t, Guid workflowId,
        ISet<string> expectedDependencies, CancellationToken ct)
    {
        var actualDependencies = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = Query(c, t, "SELECT run_id,prerequisite_run_id FROM adaptive_execution_dependencies WHERE run_id IN (SELECT run_id FROM adaptive_run_ownership WHERE workflow_id=$w)", ("$w", workflowId.ToString("D")));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) actualDependencies.Add(reader.GetString(0) + "\0" + reader.GetString(1));
        if (!expectedDependencies.SetEquals(actualDependencies)) throw Invalid("Durable execution dependency projection is missing, orphaned or contradictory.");
    }

    private static async Task<bool> ProposalActivated(SqliteConnection c, SqliteTransaction t, Guid workflowId,
        string proposalId, AdaptiveRevision revision, CancellationToken ct)
    {
        var row = await One(c, t, "SELECT digest,document,status FROM adaptive_proposals WHERE id=$p AND workflow_id=$w", ct,
            ("$p", proposalId), ("$w", workflowId.ToString("D"))).ConfigureAwait(false);
        if (row is null || row[2] != "Committed") return false;
        var proposal = Deserialize<AdaptiveProposal>(row[1]!, "activation proposal");
        return proposal.ProposalId.ToString("D") == proposalId && AdaptiveRevisionContracts.Digest(proposal) == row[0] &&
            AdaptiveRevisionContracts.Digest(proposal.Candidate) == AdaptiveRevisionContracts.Digest(revision) &&
            proposal.Candidate.ParentRevisionId == revision.ParentRevisionId;
    }

    private static async Task ValidateGraphs(SqliteConnection c, SqliteTransaction t, Guid workflowId,
        IReadOnlyDictionary<string, AdaptiveRevision> revisions, IReadOnlyDictionary<string, AdaptiveProposal> proposals,
        AdaptiveRevision active, CancellationToken ct)
    {
        var graphRows = new Dictionary<string, (string Digest, RepositoryEffectGraph Graph)>(StringComparer.Ordinal);
        await using (var cmd = Query(c, t, "SELECT revision_id,graph_digest,document FROM repository_effect_graphs WHERE workflow_id=$w", ("$w", workflowId.ToString("D"))))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var revisionId = reader.GetString(0); var digest = reader.GetString(1);
                var graph = Deserialize<RepositoryEffectGraph>(reader.GetString(2), "effect graph", WebJson);
                if (graph.WorkflowId != workflowId || graph.RevisionId != revisionId || !graph.Verify() || graph.GraphDigest != digest || !graphRows.TryAdd(revisionId, (digest, graph)))
                    throw Invalid("Stored effect graph key, identity or digest is inconsistent.");
                if (revisions.TryGetValue(revisionId, out var revision))
                {
                    if (AdaptivePlanAuthoring.ReadEffectGraph(revision)?.GraphDigest != digest)
                        throw Invalid("Effect graph is not exactly the graph embedded in its workflow revision.");
                }
                else if (!proposals.Values.Any(proposal => proposal.Candidate.RevisionId == revisionId &&
                    proposal.Candidate.ParentRevisionId == graph.ParentRevisionId &&
                    AdaptivePlanAuthoring.ReadEffectGraph(proposal.Candidate)?.GraphDigest == digest))
                    throw Invalid("Prepared effect graph is not embedded in any stored revision or proposal candidate.");
            }
        }
        var embeddedActiveGraph = AdaptivePlanAuthoring.ReadEffectGraph(active);
        if (embeddedActiveGraph is not null && (!graphRows.TryGetValue(active.RevisionId, out var storedActive) || storedActive.Digest != embeddedActiveGraph.GraphDigest))
            throw Invalid("Active revision graph projection is missing or mismatched.");
        foreach (var proposal in proposals.Values)
        {
            var candidateGraph = AdaptivePlanAuthoring.ReadEffectGraph(proposal.Candidate);
            if (candidateGraph is not null && (!graphRows.TryGetValue(candidateGraph.RevisionId, out var prepared) || prepared.Digest != candidateGraph.GraphDigest))
                throw Invalid("Proposal candidate graph is missing its exact prepared graph projection.");
        }

        var causalRows = new Dictionary<Guid, (string Digest, RepositoryCausalEffect Effect)>();
        await using (var cmd = Query(c, t, "SELECT effect_id,causal_digest,document FROM repository_causal_effects"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = Guid.Parse(reader.GetString(0)); var digest = reader.GetString(1);
                var effect = Deserialize<RepositoryCausalEffect>(reader.GetString(2), "causal effect", WebJson);
                if (id != effect.Intent.EffectId || !effect.HasValidDigest() || effect.CausalDigest != digest || !causalRows.TryAdd(id, (digest, effect)))
                    throw Invalid("Causal effect key, intent identity or digest is inconsistent.");
            }
        }
        var referencedCausalIds = new HashSet<Guid>();
        foreach (var graph in graphRows.Values.Select(x => x.Graph))
            foreach (var selection in graph.Effects)
            {
                referencedCausalIds.Add(selection.Effect.Intent.EffectId);
                if (!causalRows.TryGetValue(selection.Effect.Intent.EffectId, out var causal) ||
                    causal.Digest != selection.Effect.CausalDigest || AdaptiveRevisionContracts.Digest(causal.Effect) != AdaptiveRevisionContracts.Digest(selection.Effect))
                    throw Invalid("Graph selection lacks its exact causal effect projection.");
            }
        if (causalRows.Keys.Any(id => !referencedCausalIds.Contains(id)))
            throw Invalid("Causal effect projection is not selected by any retained graph.");
        foreach (var effectId in referencedCausalIds)
        {
            var causal = causalRows[effectId].Effect;
            var durableIntent = await SqliteRepositoryEffectJournal.ReadByIdAsync(c, t, effectId, ct).ConfigureAwait(false);
            if (durableIntent is null || durableIntent.Intent.IntentDigest != causal.Intent.IntentDigest ||
                AdaptiveRevisionContracts.Digest(durableIntent.Intent) != AdaptiveRevisionContracts.Digest(causal.Intent))
                throw Invalid("Causal effect intent is not bound to its exact durable effect journal projection.");
        }

        await using (var cmd = Query(c, t, "SELECT revision_id,graph_digest,target_state_digest,document FROM repository_effect_graph_validations WHERE workflow_id=$w", ("$w", workflowId.ToString("D"))))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var revisionId = reader.GetString(0); var graphDigest = reader.GetString(1); var target = reader.GetString(2);
                var validation = Deserialize<RepositoryEffectValidation>(reader.GetString(3), "effect graph validation", WebJson);
                if (!graphRows.TryGetValue(revisionId, out var graph) || graph.Digest != graphDigest ||
                    validation.TargetStateDigest != target || target != TerminalStateDigest(graph.Graph) ||
                    string.IsNullOrWhiteSpace(validation.Target) || string.IsNullOrWhiteSpace(validation.CommandIdentity) ||
                    string.IsNullOrWhiteSpace(validation.OutputDigest) || !RepositoryStateSnapshot.IsSha256(validation.OutputDigest) || string.IsNullOrWhiteSpace(validation.Evidence))
                    throw Invalid("Graph validation evidence is missing its graph, terminal-state or required-field binding.");
            }
        }
        await ValidatePrerequisiteAuthority(c, t, workflowId, graphRows, ct).ConfigureAwait(false);
    }

    private static async Task ValidatePrerequisiteAuthority(SqliteConnection c, SqliteTransaction t, Guid workflowId,
        IReadOnlyDictionary<string, (string Digest, RepositoryEffectGraph Graph)> graphs, CancellationToken ct)
    {
        await using var cmd = Query(c, t, "SELECT revision_id,graph_digest,dependent_effect_id,prerequisite_effect_id,observed_state_digest,document FROM repository_effect_prerequisite_authority WHERE workflow_id=$w", ("$w", workflowId.ToString("D")));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var revisionId = reader.GetString(0); var digest = reader.GetString(1);
            var dependentId = Guid.Parse(reader.GetString(2)); var prereqId = Guid.Parse(reader.GetString(3)); var state = reader.GetString(4);
            var evidence = Deserialize<RepositoryEffectAuthorizationEvidence>(reader.GetString(5), "prerequisite authorization", WebJson);
            if (!graphs.TryGetValue(revisionId, out var row) || digest != row.Digest || evidence.GraphDigest != digest ||
                evidence.WorkflowId != workflowId.ToString("D") || evidence.RevisionId != revisionId || evidence.DependentEffectId != dependentId ||
                evidence.PrerequisiteEffectId != prereqId || evidence.ObservedStateDigest != state || !RepositoryStateSnapshot.IsSha256(state))
                throw Invalid("Prerequisite authority evidence does not match its graph identity columns.");
            var dependent = row.Graph.Effects.SingleOrDefault(e => e.Effect.Intent.EffectId == dependentId);
            var prerequisite = dependent is null ? null : RepositoryEffectGraphRuntime.TransitivePrerequisites(row.Graph, dependent)
                .SingleOrDefault(e => e.Effect.Intent.EffectId == prereqId && e.Effect.CausalDigest == evidence.PrerequisiteCausalDigest);
            if (dependent is null || dependent.Reuse || prerequisite is null || evidence.IntentDigest != prerequisite.Effect.Intent.IntentDigest ||
                evidence.ObservedStateDigest != dependent.Effect.Intent.BaseState.StateDigest || evidence.Status != "Permit" ||
                !RepositoryStateSnapshot.IsSha256(evidence.PrerequisiteCausalDigest ?? ""))
                throw Invalid("Prerequisite authority does not resolve to the selected graph effect.");
        }
    }

    private static async Task ValidateApprovals(SqliteConnection c, SqliteTransaction t, Guid workflowId,
        IReadOnlyDictionary<string, AdaptiveProposal> proposals, CancellationToken ct)
    {
        var approvals = new Dictionary<string, AdaptiveRevisionApprovalEvidence>(StringComparer.Ordinal);
        await using (var cmd = Query(c, t, "SELECT a.evidence_id,a.proposal_id,a.digest,a.document FROM adaptive_operator_approvals a JOIN adaptive_proposals p ON p.id=a.proposal_id WHERE p.workflow_id=$w", ("$w", workflowId.ToString("D"))))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var evidenceId = reader.GetString(0); var proposalId = reader.GetString(1); var digest = reader.GetString(2);
                var evidence = Deserialize<AdaptiveRevisionApprovalEvidence>(reader.GetString(3), "operator approval");
                if (!proposals.TryGetValue(proposalId, out var proposal) || proposal.Candidate.WorkflowId != workflowId ||
                    evidence.EvidenceId != evidenceId || evidence.WorkflowId != workflowId.ToString("D") ||
                    evidence.ParentRevisionId != proposal.Candidate.ParentRevisionId || evidence.ParentStateDigest != proposal.ParentStateDigest ||
                    evidence.ProposalDigest != digest || digest != AdaptiveRevisionContracts.Digest(proposal) ||
                    string.IsNullOrWhiteSpace(evidence.ActorId) || string.IsNullOrWhiteSpace(evidence.TenantId) || evidence.ApprovedAt >= evidence.ExpiresAt ||
                    !approvals.TryAdd(evidenceId, evidence))
                    throw Invalid("Operator approval document is not bound to the stored proposal columns.");
            }
        }
        await using var proposalsQuery = Query(c, t,
            "SELECT id,status,approval_actor,approval_evidence,approval_expires FROM adaptive_proposals WHERE workflow_id=$w",
            ("$w", workflowId.ToString("D")));
        await using var proposalsReader = await proposalsQuery.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await proposalsReader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = proposalsReader.GetString(0); var status = proposalsReader.GetString(1);
            if (!proposals.TryGetValue(id, out var proposal) || status is not ("Approved" or "Committed") ||
                proposal.Authority != AdaptiveAuthorityDecision.ApprovalRequired) continue;
            if (proposalsReader.IsDBNull(2) || proposalsReader.IsDBNull(3) || proposalsReader.IsDBNull(4) ||
                !approvals.TryGetValue(proposalsReader.GetString(3), out var evidence) ||
                evidence.ProposalDigest != AdaptiveRevisionContracts.Digest(proposal) || evidence.ActorId != proposalsReader.GetString(2) ||
                !DateTimeOffset.TryParse(proposalsReader.GetString(4), out var expires) || expires != evidence.ExpiresAt)
                throw Invalid("Approved proposal receipt reference does not resolve to its exact durable operator evidence.");
        }
    }

    private static async Task ValidateProviderRecovery(SqliteConnection c, SqliteTransaction t, CancellationToken ct)
    {
        var attempts = new Dictionary<string, (ExternalOperationRecoveryRecord Record, string DelegationId)>(StringComparer.Ordinal);
        await using (var cmd = Query(c, t, "SELECT attempt_key,delegation_id,document FROM adaptive_delegation_attempts"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = reader.GetString(0); var delegation = reader.GetString(1);
                var record = SqliteDelegationRecoveryJournal.Decode(reader.GetString(2));
                var identity = record.Intent.Request.Identity;
                var expectedKey = AdaptiveRevisionContracts.Digest(new { identity.DelegationId, identity.WorkflowRun, identity.StructuralNode, identity.NodeGeneration, identity.ExecutionAttemptId });
                if (key != expectedKey || identity.DelegationId.Value.ToString("D") != delegation || !attempts.TryAdd(key, (record, delegation)))
                    throw Invalid("Provider attempt identity columns disagree with the recovery record.");
            }
        }
        var indexedCaptures = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = Query(c, t, "SELECT provider,handle,attempt_key FROM adaptive_provider_handles"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var provider = reader.GetString(0); var handle = reader.GetString(1); var key = reader.GetString(2);
                if (!attempts.TryGetValue(key, out var attempt) || attempt.Record.Capture is null ||
                    attempt.Record.Capture.Handle.Provider != provider || attempt.Record.Capture.Handle.Value != handle)
                    throw Invalid("Provider handle projection is orphaned or contradicts its durable capture.");
                indexedCaptures.Add(key);
            }
        }
        foreach (var (key, attempt) in attempts)
            if (attempt.Record.Capture is not null && !indexedCaptures.Contains(key))
                throw Invalid("Captured provider handle is missing its durable handle-index projection.");
        var boundDelegations = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = Query(c, t, "SELECT execution_id,delegation_id,request_digest,document FROM adaptive_branch_delegations"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var executionId = Guid.Parse(reader.GetString(0)); var delegationId = reader.GetString(1); var requestDigest = reader.GetString(2);
                var binding = Deserialize<BranchDelegationBinding>(reader.GetString(3), "branch delegation binding");
                // A host may persist acceptance/binding before the provider pump has durably
                // created a start-intent row, so this projection is validated internally only.
                if (executionId == Guid.Empty || binding.ExecutionId != executionId || binding.DelegationId.Value.ToString("D") != delegationId ||
                    binding.RequestDigest != requestDigest || !RepositoryStateSnapshot.IsSha256(requestDigest))
                    throw Invalid("Branch delegation binding is orphaned or differs from its document.");
                boundDelegations.Add(delegationId);
            }
        }
        foreach (var attempt in attempts.Values)
            if (!boundDelegations.Contains(attempt.DelegationId))
                throw Invalid("Provider start intent has no durable host branch-delegation binding.");
    }

    private static async Task ValidateContinuity(SqliteConnection c, SqliteTransaction t, Guid workflowId,
        IReadOnlyDictionary<string, AdaptiveRevision> revisions, IReadOnlyDictionary<string, AdaptiveProposal> proposals,
        CancellationToken ct)
    {
        await using (var cmd = Query(c, t, "SELECT workflow_id,revision_id,revision_digest,goal,trigger,rationale,evidence_refs_json,recorded_by,recorded_at FROM supervisor_revision_intents WHERE workflow_id=$w", ("$w", workflowId.ToString("D"))))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var rowWorkflow = reader.GetString(0); var revisionId = reader.GetString(1); var digest = reader.GetString(2);
                var goal = reader.GetString(3); var triggerText = reader.GetString(4); var rationale = reader.GetString(5);
                var refs = Deserialize<ImmutableArray<string>>(reader.GetString(6), "supervisor revision evidence references");
                var actor = reader.GetString(7); var recordedAt = reader.GetString(8);
                var revision = revisions.TryGetValue(revisionId, out var archived) ? archived :
                    proposals.Values.Select(p => p.Candidate).SingleOrDefault(p => p.RevisionId == revisionId);
                if (rowWorkflow != workflowId.ToString("D") || revision is null || revision.WorkflowId != workflowId ||
                    digest != AdaptiveRevisionContracts.Digest(revision) || !IsText(goal, 4096) || !IsText(rationale, 4096) ||
                    !Enum.TryParse<SupervisorIntentTrigger>(triggerText, ignoreCase: false, out var trigger) || !Enum.IsDefined(trigger) ||
                    !ValidItems(refs, 32, 512, allowEmpty: true) || !IsText(actor, 256) || !ValidTime(recordedAt))
                    throw Invalid("Supervisor revision intent columns or evidence references are inconsistent.");
                AdaptivePlanAuthoring.VerifyArchive(revision);
                var archive = Deserialize<AdaptivePlanArchive>(revision.PlanEnvelopeJson, "revision plan archive");
                var envelope = PlanRevisionDocument.LoadVerified(archive.EnvelopeFingerprint, Convert.FromBase64String(archive.EnvelopeBase64)).ReadEnvelope();
                var objective = new ContentDigest("sha256", "aaaw-m1/v1", AdaptiveRevisionContracts.Digest(goal));
                if (envelope.Semantics.Objective != objective) throw Invalid("Supervisor revision goal does not match the archived plan objective.");
            }
        }

        await using (var cmd = Query(c, t, "SELECT workflow_id,decision_id,parent_revision_id,parent_state_digest,summary,missing_json,affected_json,options_json,evidence_refs_json,status,choice,recorded_by,recorded_at,dismissal_reason FROM supervisor_pending_discussions WHERE workflow_id=$w", ("$w", workflowId.ToString("D"))))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var rowWorkflow = reader.GetString(0); var decisionId = reader.GetString(1); var parentId = reader.GetString(2);
                var parentDigest = reader.GetString(3); var summary = reader.GetString(4);
                var missing = Deserialize<ImmutableArray<string>>(reader.GetString(5), "discussion missing-information list");
                var affected = Deserialize<ImmutableArray<string>>(reader.GetString(6), "discussion affected-branch list");
                var options = Deserialize<ImmutableArray<string>>(reader.GetString(7), "discussion options");
                var refs = Deserialize<ImmutableArray<string>>(reader.GetString(8), "discussion evidence references");
                var statusText = reader.GetString(9); var choice = reader.IsDBNull(10) ? null : reader.GetString(10);
                var actor = reader.GetString(11); var recordedAt = reader.GetString(12); var dismissal = reader.IsDBNull(13) ? null : reader.GetString(13);
                if (rowWorkflow != workflowId.ToString("D") || !IsText(decisionId, 128) || !revisions.TryGetValue(parentId, out var parent) ||
                    !RepositoryStateSnapshot.IsSha256(parentDigest) || !IsText(summary, 4096) ||
                    !ValidItems(missing, 32, 1024, allowEmpty: true) || !ValidItems(affected, 32, 256, allowEmpty: true) ||
                    !ValidItems(options, 32, 1024, allowEmpty: false) || !ValidItems(refs, 32, 512, allowEmpty: true) ||
                    !Enum.TryParse<SupervisorDiscussionStatus>(statusText, ignoreCase: false, out var status) || !Enum.IsDefined(status) ||
                    !IsText(actor, 256) || !ValidTime(recordedAt) || affected.Any(id => parent.Branches.All(branch => branch.LogicalId != id)))
                    throw Invalid("Supervisor discussion columns or nested lists are inconsistent.");
                if (status == SupervisorDiscussionStatus.Pending && (choice is not null || dismissal is not null) ||
                    status == SupervisorDiscussionStatus.Resolved && (choice is null || !options.Contains(choice, StringComparer.Ordinal) || dismissal is not null) ||
                    status == SupervisorDiscussionStatus.Dismissed && (choice is not null || !IsText(dismissal, 1024)))
                    throw Invalid("Supervisor discussion resolution fields contradict its stored status.");
            }
        }

        await using var watermarkCommand = Query(c, t, "SELECT COALESCE(MAX(sequence),0) FROM adaptive_evidence WHERE workflow_id=$w", ("$w", workflowId.ToString("D")));
        var watermark = Convert.ToInt64(await watermarkCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await using (var cmd = Query(c, t, "SELECT workflow_id,actor_id,revision_id,evidence_watermark,acknowledged_at FROM supervisor_operator_checkpoints WHERE workflow_id=$w", ("$w", workflowId.ToString("D"))))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != workflowId.ToString("D") || !IsText(reader.GetString(1), 256) ||
                    !revisions.ContainsKey(reader.GetString(2)) || reader.GetInt64(3) < 0 || reader.GetInt64(3) > watermark ||
                    !ValidTime(reader.GetString(4)))
                    throw Invalid("Supervisor checkpoint is not bound to known workflow history and evidence.");
            }
        }

        await using (var cmd = Query(c, t, "SELECT proposal_id,workflow_id,document FROM supervisor_proposal_contexts WHERE workflow_id=$w", ("$w", workflowId.ToString("D"))))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var proposalId = reader.GetString(0); var rowWorkflow = reader.GetString(1);
                var context = Deserialize<SupervisorProposalContext>(reader.GetString(2), "proposal branch context");
                if (rowWorkflow != workflowId.ToString("D") || !proposals.TryGetValue(proposalId, out var proposal) ||
                    context.ProposalDigest != AdaptiveRevisionContracts.Digest(proposal) || context.ParentStateDigest != proposal.ParentStateDigest ||
                    !RepositoryStateSnapshot.IsSha256(context.ParentStateDigest) || context.Branches.IsDefault ||
                    context.Branches.Length != proposal.Changes.Length || context.Branches.Select(b => b.LogicalId).Distinct(StringComparer.Ordinal).Count() != context.Branches.Length)
                    throw Invalid("Supervisor proposal context does not bind to its stored proposal.");
                var parent = revisions[proposal.Candidate.ParentRevisionId!];
                foreach (var change in proposal.Changes)
                {
                    var branchContext = context.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
                    var prior = parent.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
                    if (branchContext is null || (prior is null) != (branchContext.PreviousStatus is null))
                        throw Invalid("Supervisor proposal branch context has a missing or unexpected prior branch.");
                    if (branchContext.PreviousStatus is { } priorStatus &&
                        (!Enum.TryParse<WorkflowStatus>(priorStatus, ignoreCase: false, out var parsedStatus) || !Enum.IsDefined(parsedStatus)))
                        throw Invalid("Supervisor proposal context contains an unknown prior execution state.");
                    var candidate = proposal.Candidate.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
                    var expectedReuse = change.Kind == AdaptiveChangeKind.Preserved && branchContext.PreviousStatus == nameof(WorkflowStatus.Completed) && candidate?.AcceptReuse == true;
                    if (branchContext.ReusedAtProposal != expectedReuse)
                        throw Invalid("Supervisor proposal reuse context contradicts its exact prior branch state.");
                }
            }
        }
    }

    private static bool IsText(string? value, int maxLength) => value is { Length: > 0 } && value.Length <= maxLength &&
        !value.Any(char.IsControl) && value == value.Trim();

    private static bool ValidItems(ImmutableArray<string> items, int maximumCount, int itemMaximumLength, bool allowEmpty) =>
        !items.IsDefault && items.Length <= maximumCount && (allowEmpty || items.Length > 0) &&
        items.All(item => IsText(item, itemMaximumLength)) && items.Distinct(StringComparer.Ordinal).Count() == items.Length;

    private static bool ValidTime(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var parsed) && parsed != default;

    private static async Task<string?> ProposalStatus(SqliteConnection c, SqliteTransaction t, string id, CancellationToken ct) =>
        (await One(c, t, "SELECT status FROM adaptive_proposals WHERE id=$p", ct, ("$p", id)).ConfigureAwait(false))?[0];

    private static string TerminalStateDigest(RepositoryEffectGraph graph)
    {
        var remaining = graph.Effects.Where(e => !e.Reuse).ToList(); var state = graph.PhysicalBaseState.StateDigest;
        while (remaining.Count != 0)
        {
            var next = remaining.SingleOrDefault(e => e.Effect.Intent.BaseState.StateDigest == state)
                ?? throw Invalid("Effect graph has no unique terminal state.");
            state = next.Effect.Intent.ExpectedPostState.StateDigest; remaining.Remove(next);
        }
        return state;
    }

    private static T Deserialize<T>(string json, string label, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(json, options) ?? throw Invalid($"Stored {label} JSON is empty.");

    private static string Required(string? value, string label) => string.IsNullOrWhiteSpace(value) ? throw Invalid($"Stored {label} is missing.") : value;
    private static InvalidDataException Invalid(string message) => new(message);

    private static SqliteCommand Query(SqliteConnection c, SqliteTransaction t, string sql, params (string Key, string Value)[] parameters)
    {
        var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        return command;
    }

    private static async Task<string?[]?> One(SqliteConnection c, SqliteTransaction t, string sql, CancellationToken ct,
        params (string Key, string Value)[] parameters)
    {
        await using var cmd = Query(c, t, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var values = new string?[reader.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
        return values;
    }
}
