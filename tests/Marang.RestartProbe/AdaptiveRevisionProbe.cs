using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.SessionJournal;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Persistence;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.RestartProbe;

/// <summary>Repository-backed, deterministic read/report scenario; never edits the repository under review.</summary>
internal static class AdaptiveRevisionProbe
{
    private static readonly Guid WorkflowId = Guid.Parse("a58f14e7-3eb5-4339-ac6b-5247182d7094");
    private const string Goal = "Review Marang's README and package contracts; produce a repository readiness report.";
    private const string Change = "Use Markdown instead of JSON for the contract assessment, and include restart limitations.";
    private const string Operator = "qualified-operator";

    public static async Task RunAsync(string directory, string? barrier, bool interactive = false)
    {
        Directory.CreateDirectory(directory);
        var database = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(directory, "execution.db"),
            Pooling = false,
            BusyTimeout = TimeSpan.FromSeconds(10)
        });
        var revisions = new SqliteAdaptiveRevisionRepository(database);
        await revisions.InitializeAsync();
        var journal = new SqliteDelegationRecoveryJournal(database);
        await journal.InitializeAsync();
        var workflowStore = new SqliteWorkflowStore(database);
        await using var evidence = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        { RootPath = Path.Combine(directory, "hongxian"), Pooling = false });
        SessionId sessionId;
        var sessionFile = Path.Combine(directory, "session-id");
        if (File.Exists(sessionFile)) sessionId = SessionId.TryParse(File.ReadAllText(sessionFile), out var saved) ? saved : throw new InvalidOperationException("Invalid session binding.");
        else
        {
            sessionId = (await evidence.SessionStore.CreateAsync("aaaw-m1", "repository/readiness")).Id;
            File.WriteAllText(sessionFile, sessionId.Value.ToString("D"));
        }
        var history = await revisions.GetHistoryAsync(WorkflowId);
        var provider = new DurableExternalOperationProvider(new FileReportProvider(directory, barrier), journal, new ProbeFingerprintVerifier());
        var providers = new InMemoryProviderRegistry();
        var catalog = new InMemoryExternalOperationProviderCatalog();
        var descriptor = new ProviderDescriptor("repository-report", [new CapabilityDescriptor("agent.execute", 1)]);
        providers.Register(descriptor); catalog.Register(descriptor, provider);
        var delegation = new DelegationRuntime(new InMemoryDelegationAcceptanceRegistry(), null, providers, catalog,
            fingerprintVerifier: new ProbeFingerprintVerifier(), inputMaterializer: new ProbeMaterializer());
        var ports = new RepositoryPorts(directory, revisions, journal, delegation, provider, workflowStore);
        var executionPorts = new FuwenZhinuExecutionPorts(ports, ports, ports);
        WorkflowRegistry registry;
        if (history.Length == 0)
        {
            var repository = FindRepositoryRoot();
            File.Copy(Path.Combine(repository, "README.md"), Path.Combine(directory, "readme.input"));
            File.Copy(Path.Combine(repository, "tests", "Marang.Tests", "Marang.Tests.csproj"), Path.Combine(directory, "packages.input"));
            var original = await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, "revision-1", null, Goal, Goal,
                "deterministic-planner", Specs(directory, false), executionPorts);
            registry = original.Registry;
            await revisions.ActivateInitialAsync(original.Revision);
            history = [original.Revision];
        }
        else registry = await AdaptivePlanAuthoring.RestoreRegistryAsync(history, executionPorts);
        ports.Bind(history.SelectMany(r => r.Branches));
        await Barrier(directory, barrier, "after-activation");

        var actor = new AuthorityStoreActor("m1", "host", "qualification");
        var hufu = new SqliteAuthorityStore(Path.Combine(directory, "authority.db"), new ProbeStorePolicy());
        var current = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(hufu, actor),
            new CedarAuthorityEvaluator(), new ProbeDecisionRecorder(database));
        var authority = new SqliteAdaptiveSupervisorAuthority(database, revisions, current,
            new AdaptiveAuthorityProfile("m1", "supervisor", "repository", "reports/readiness.md", "write-file", "m1"),
            name => name == Operator);
        await authority.InitializeAsync();
        var supervisor = new AdaptiveSupervisor(revisions, authority);
        var active = await revisions.GetActiveAsync(WorkflowId);
        await PublishAuthority(hufu, actor, active.RevisionId);

        await using var engine = new WorkflowEngine(workflowStore, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(250)
        });
        var executions = new Dictionary<Guid, Task>();
        async Task Dispatch(AdaptiveRevision revision)
        {
            foreach (var branch in revision.Branches)
            {
                if (executions.TryGetValue(branch.Execution.Id, out var previous) && !previous.IsCompleted) continue;
                var state = await workflowStore.GetRunAsync(branch.Execution.Id);
                if (state?.Status == WorkflowStatus.Completed) continue;
                if (state?.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                    throw new InvalidOperationException("Current branch cannot progress: " + branch.Title + "; " + state.Error?.Message);
                var dependencyRuns = branch.Dependencies.Select(d => revision.Branches.Single(b => b.LogicalId == d).Execution.Id);
                var ready = true;
                foreach (var dependency in dependencyRuns)
                    ready &= (await workflowStore.GetRunAsync(dependency))?.Status == WorkflowStatus.Completed;
                if (!ready) continue;
                var input = JsonSerializer.Deserialize<JsonElement>(branch.Execution.InputJson!);
                await engine.StartAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion, input, branch.Execution.Id);
                if ((await workflowStore.GetRunAsync(branch.Execution.Id))!.Status == WorkflowStatus.Completed) continue;
                // A direct Execute may return because a dead process's lease has not expired yet.
                // Keep polling that SAME run; a returned dispatch is not a durable completion.
                executions[branch.Execution.Id] = engine.ExecuteAsync(branch.Execution.Id);
            }
        }

        await evidence.EventStore.AppendAsync(new SessionEventRequest(sessionId,
            SessionParticipantAttribution.System("deterministic-planner", "marang"), SessionEventTypes.UserMessage,
            FixedEventTime(), PayloadJson: JsonSerializer.Serialize(new { Goal }), IdempotencyKey: "aaaw-original-goal"));
        var recoveredPending = await revisions.GetPendingAsync(WorkflowId);
        if (recoveredPending is null) await Dispatch(active);
        if (active.RevisionId == "revision-1")
        {
            if (recoveredPending is null) await WaitFor(async () =>
                (await workflowStore.GetRunAsync(active.Branches.Single(b => b.LogicalId == "inventory").Execution.Id))?.Status == WorkflowStatus.Completed &&
                (await workflowStore.GetRunAsync(active.Branches.Single(b => b.LogicalId == "acceptance").Execution.Id))?.Status == WorkflowStatus.Completed &&
                File.Exists(Path.Combine(directory, "review.started")) &&
                (await journal.GetBindingAsync(active.Branches.Single(b => b.LogicalId == "delegated").Execution.Id,
                    ports.InputDigest(active.Branches.Single(b => b.LogicalId == "delegated")))) is { } binding &&
                (await journal.ListAsync(binding.DelegationId)).Any(r => r.Capture is not null && r.Observation is not null), "parallel progress");
            await Barrier(directory, barrier, "delegated-running");

            var pending = recoveredPending;
            if (pending is null)
            {
                var specs = Specs(directory, true);
                // Preserve intentional continuity; new executions are issued for all changed contracts.
                foreach (var id in new[] { "inventory", "review" })
                {
                    var index = Array.FindIndex(specs, s => s.LogicalId == id);
                    specs[index] = specs[index] with { ExecutionId = active.Branches.Single(b => b.LogicalId == id).Execution.Id };
                }
                var candidate = await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, "revision-2", "revision-1", Goal,
                    Change, "deterministic-replanner", specs, executionPorts, registry);
                ports.Bind(candidate.Revision.Branches);
                var proposal = await supervisor.ProposeAsync(candidate.Revision,
                    "The inventory and ongoing README review remain valid. Earlier acceptance evidence becomes historical; contract work is replaced. One bounded additional reporting pass is required.");
                pending = proposal.Proposal;
                await File.WriteAllTextAsync(Path.Combine(directory, "catch-up.json"), JsonSerializer.Serialize(proposal.View, new JsonSerializerOptions { WriteIndented = true }));
                await evidence.EventStore.AppendAsync(new SessionEventRequest(sessionId,
                    SessionParticipantAttribution.System("operator", "marang"), SessionEventTypes.UserMessage,
                    FixedEventTime(), PayloadJson: JsonSerializer.Serialize(new { Change }), IdempotencyKey: "aaaw-user-change"));
            }
            else
            {
                // Pending candidate definitions are durably in the proposal, not yet activated history.
                var restored = await AdaptivePlanAuthoring.RestoreRegistryAsync([pending.Candidate], executionPorts);
                foreach (var branch in pending.Candidate.Branches)
                {
                    if (history.SelectMany(r => r.Branches).Any(b => b.Execution.WorkflowName == branch.Execution.WorkflowName && b.Execution.WorkflowVersion == branch.Execution.WorkflowVersion)) continue;
                    registry.Register(restored.Get(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion));
                }
                ports.Bind(pending.Candidate.Branches);
            }
            await AdaptiveEvidenceMirror.MirrorAsync(revisions, WorkflowId, evidence.EventStore, sessionId);
            await Barrier(directory, barrier, "proposal-created");
            var digest = AdaptiveRevisionContracts.Digest(pending);
            if (interactive)
            {
                var view = AdaptiveSupervisor.View(active, pending, "Approval required", null);
                Console.WriteLine(view.Explanation);
                foreach (var line in view.Preserved.Concat(view.Affected).Concat(view.Introduced)) Console.WriteLine("- " + line);
                while (true)
                {
                    Console.WriteLine("Choose: Approve, Reject, Cancel, or Ask <question>.");
                    var action = Console.ReadLine() ?? throw new InvalidOperationException("Operator input ended.");
                    if (action.Equals("Approve", StringComparison.OrdinalIgnoreCase)) break;
                    if (action.StartsWith("Ask ", StringComparison.OrdinalIgnoreCase))
                    {
                        await supervisor.DiscussAsync(WorkflowId, Operator, action[4..]);
                        Console.WriteLine("The inventory and README review keep their executions. Contract/acceptance work is replaced; earlier results stay in history. Approval applies only to this exact proposal.");
                        continue;
                    }
                    if (action.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        await supervisor.CancelAsync(WorkflowId, Operator);
                        File.WriteAllText(Path.Combine(directory, "release-work"), "cancelled");
                        await AdaptiveEvidenceMirror.MirrorAsync(revisions, WorkflowId, evidence.EventStore, sessionId);
                        Console.WriteLine("Workflow cancelled. Earlier evidence is retained.");
                        return;
                    }
                    if (action.Equals("Reject", StringComparison.OrdinalIgnoreCase))
                    {
                        await supervisor.RejectAsync(pending, digest, Operator);
                        File.WriteAllText(Path.Combine(directory, "release-work"), "rejected revision");
                        await WaitFor(async () =>
                        {
                            foreach (var branch in active.Branches.Where(b => b.Dependencies.Length == 0))
                                if ((await workflowStore.GetRunAsync(branch.Execution.Id))?.Status != WorkflowStatus.Completed) return false;
                            return true;
                        }, "original workflow completion");
                        await Dispatch(active);
                        await WaitFor(async () => (await workflowStore.GetRunAsync(active.Branches.Single(b => b.LogicalId == "outcome").Execution.Id))?.Status == WorkflowStatus.Completed,
                            "original outcome");
                        await AdaptiveEvidenceMirror.MirrorAsync(revisions, WorkflowId, evidence.EventStore, sessionId);
                        Console.WriteLine("Revision rejected. Original workflow completed under its original contract.");
                        return;
                    }
                }
            }
            var receipt = await authority.FindApprovalAsync(pending.ProposalId, digest);
            if (receipt is null) receipt = await supervisor.ApproveAsync(pending, digest, Operator);
            await Barrier(directory, barrier, "approved-before-commit");
            if (barrier == "during-fencing" && !File.Exists(Path.Combine(directory, "barrier-used")))
            {
                typeof(SqliteAdaptiveRevisionRepository).GetProperty("QualificationCheckpoint", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(revisions, (Action<string>)(name => Barrier(directory, barrier, name).GetAwaiter().GetResult()));
            }
            await supervisor.CommitAsync(pending, digest, receipt);
            active = await revisions.GetActiveAsync(WorkflowId);
            await Barrier(directory, barrier, "committed-before-dispatch");
        }
        // A committed revision's new pending runs are already durable. Dispatch can be replayed safely.
        await Dispatch(active);
        await WaitFor(async () =>
        {
            await Dispatch(active);
            if (!File.Exists(Path.Combine(directory, "replacement.started"))) return false;
            var replacement = active.Branches.Single(b => b.LogicalId == "delegated");
            var binding = await journal.GetBindingAsync(replacement.Execution.Id, ports.InputDigest(replacement));
            return binding is not null && (await journal.ListAsync(binding.DelegationId))
                .Any(operation => operation.Capture is not null && operation.Observation is not null);
        }, "replacement execution with durable provider capture");
        if (barrier == "replacement-running" && !File.Exists(Path.Combine(directory, "barrier-used")))
        {
            var replacement = active.Branches.Single(b => b.LogicalId == "delegated");
            var binding = (await journal.GetBindingAsync(replacement.Execution.Id, ports.InputDigest(replacement)))!;
            var operation = (await journal.ListAsync(binding.DelegationId)).Single(r => r.Capture is not null && r.Observation is not null);
            File.WriteAllText(Path.Combine(directory, "replacement-capture.json"), JsonSerializer.Serialize(new
            { replacement.Execution.Id, DelegationId = binding.DelegationId.Value, ProviderHandle = operation.Capture!.Handle.Value, HandleCaptured = true }));
        }
        await Barrier(directory, barrier, "replacement-running");
        File.WriteAllText(Path.Combine(directory, "release-work"), "continue");
        await WaitFor(async () =>
        {
            await Dispatch(active);
            foreach (var branch in active.Branches.Where(b => b.Dependencies.Length == 0))
                if ((await workflowStore.GetRunAsync(branch.Execution.Id))?.Status != WorkflowStatus.Completed) return false;
            return true;
        }, "revised branch completion");
        await Dispatch(active);
        await WaitFor(async () => (await workflowStore.GetRunAsync(active.Branches.Single(b => b.LogicalId == "outcome").Execution.Id))?.Status == WorkflowStatus.Completed,
            "current final outcome");

        // Recover the old provider attempt for historical collection after fencing, never for active-result adoption.
        var originalRevision = (await revisions.GetHistoryAsync(WorkflowId))[0];
        var oldBranch = originalRevision.Branches.Single(b => b.LogicalId == "delegated");
        var oldBinding = await journal.GetBindingAsync(oldBranch.Execution.Id, ports.InputDigest(oldBranch));
        var oldRecords = await journal.ListAsync(oldBinding!.DelegationId);
        foreach (var record in oldRecords.Where(r => r.Capture is not null))
        {
            await provider.StartAsync(record.Intent.Request, new EmptyCaptureSink());
            await provider.ObserveAsync(record.Capture!.Handle);
            await provider.GetResultAsync(record.Capture.Handle);
        }
        await AdaptiveEvidenceMirror.MirrorAsync(revisions, WorkflowId, evidence.EventStore, sessionId);
        var final = await workflowStore.GetRunAsync(active.Branches.Single(b => b.LogicalId == "outcome").Execution.Id);
        var preserved = originalRevision.Branches.Where(b => b.LogicalId is "inventory" or "review");
        foreach (var branch in preserved)
            Ensure(active.Branches.Single(b => b.LogicalId == branch.LogicalId).Execution.Id == branch.Execution.Id, "preserved execution identity");
        Ensure(final!.OutputJson!.Contains("Markdown", StringComparison.Ordinal), "final outcome uses revised requirement");
        Ensure(final.OutputJson.Contains("Markdown contract report including restart limitations", StringComparison.Ordinal),
            "current delegated result satisfies the revised requirement");
        Ensure((await workflowStore.GetRunAsync(oldBranch.Execution.Id))!.Status != WorkflowStatus.Completed, "late provider cannot complete obsolete run");
        var oldLocal = originalRevision.Branches.Single(b => b.LogicalId == "local");
        Ensure((await workflowStore.GetRunAsync(oldLocal.Execution.Id))!.OutputJson is null, "late local output cannot become authoritative");
        var oldAcceptance = originalRevision.Branches.Single(b => b.LogicalId == "acceptance");
        Ensure((await workflowStore.GetRunAsync(oldAcceptance.Execution.Id))!.Status == WorkflowStatus.Completed,
            "invalidated completed evidence must remain historical");
        Ensure(!final.OutputJson.Contains("Validate JSON contract report", StringComparison.Ordinal), "invalidated completion must not feed new outcome");
        Ensure(File.ReadAllLines(Path.Combine(directory, "provider-starts.log")).Length == 2, "only original and replacement provider starts");
        Ensure(File.ReadAllLines(Path.Combine(directory, "inventory.starts")).Length == 1, "completed inventory was not executed again");
        var summary = new
        {
            ActiveRevision = active.RevisionId,
            RevisionCount = (await revisions.GetHistoryAsync(WorkflowId)).Length,
            UnaffectedExecutionPreserved = true,
            CompletedInventoryReused = true,
            InvalidatedAcceptanceHistorical = true,
            SupersededProviderHistorical = (await journal.ListAsync(oldBinding.DelegationId)).All(r => r.Result is not null),
            ProviderStarts = File.ReadAllLines(Path.Combine(directory, "provider-starts.log")).Length,
            FinalOutcome = final.OutputJson,
            Evidence = await revisions.GetEvidenceAsync(WorkflowId)
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "adaptive-result.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        await evidence.EventStore.AppendAsync(new SessionEventRequest(sessionId,
            SessionParticipantAttribution.System("adaptive-supervisor", "marang"), SessionEventTypes.ExecutionCompleted,
            FixedEventTime(), PayloadJson: JsonSerializer.Serialize(summary), IdempotencyKey: "aaaw-final-outcome"));
        await File.WriteAllTextAsync(Path.Combine(directory, "demonstration.md"),
            $"# Durable adaptive repository review\n\nGoal: {Goal}\n\nUser change: {Change}\n\nThe inventory completed and was reused. README review continued with the same execution. Original contract assessment and acceptance evidence were fenced; their results remain historical. The approved Markdown revision completed.\n\nRevisions retained: {summary.RevisionCount}. Provider starts: {summary.ProviderStarts}. Restart barrier: {barrier ?? "none"}.\n\nFinal outcome: {JsonSerializer.Deserialize<string>(final.OutputJson!)}\n");
    }

    private static AdaptiveWorkSpec[] Specs(string dir, bool revised)
    {
        var provenance = AdaptiveRevisionContracts.Digest(File.ReadAllText(Path.Combine(dir, "readme.input")) + File.ReadAllText(Path.Combine(dir, "packages.input")));
        AdaptiveWorkSpec Spec(string id, string title, string requirement, string[]? dependencies = null) =>
            new(id, title, requirement, provenance, "m1/bounded-repository-read-report", provenance, dependencies ?? [], Guid.NewGuid(), true);
        return [Spec("inventory", "Package inventory", "Identify the published package dependencies"),
            Spec("acceptance", "Acceptance evidence", revised ? "Validate Markdown and restart limitations" : "Validate JSON contract report"),
            Spec("review", "README review", "Review consumer guidance in the README"),
            Spec("local", "Local contract assessment", revised ? "Assess Markdown compatibility and restart limitations" : "Assess JSON compatibility"),
            Spec("delegated", "Delegated contract assessment", revised ? "Markdown contract report including restart limitations" : "JSON contract report"),
            Spec("outcome", "Final report", revised ? "Produce Markdown readiness report" : "Produce JSON readiness report",
                ["inventory", "acceptance", "review", "local", "delegated"])];
    }

    public static async Task VerifyUnknownAsync(string directory)
    {
        var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "execution.db"), Pooling = false });
        var revisions = new SqliteAdaptiveRevisionRepository(database); await revisions.InitializeAsync();
        var journal = new SqliteDelegationRecoveryJournal(database); await journal.InitializeAsync();
        var active = await revisions.GetActiveAsync(WorkflowId);
        var branch = active.Branches.Single(b => b.LogicalId == "delegated");
        var binding = await journal.GetBindingAsync(branch.Execution.Id,
            AdaptiveRevisionContracts.Digest(JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)));
        var record = (await journal.ListAsync(binding!.DelegationId)).Single();
        Ensure(record.Capture is null, "unknown-operation qualification must have no captured handle");
        var provider = new DurableExternalOperationProvider(new FileReportProvider(directory, null), journal, new ProbeFingerprintVerifier());
        var parked = false;
        try { await provider.StartAsync(record.Intent.Request, new EmptyCaptureSink()); }
        catch (ExternalOperationRecoveryException exception) when (exception.State == ExternalOperationRecoveryState.UnknownAfterRecovery) { parked = true; }
        Ensure(parked, "unknown operation must require resolution");
        Ensure(File.ReadAllLines(Path.Combine(directory, "provider-starts.log")).Length == 1, "unknown provider must not respawn");
        await File.WriteAllTextAsync(Path.Combine(directory, "unknown-result.json"), JsonSerializer.Serialize(new
        { State = "UnknownAfterRecovery", RequiresOperatorResolution = true, ProviderStarts = 1, ActiveRevision = active.RevisionId }));
        await File.WriteAllTextAsync(Path.Combine(directory, "unknown-catch-up.md"),
            "The delegated report may still be running, but its provider handle was not captured before the process stopped. Automatic restart is blocked. Reconcile the provider operation or cancel the workflow; a fresh execution requires a separate safety decision.\n");
    }

    private static async Task PublishAuthority(SqliteAuthorityStore store, AuthorityStoreActor actor, string parent)
    {
        var context = new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), parent, "m1");
        var now = DateTimeOffset.UtcNow;
        // A trusted fixture issues a finite report-only grant; arbitrary repository mutation is excluded.
        var result = await store.PublishAsync(new AuthorityPublishCommand("m1-issue-" + parent, actor,
            new AuthoritySnapshot(context, "m1-report-grant/1", [new AuthorityLayer("report", [new AuthorityGrant(
                "report-only", [AuthorityAction.WriteFile], new AuthorityScope("repository", "reports", AuthorityScopeKind.Subtree), [],
                now.AddMinutes(-1), now.AddDays(1))])], [], now.AddDays(1)), 0));
        // Exact snapshots include issuance times; an existing published context is already sufficient on recovery.
        _ = result;
    }
    private static async Task Barrier(string directory, string? requested, string current)
    {
        if (requested != current || File.Exists(Path.Combine(directory, "barrier-used"))) return;
        File.WriteAllText(Path.Combine(directory, "barrier-ready"), current);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    private static async Task WaitFor(Func<Task<bool>> condition, string reason)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(90);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until) throw new TimeoutException(reason);
            await Task.Delay(20);
        }
    }
    private static void Ensure(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static DateTimeOffset FixedEventTime() => new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Marang.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository-backed probe must run from a repository build.");
    }
    private sealed class ProbeStorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }
    private sealed class ProbeDecisionRecorder(IZhinuSqliteDatabase database) : IAuthorityDecisionRecorder
    {
        public async ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS probe_hufu_decisions (document TEXT NOT NULL); INSERT INTO probe_hufu_decisions(document) VALUES($d)";
            command.Parameters.AddWithValue("$d", JsonSerializer.Serialize(new { request.RequestIdentity, decision.Status, decision.SnapshotIdentity, decision.ReasonCode }));
            await command.ExecuteNonQueryAsync(cancellationToken); return true;
        }
    }
    private sealed class ProbeFingerprintVerifier : IExternalOperationSemanticFingerprintVerifier
    {
        public string Compute(ExternalOperationSemanticInputEnvelope input) => AdaptiveRevisionContracts.Digest(input);
        public bool Matches(ExternalOperationStartIdentity identity, ExternalOperationSemanticInputEnvelope input) =>
            identity.DelegationId == input.DelegationId && identity.SemanticFingerprint == Compute(input);
    }
    private sealed class EmptyCaptureSink : IExternalOperationHandleCaptureSink
    {
        public ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class ProbeMaterializer : IDelegationInputMaterializer
    {
        public MaterializedDelegationInputs Materialize(DelegationRequest request, DelegationId id)
        {
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Objective))).ToLowerInvariant();
            var artifact = new DelegationArtifactReference(id, new StructuralNodeReference("delegation"), new NodeGenerationId(id.Value),
                "repository-report", "m1-inputs", contentHash, "read-report-input", 1, "artifact:" + contentHash,
                ArtifactContentIdentity.Sha256Bytes(contentHash));
            return new MaterializedDelegationInputs([artifact], null, null, new ExternalAgentReference("repository-report", "bounded-repository-review", "m1-v1"));
        }
    }

    private sealed class RepositoryPorts(string directory, SqliteAdaptiveRevisionRepository revisions,
        SqliteDelegationRecoveryJournal journal, DelegationRuntime runtime, DurableExternalOperationProvider provider, IWorkflowRepository workflowStore)
        : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        private readonly Dictionary<string, Guid> executions = new(StringComparer.Ordinal);
        public void Bind(IEnumerable<AdaptiveBranch> branches)
        {
            lock (executions)
                foreach (var branch in branches) executions[JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!] = branch.Execution.Id;
        }
        public string InputDigest(AdaptiveBranch branch) => AdaptiveRevisionContracts.Digest(JsonSerializer.Deserialize<string>(branch.Execution.InputJson!));

        public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var facts = JsonDocument.Parse(input);
            var logical = facts.RootElement.GetProperty("LogicalId").GetString()!;
            var requirement = facts.RootElement.GetProperty("Requirement").GetString()!;
            Guid executionId; lock (executions) executionId = executions[input];
            MarkOnce(Path.Combine(directory, executionId.ToString("N") + ".started"), Path.Combine(directory, logical + ".starts"));
            if (logical is "review" or "local")
            {
                File.WriteAllText(Path.Combine(directory, logical + ".started"), "running");
                if (requirement.Contains("Markdown", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(directory, "replacement.started"), "running");
                // Deliberately non-cooperative old local work: results must be fenced even when cancellation is ignored.
                while (!File.Exists(Path.Combine(directory, "release-work"))) await Task.Delay(20, CancellationToken.None);
            }
            string summary;
            if (logical == "delegated")
            {
                try { summary = await Delegate(executionId, input, cancellationToken); }
                catch (Exception exception)
                {
                    File.WriteAllText(Path.Combine(directory, "delegation-error.log"), exception.ToString());
                    File.WriteAllText(Path.Combine(directory, "release-work"), "resolve failure");
                    throw;
                }
            }
            else if (logical == "outcome")
            {
                var active = await revisions.GetActiveAsync(WorkflowId, cancellationToken);
                var results = new List<string>();
                foreach (var branch in active.Branches.Where(b => b.LogicalId != "outcome"))
                {
                    var run = await workflowStore.GetRunAsync(branch.Execution.Id, cancellationToken);
                    if (run?.Status != WorkflowStatus.Completed) throw new InvalidOperationException("Current prerequisite is incomplete.");
                    results.Add(branch.Title + ": " + JsonSerializer.Deserialize<string>(run.OutputJson!));
                }
                summary = requirement + "\n" + string.Join("\n", results);
            }
            else
            {
                var readme = await File.ReadAllTextAsync(Path.Combine(directory, "readme.input"), CancellationToken.None);
                var packages = await File.ReadAllTextAsync(Path.Combine(directory, "packages.input"), CancellationToken.None);
                summary = requirement + $"; README evidence {readme.Split('\n').Length} lines; package evidence {packages.Split("PackageReference", StringSplitOptions.None).Length - 1} references.";
                File.WriteAllText(Path.Combine(directory, executionId.ToString("N") + ".result"), summary);
            }
            return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(summary)));
        }

        private async Task<string> Delegate(Guid executionId, string input, CancellationToken ct)
        {
            var digest = AdaptiveRevisionContracts.Digest(input);
            var binding = await journal.GetBindingAsync(executionId, digest, ct);
            if (binding is null)
            {
                var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
                await File.WriteAllTextAsync(Path.Combine(directory, contentHash + ".input"), input, ct);
                var handle = await runtime.DelegateAsync(new DelegationCallerScope("aaaw-host"), new DelegationRequest(
                    executionId.ToString("D"), input, "repository-report", new WorkspaceReference("local", "repository", "snapshot/v1"),
                    ["Return a bounded repository evidence report"], [], new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 0)), ct);
                // Acceptance alone cannot start provider work. Bind durably before the first runtime pump.
                await journal.BindAsync(executionId, handle, digest, ct);
                while (true)
                {
                    var snapshot = await runtime.GetAsync(handle.DelegationId, ct);
                    if (DelegationLifecycle.IsTerminal(snapshot.Progress.State))
                    {
                        if (snapshot.Progress.State != DelegationState.Completed) throw new InvalidOperationException("Delegation requires operator resolution.");
                        var terminal = (await journal.ListAsync(handle.DelegationId, ct)).Single().Result;
                        if (terminal?.State != ExternalOperationState.Succeeded) throw new InvalidOperationException("Accepted delegation has no durable successful result.");
                        return terminal.Summary;
                    }
                    await runtime.PumpAsync(handle.DelegationId, snapshot.Progress.Revision, ct);
                    var operation = (await journal.ListAsync(handle.DelegationId, ct)).SingleOrDefault();
                    if (operation?.Observation?.State == ExternalOperationState.Running)
                    {
                        // The deterministic external fixture completes only after release. Park supervision
                        // instead of burning Qingniao's finite worker/stall budget with identical observations.
                        while (!File.Exists(Path.Combine(directory, "release-work"))) await Task.Delay(20, ct);
                    }
                    await Task.Delay(20, ct);
                }
            }
            // Recovery is observation of the SAME Qingniao attempt, never a new acceptance or provider start.
            var records = await journal.ListAsync(binding.DelegationId, ct);
            if (records.Length != 1 || records[0].Capture is null)
                throw new ExternalOperationRecoveryException(ExternalOperationRecoveryState.UnknownAfterRecovery);
            var record = records[0];
            await provider.StartAsync(record.Intent.Request, new EmptyCaptureSink(), ct);
            while (true)
            {
                var observation = await provider.ObserveAsync(record.Capture!.Handle, ct);
                if (observation.State is ExternalOperationState.Succeeded or ExternalOperationState.Failed or
                    ExternalOperationState.Cancelled or ExternalOperationState.TimedOut or ExternalOperationState.Rejected)
                {
                    var result = await provider.GetResultAsync(record.Capture.Handle, ct);
                    if (result.State != ExternalOperationState.Succeeded) throw new InvalidOperationException("Recovered provider result is not successful.");
                    return result.Summary;
                }
                await Task.Delay(20, ct);
            }
        }
        private static void MarkOnce(string marker, string log)
        {
            try
            {
                using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                File.AppendAllText(log, "started\n");
            }
            catch (IOException) when (File.Exists(marker)) { }
        }
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed record ProviderOperation(ExternalOperationHandle Handle, DateTimeOffset StartedAt, string InputHash, string Requirement);
    private sealed class FileReportProvider(string directory, string? barrier) : IExternalOperationProvider
    {
        public async ValueTask<ExternalOperationStartReceipt> StartAsync(ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default)
        {
            var artifact = request.InputArtifacts.Single();
            var input = await File.ReadAllTextAsync(Path.Combine(directory, artifact.ArtifactId + ".input"), cancellationToken);
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
            if (artifact.ContentIdentity.Hash != contentHash) throw new InvalidOperationException("Provider input content mismatch.");
            using var facts = JsonDocument.Parse(input);
            var requirement = facts.RootElement.GetProperty("Requirement").GetString()!;
            var taskId = Guid.NewGuid().ToString("N");
            var original = request.Correlation;
            var correlation = new ExternalOperationCorrelation(original.DelegationId, original.WorkflowRun, original.StructuralNode,
                original.NodeGeneration, original.ExecutionAttemptId, original.Agent, new ExternalTaskReference(original.Agent.Provider, taskId));
            var handle = new ExternalOperationHandle(original.Agent.Provider, taskId, original.Agent.ProtocolVersion, correlation);
            var operation = new ProviderOperation(handle, DateTimeOffset.UtcNow, contentHash, requirement);
            await File.WriteAllTextAsync(Path.Combine(directory, taskId + ".provider"), JsonSerializer.Serialize(operation), cancellationToken);
            await File.AppendAllTextAsync(Path.Combine(directory, "provider-starts.log"), "started\n", cancellationToken);
            await Barrier(directory, barrier, "unknown-provider");
            if (requirement.Contains("Markdown", StringComparison.Ordinal)) File.WriteAllText(Path.Combine(directory, "replacement.started"), "running");
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, operation.StartedAt), cancellationToken);
            return new ExternalOperationStartReceipt(request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, operation.StartedAt);
        }
        public async ValueTask<ExternalOperationObservation> ObserveAsync(ExternalOperationHandle handle, CancellationToken cancellationToken = default)
        {
            var operation = await Read(handle, cancellationToken);
            var completed = File.Exists(Path.Combine(directory, "release-work"));
            return new ExternalOperationObservation(handle, completed ? 2 : 1,
                completed ? ExternalOperationState.Succeeded : ExternalOperationState.Running,
                completed ? operation.StartedAt.AddMinutes(1) : operation.StartedAt, resultAvailable: completed);
        }
        public async ValueTask<ExternalOperationResult> GetResultAsync(ExternalOperationHandle handle, CancellationToken cancellationToken = default)
        {
            var operation = await Read(handle, cancellationToken);
            if (!File.Exists(Path.Combine(directory, "release-work"))) throw new InvalidOperationException("Provider work still in progress.");
            var readme = await File.ReadAllTextAsync(Path.Combine(directory, "readme.input"), cancellationToken);
            var summary = operation.Requirement + $"; repository README has {readme.Split('\n').Length} lines. Restart state was inspected.";
            return new ExternalOperationResult(handle, ExternalOperationState.Succeeded, operation.StartedAt.AddMinutes(1), summary, []);
        }
        private async Task<ProviderOperation> Read(ExternalOperationHandle handle, CancellationToken ct)
        {
            var operation = JsonSerializer.Deserialize<ProviderOperation>(await File.ReadAllTextAsync(Path.Combine(directory, handle.Value + ".provider"), ct), DelegationRecoveryJson.CreateOptions())!;
            if (operation.Handle != handle) throw new InvalidOperationException("Provider operation identity mismatch.");
            return operation;
        }
        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(ExternalOperationCancelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The M1 provider is observe-only; runtime revision uses result fencing.");
        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("M1 reconnect observes an existing operation; it does not resume execution.");
    }
}
