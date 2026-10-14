using System.Text.Json;
using Marang.Hufu;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Adaptive;

public enum DelegationRecoveryClassification
{
    ObservableReconnectable,
    ObservableComplete,
    ObservableFailed,
    KnownNotReconnectable,
    ProviderStateUnknown,
    KnownUnsafeToRetry
}

public enum DelegationRecoveryAction { Wait, Observe, Replace }

/// <summary>A read-only, version-bound projection of one provider attempt and its active-manifest binding.</summary>
public sealed record DelegationRecoveryProjection(
    ExternalOperationStartIdentity Identity,
    Guid WorkflowId,
    Guid BoundExecutionId,
    string JournalRecordDigest,
    string ActiveRevisionId,
    string ActiveStateDigest,
    string BoundBranchAuthorityDigest,
    bool IsActive,
    DelegationRecoveryClassification Classification,
    bool SafeToReplace,
    bool RequiresDecision,
    DelegationRecoveryAction[] AllowedActions);

/// <summary>Durable operator decision. A replacement remains staged until the exact proposal is approved and committed.</summary>
public sealed record DelegationRecoveryDecision(
    Guid DecisionId,
    Guid WorkflowId,
    Guid DelegationId,
    Guid BoundExecutionId,
    string JournalRecordDigest,
    DelegationRecoveryClassification Classification,
    string BoundBranchAuthorityDigest,
    string ActiveRevisionId,
    string ActiveStateDigest,
    string[] Options,
    string ChosenOption,
    string Actor,
    DateTimeOffset RecordedAt,
    string Status,
    string? CandidateRevisionId,
    string? CandidateDigest,
    Guid? ProposalId,
    string? ProposalDigest,
    string? ResultingRevisionId,
    string? ResultingState = null,
    string? AuthorityOutcome = null,
    string? AuthorityEvidence = null);

/// <summary>
/// Projects and records conservative host-level delegation recovery. It never starts or retries provider work;
/// replacement is a normal M1 proposal whose commit performs the existing atomic fence and activation.
/// </summary>
public sealed class DelegationRecoveryActions(
    IZhinuSqliteDatabase database,
    SqliteDelegationRecoveryJournal journal,
    SqliteAdaptiveRevisionRepository revisions,
    AdaptiveSupervisor supervisor,
    IAdaptiveSupervisorAuthority authority)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS adaptive_delegation_recovery_decisions(id TEXT PRIMARY KEY,document TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static DelegationRecoveryProjection Project(ExternalOperationStartIdentity identity,
        ExternalOperationRecoveryRecord? record, Guid workflowId, Guid boundExecutionId,
        string activeRevisionId, string activeStateDigest, string boundBranchAuthorityDigest, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeRevisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeStateDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundBranchAuthorityDigest);
        if (workflowId == Guid.Empty || boundExecutionId == Guid.Empty) throw new ArgumentException("Workflow and execution bindings are required.");
        if (record is not null && record.Intent.Request.Identity != identity)
            throw new InvalidOperationException("Provider attempt identity conflict.");

        var digest = AdaptiveRevisionContracts.Digest(record);
        var classification = Classify(record);
        var safeToReplace = record is not null && isActive && boundBranchAuthorityDigest == "m1/bounded-repository-read-report" &&
            classification is DelegationRecoveryClassification.KnownNotReconnectable
            or DelegationRecoveryClassification.ProviderStateUnknown
            or DelegationRecoveryClassification.KnownUnsafeToRetry
            or DelegationRecoveryClassification.ObservableFailed;
        var requiresDecision = isActive && (classification is DelegationRecoveryClassification.KnownNotReconnectable
            or DelegationRecoveryClassification.ProviderStateUnknown
            or DelegationRecoveryClassification.KnownUnsafeToRetry
            or DelegationRecoveryClassification.ObservableFailed);
        var actions = new List<DelegationRecoveryAction> { DelegationRecoveryAction.Wait };
        if (isActive && classification == DelegationRecoveryClassification.ObservableReconnectable)
            actions.Add(DelegationRecoveryAction.Observe);
        if (safeToReplace) actions.Add(DelegationRecoveryAction.Replace);
        return new(identity, workflowId, boundExecutionId, digest, activeRevisionId, activeStateDigest,
            boundBranchAuthorityDigest, isActive,
            classification, safeToReplace, requiresDecision, actions.ToArray());
    }

    public async Task<DelegationRecoveryDecision> WaitAsync(DelegationRecoveryProjection projection, string actor,
        CancellationToken cancellationToken = default)
    {
        RequireAction(projection, DelegationRecoveryAction.Wait);
        RequireOperator(actor);
        await RequireCurrentAsync(projection, cancellationToken);
        return await RecordAsync(projection, "Wait", actor, "Waiting", null, null, null, cancellationToken);
    }

    /// <summary>Observes only the already captured provider handle; this method never starts or fetches a result.</summary>
    public async Task<ExternalOperationObservation> ObserveAsync(DelegationRecoveryProjection projection,
        IExternalOperationProvider provider, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        RequireAction(projection, DelegationRecoveryAction.Observe);
        RequireOperator(actor);
        var current = await RequireCurrentAsync(projection, cancellationToken);
        if (current?.Capture is null || current.RecoveryState != ExternalOperationRecoveryState.KnownObservable || current.Result is not null)
            throw new WorkflowStateException("The captured operation is no longer reconnectable.");
        var decision = await RecordAsync(projection, "Observe", actor, "Observing", null, null, null, cancellationToken);
        try
        {
            var observation = await provider.ObserveAsync(current.Capture.Handle, cancellationToken);
            _ = await RequireCurrentAsync(projection, cancellationToken);
            await journal.RecordObservationAsync(projection.Identity, observation, cancellationToken);
            await UpdateDecisionAsync(decision with { Status = "Observed", ResultingState = observation.State.ToString() }, cancellationToken);
            return observation;
        }
        catch
        {
            // Retain the interrupted choice even when the caller's cancellation token has ended.
            // A hard process death leaves Observing, which catch-up exposes for safe fresh observation.
            await UpdateDecisionAsync(decision with { Status = "ObservationUnresolved", ResultingState = "No authoritative adoption; refresh provider condition." }, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Stages the replacement under an exact recovery decision; approval and commit remain separate calls.</summary>
    public async Task<DelegationRecoveryDecision> PrepareReplacementAsync(DelegationRecoveryProjection projection,
        AdaptiveRevision candidate, string impact, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate = JsonSerializer.Deserialize<AdaptiveRevision>(JsonSerializer.Serialize(candidate))!;
        RequireAction(projection, DelegationRecoveryAction.Replace);
        RequireOperator(actor);
        var current = await RequireCurrentAsync(projection, cancellationToken);
        if (current is null) throw new WorkflowStateException("A missing journal record cannot authorize replacement.");
        var active = await revisions.GetActiveAsync(candidate.WorkflowId, cancellationToken);
        if (candidate.WorkflowId != projection.WorkflowId ||
            candidate.ParentRevisionId != projection.ActiveRevisionId || active.RevisionId != projection.ActiveRevisionId)
            throw new WorkflowStateException("Replacement must target the exact active workflow revision.");
        var oldTarget = active.Branches.SingleOrDefault(b => b.Execution.Id == projection.BoundExecutionId)
            ?? throw new WorkflowStateException("The unresolved operation is not owned by the active revision.");
        var targetChange = AdaptiveRevisionContracts.Compare(active, candidate).Single(c => c.LogicalId == oldTarget.LogicalId);
        if (targetChange.Kind == AdaptiveChangeKind.Preserved)
            throw new WorkflowStateException("Replacement must change or remove the unresolved branch so M1 atomically fences its old execution.");
        _ = authority.PolicyFor(candidate);
        if (candidate.Branches
            .Any(b => b.AuthorityDigest != "m1/bounded-repository-read-report"))
            throw new WorkflowAuthorizationException("Replacement exceeds the bounded repository read/report recovery profile.");

        var decisionId = Guid.NewGuid();
        var preparing = await RecordAsync(projection, "Replace", actor, "Preparing", decisionId, null, null, cancellationToken,
            candidate.RevisionId, AdaptiveRevisionContracts.Digest(candidate));
        var staged = await supervisor.ProposeAsync(candidate, impact, cancellationToken);
        try
        {
            _ = await RequireCurrentAsync(projection, cancellationToken);
            if (staged.Proposal.Candidate.WorkflowId != preparing.WorkflowId ||
                staged.Proposal.ParentStateDigest != projection.ActiveStateDigest ||
                AdaptiveRevisionContracts.Digest(staged.Proposal.Candidate) != preparing.CandidateDigest)
                throw new WorkflowStateException("Replacement proposal no longer matches the reviewed active state.");
        }
        catch
        {
            await supervisor.RejectAsync(staged.Proposal, staged.Digest, actor, cancellationToken);
            throw;
        }
        return await UpdateDecisionAsync(preparing with
        {
            Status = "AwaitingApproval",
            ProposalId = staged.Proposal.ProposalId,
            ProposalDigest = staged.Digest,
            AuthorityOutcome = staged.View.Authority,
            AuthorityEvidence = staged.Proposal.AuthorityEvidence
        }, cancellationToken);
    }

    /// <summary>Commits the exact staged proposal only after a fresh Hufu check by AdaptiveSupervisor.</summary>
    public async Task<AdaptiveRevisionApprovalReceipt> ApproveReplacementAsync(Guid decisionId, string actor,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(actor);
        var decision = await GetDecisionAsync(decisionId, cancellationToken)
            ?? throw new WorkflowStateException("Recovery decision does not exist.");
        if (decision.Status != "AwaitingApproval" || decision.ProposalId is null || decision.ProposalDigest is null)
            throw new WorkflowStateException("Recovery decision has no staged proposal awaiting approval.");
        var proposal = await revisions.GetPendingAsync(decision.WorkflowId, cancellationToken);
        if (proposal?.ProposalId != decision.ProposalId || AdaptiveRevisionContracts.Digest(proposal) != decision.ProposalDigest)
            throw new WorkflowStateException("The staged recovery proposal is stale or was replaced.");
        try
        {
            var receipt = await supervisor.ApproveAsync(proposal, decision.ProposalDigest, actor, cancellationToken);
            await UpdateDecisionAsync(decision with
            {
                AuthorityOutcome = "ApprovalRecorded",
                AuthorityEvidence = receipt.EvidenceId
            }, cancellationToken);
            return receipt;
        }
        catch
        {
            var denied = await LatestAuthorityEvidenceAsync(decision.ProposalId.Value, decision.ProposalDigest, cancellationToken);
            await UpdateDecisionAsync(decision with { AuthorityOutcome = denied.Outcome, AuthorityEvidence = denied.Evidence }, cancellationToken);
            throw;
        }
    }

    public async Task<AdaptiveRevisionApprovalReceipt?> GetReplacementApprovalAsync(Guid decisionId,
        CancellationToken cancellationToken = default)
    {
        var decision = await GetDecisionAsync(decisionId, cancellationToken)
            ?? throw new WorkflowStateException("Recovery decision does not exist.");
        if (decision.ProposalId is null || decision.ProposalDigest is null) return null;
        return authority is SqliteAdaptiveSupervisorAuthority sqlite
            ? await sqlite.FindApprovalAsync(decision.ProposalId.Value, decision.ProposalDigest, cancellationToken)
            : null;
    }

    /// <summary>Commits the exact staged proposal only after a fresh Hufu check by AdaptiveSupervisor.</summary>
    public async Task<DelegationRecoveryDecision> CompleteReplacementAsync(Guid decisionId, string actor,
        AdaptiveRevisionApprovalReceipt? receipt, CancellationToken cancellationToken = default)
    {
        RequireOperator(actor);
        var decision = await GetDecisionAsync(decisionId, cancellationToken)
            ?? throw new WorkflowStateException("Recovery decision does not exist.");
        if (decision.ChosenOption != "Replace" || decision.ProposalId is null || decision.ProposalDigest is null)
            throw new WorkflowStateException("Recovery decision has no exact staged replacement.");
        if (decision.Status == "Committed") return decision;
        if (decision.Status != "AwaitingApproval") throw new WorkflowStateException("Recovery decision is not ready to commit.");
        var proposal = await revisions.GetPendingAsync(decision.WorkflowId, cancellationToken);
        if (proposal is null)
        {
            if (await WasCommittedAsync(decision, cancellationToken))
            {
                var committedAuthority = await LatestAuthorityEvidenceAsync(decision.ProposalId.Value, decision.ProposalDigest, cancellationToken);
                return await UpdateDecisionAsync(decision with
                {
                    Status = "Committed",
                    ResultingRevisionId = decision.CandidateRevisionId,
                    AuthorityOutcome = committedAuthority.Outcome,
                    AuthorityEvidence = committedAuthority.Evidence
                }, cancellationToken);
            }
            throw new WorkflowStateException("The staged recovery proposal is stale or was replaced.");
        }
        if (proposal.ProposalId != decision.ProposalId || AdaptiveRevisionContracts.Digest(proposal) != decision.ProposalDigest)
            throw new WorkflowStateException("The staged recovery proposal is stale or was replaced.");
        receipt ??= await GetReplacementApprovalAsync(decisionId, cancellationToken);
        try
        {
            await supervisor.CommitAsync(proposal, decision.ProposalDigest, receipt, cancellationToken);
        }
        catch
        {
            var denied = await LatestAuthorityEvidenceAsync(decision.ProposalId.Value, decision.ProposalDigest, cancellationToken);
            await UpdateDecisionAsync(decision with { AuthorityOutcome = denied.Outcome, AuthorityEvidence = denied.Evidence }, cancellationToken);
            throw;
        }
        var authorityEvidence = await LatestAuthorityEvidenceAsync(decision.ProposalId.Value, decision.ProposalDigest, cancellationToken);
        var completed = decision with
        {
            Status = "Committed",
            ResultingRevisionId = decision.CandidateRevisionId,
            AuthorityOutcome = authorityEvidence.Outcome,
            AuthorityEvidence = authorityEvidence.Evidence
        };
        return await UpdateDecisionAsync(completed, cancellationToken);
    }

    /// <summary>Reattaches a proposal created just before a host crash to its already durable decision intent.</summary>
    public async Task<DelegationRecoveryDecision> ResumePreparedReplacementAsync(Guid decisionId, string actor,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(actor);
        var decision = await GetDecisionAsync(decisionId, cancellationToken)
            ?? throw new WorkflowStateException("Recovery decision does not exist.");
        if (decision.Status != "Preparing" || decision.CandidateRevisionId is null || decision.CandidateDigest is null)
            return decision;
        var active = await revisions.GetActiveAsync(decision.WorkflowId, cancellationToken);
        var state = await revisions.GetStateDigestAsync(decision.WorkflowId, cancellationToken);
        if (active.RevisionId != decision.ActiveRevisionId || state != decision.ActiveStateDigest ||
            !active.Branches.Any(b => b.Execution.Id == decision.BoundExecutionId &&
                b.AuthorityDigest == "m1/bounded-repository-read-report"))
            return decision;
        var journalRecords = await journal.ListAsync(new DelegationId(decision.DelegationId), cancellationToken);
        if (!journalRecords.Any(r => AdaptiveRevisionContracts.Digest(r) == decision.JournalRecordDigest)) return decision;
        var bound = await journal.GetBindingAsync(decision.BoundExecutionId,
            active.Branches.Single(b => b.Execution.Id == decision.BoundExecutionId).InputDigest, cancellationToken);
        if (bound?.DelegationId.Value != decision.DelegationId) return decision;
        var pending = await revisions.GetPendingAsync(decision.WorkflowId, cancellationToken);
        if (pending is null || pending.Candidate.RevisionId != decision.CandidateRevisionId ||
            AdaptiveRevisionContracts.Digest(pending.Candidate) != decision.CandidateDigest ||
            pending.ParentStateDigest != decision.ActiveStateDigest)
            return decision;
        var stageAuthority = await LatestAuthorityEvidenceAsync(pending.ProposalId, AdaptiveRevisionContracts.Digest(pending), cancellationToken);
        return await UpdateDecisionAsync(decision with
        {
            Status = "AwaitingApproval",
            ProposalId = pending.ProposalId,
            ProposalDigest = AdaptiveRevisionContracts.Digest(pending),
            AuthorityOutcome = stageAuthority.Outcome,
            AuthorityEvidence = stageAuthority.Evidence
        }, cancellationToken);
    }

    public Task<DelegationRecoveryDecision?> GetDecisionAsync(Guid decisionId, CancellationToken cancellationToken = default) =>
        ReadDecisionAsync(decisionId, cancellationToken);

    public async Task<DelegationRecoveryDecision[]> ListDecisionsAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM adaptive_delegation_recovery_decisions ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var decisions = new List<DelegationRecoveryDecision>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var decision = JsonSerializer.Deserialize<DelegationRecoveryDecision>(reader.GetString(0));
            if (decision?.WorkflowId == workflowId) decisions.Add(decision);
        }
        return decisions.ToArray();
    }

    private async Task<ExternalOperationRecoveryRecord?> RequireCurrentAsync(DelegationRecoveryProjection projection, CancellationToken ct)
    {
        var record = await journal.ReadAsync(projection.Identity, ct);
        if (AdaptiveRevisionContracts.Digest(record) != projection.JournalRecordDigest)
            throw new WorkflowStateException("Delegation recovery projection is stale; refresh the operator view.");
        var workflowId = projection.WorkflowId;
        var active = await revisions.GetActiveAsync(workflowId, ct);
        var state = await revisions.GetStateDigestAsync(workflowId, ct);
        if (active.RevisionId != projection.ActiveRevisionId || state != projection.ActiveStateDigest)
            throw new WorkflowStateException("Delegation recovery projection is stale; refresh the operator view.");
        var boundBranch = active.Branches.SingleOrDefault(b => b.Execution.Id == projection.BoundExecutionId);
        var actualIsActive = false;
        if (boundBranch is not null)
        {
            var binding = await journal.GetBindingAsync(boundBranch.Execution.Id, boundBranch.InputDigest, ct);
            actualIsActive = binding?.DelegationId == projection.Identity.DelegationId;
        }
        if (projection.IsActive != actualIsActive)
            throw new WorkflowStateException("Delegation projection has an invalid active-manifest binding.");
        var refreshed = Project(projection.Identity, record, workflowId, projection.BoundExecutionId,
            active.RevisionId, state, boundBranch?.AuthorityDigest ?? projection.BoundBranchAuthorityDigest, actualIsActive);
        if (refreshed.BoundBranchAuthorityDigest != projection.BoundBranchAuthorityDigest ||
            refreshed.Classification != projection.Classification || refreshed.SafeToReplace != projection.SafeToReplace ||
            refreshed.RequiresDecision != projection.RequiresDecision ||
            !refreshed.AllowedActions.SequenceEqual(projection.AllowedActions))
            throw new WorkflowStateException("Delegation projection is stale; refresh the operator view.");
        return record;
    }

    private async Task<DelegationRecoveryDecision> RecordAsync(DelegationRecoveryProjection projection, string chosen,
        string actor, string status, Guid? id, Guid? proposalId, string? proposalDigest, CancellationToken ct,
        string? candidateRevisionId = null, string? candidateDigest = null)
    {
        var decision = new DelegationRecoveryDecision(id ?? Guid.NewGuid(),
            projection.WorkflowId, projection.Identity.DelegationId.Value, projection.BoundExecutionId,
            projection.JournalRecordDigest, projection.Classification, projection.BoundBranchAuthorityDigest,
            projection.ActiveRevisionId, projection.ActiveStateDigest,
            projection.AllowedActions.Select(a => a.ToString()).ToArray(), chosen, actor,
            database.TimeProvider.GetUtcNow(), status, candidateRevisionId, candidateDigest, proposalId, proposalDigest, null);
        await using var connection = await database.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO adaptive_delegation_recovery_decisions(id,document) VALUES($id,$doc)";
            command.Parameters.AddWithValue("$id", decision.DecisionId.ToString("D"));
            command.Parameters.AddWithValue("$doc", JsonSerializer.Serialize(decision));
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO adaptive_evidence(workflow_id,kind,document) VALUES($w,'DelegationRecoveryDecision',$doc)";
            command.Parameters.AddWithValue("$w", decision.WorkflowId.ToString("D"));
            command.Parameters.AddWithValue("$doc", JsonSerializer.Serialize(decision));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
        return decision;
    }

    private async Task<DelegationRecoveryDecision> UpdateDecisionAsync(DelegationRecoveryDecision decision, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var document = JsonSerializer.Serialize(decision);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE adaptive_delegation_recovery_decisions SET document=$doc WHERE id=$id";
            command.Parameters.AddWithValue("$doc", document);
            command.Parameters.AddWithValue("$id", decision.DecisionId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new WorkflowStateException("Recovery decision disappeared.");
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO adaptive_evidence(workflow_id,kind,document) VALUES($w,'DelegationRecoveryDecisionUpdated',$doc)";
            command.Parameters.AddWithValue("$w", decision.WorkflowId.ToString("D"));
            command.Parameters.AddWithValue("$doc", document);
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
        return decision;
    }

    private async Task<DelegationRecoveryDecision?> ReadDecisionAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM adaptive_delegation_recovery_decisions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var json = await command.ExecuteScalarAsync(ct) as string;
        return json is null ? null : JsonSerializer.Deserialize<DelegationRecoveryDecision>(json);
    }

    private async Task<bool> WasCommittedAsync(DelegationRecoveryDecision decision, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT digest,status,document FROM adaptive_proposals WHERE id=$id";
        command.Parameters.AddWithValue("$id", decision.ProposalId!.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != decision.ProposalDigest || reader.GetString(1) != "Committed") return false;
        var proposal = JsonSerializer.Deserialize<AdaptiveProposal>(reader.GetString(2));
        if (proposal is null || proposal.Candidate.RevisionId != decision.CandidateRevisionId) return false;
        var history = await revisions.GetHistoryAsync(decision.WorkflowId, ct);
        return history.Any(revision => revision.RevisionId == proposal.Candidate.RevisionId &&
            revision.ParentRevisionId == decision.ActiveRevisionId);
    }

    private async Task<(string Outcome, string Evidence)> LatestAuthorityEvidenceAsync(Guid proposalId, string digest, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome,reason,recorded_at FROM adaptive_authority_decisions WHERE proposal_id=$p AND digest=$d ORDER BY sequence DESC LIMIT 1";
        command.Parameters.AddWithValue("$p", proposalId.ToString("D"));
        command.Parameters.AddWithValue("$d", digest);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return ("Unrecorded", "No durable authority decision row is available; do not infer permission.");
        return (reader.GetString(0), reader.GetString(1) + " @ " + reader.GetString(2));
    }

    private void RequireOperator(string actor)
    {
        if (!authority.IsAuthorizedOperator(actor)) throw new WorkflowAuthorizationException("Operator is not authorized.");
    }

    private static void RequireAction(DelegationRecoveryProjection projection, DelegationRecoveryAction action)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (!projection.AllowedActions.Contains(action)) throw new WorkflowStateException("Recovery action is unavailable for this condition.");
    }

    private static DelegationRecoveryClassification Classify(ExternalOperationRecoveryRecord? record)
    {
        if (record is null || record.RecoveryState is ExternalOperationRecoveryState.StartIntent or ExternalOperationRecoveryState.UnknownAfterRecovery)
            return DelegationRecoveryClassification.ProviderStateUnknown;
        if (record.Result is { } result)
            return result.State == ExternalOperationState.Succeeded
                ? DelegationRecoveryClassification.ObservableComplete : DelegationRecoveryClassification.ObservableFailed;
        if (record.Observation is { } observation)
        {
            if (observation.State == ExternalOperationState.Unknown) return DelegationRecoveryClassification.KnownUnsafeToRetry;
            if (observation.State == ExternalOperationState.Succeeded) return DelegationRecoveryClassification.ObservableComplete;
            if (observation.State is ExternalOperationState.Failed or ExternalOperationState.Cancelled or ExternalOperationState.TimedOut or ExternalOperationState.Rejected)
                return DelegationRecoveryClassification.ObservableFailed;
        }
        if (record.RecoveryState == ExternalOperationRecoveryState.KnownNotReconnectable)
            return DelegationRecoveryClassification.KnownNotReconnectable;
        return record.Capture is not null && record.RecoveryState == ExternalOperationRecoveryState.KnownObservable
            ? DelegationRecoveryClassification.ObservableReconnectable : DelegationRecoveryClassification.ProviderStateUnknown;
    }
}
