using System.Text.Json;
using Marang.DelegationAuthority;
using Marang.Http;
using Marang.Mcp;
using Marang.Runs;
using Marang.SessionJournal;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Marang.RestartProbe;

// M5.5 cross-process durability probe. Phase 1 sets up correlated durable
// state and runs a bounded workflow to a parked wait, then exits (process
// death is the crash). Phase 2 runs in a FRESH process against the same files
// and verifies/resumes. State crosses the boundary ONLY via files.
internal static class Program
{
    private const string Caller = "restart-probe";
    private const string WorkflowName = "demo-restart";
    private const string WorkflowVersion = "1";

    public static async Task<int> Main(string[] args)
    {
        string? phase = null;
        string? dir = null;
        string? barrier = null;
        var interactive = false;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--phase") phase = args[i + 1];
            if (args[i] == "--dir") dir = args[i + 1];
            if (args[i] == "--barrier") barrier = args[i + 1];
            if (args[i] == "--interactive") interactive = args[i + 1] == "true";
        }

        if (string.IsNullOrWhiteSpace(phase) || string.IsNullOrWhiteSpace(dir))
        {
            await Console.Error.WriteLineAsync("usage: --phase 1|2 --dir <dir>");
            return 2;
        }

        try
        {
            if (phase == "1") await Phase1Async(dir);
            else if (phase == "2") await Phase2Async(dir);
            else if (phase == "adaptive") await AdaptiveRevisionProbe.RunAsync(dir, barrier, interactive);
            else if (phase == "adaptive-unknown") await AdaptiveRevisionProbe.VerifyUnknownAsync(dir);
            else throw new ArgumentException($"Unknown phase '{phase}'.");
            return 0;
        }
        catch (Exception exception)
        {
            var error = new Dictionary<string, string>
            {
                ["error"] = exception.GetType().FullName ?? "error",
                ["message"] = exception.Message,
            };
            await File.WriteAllTextAsync(
                Path.Combine(dir, phase == "1" ? "phase1-error.json" : "phase2-error.json"),
                JsonSerializer.Serialize(error));
            await Console.Error.WriteLineAsync(exception.ToString());
            return 1;
        }
    }

    private static async Task Phase1Async(string dir)
    {
        Directory.CreateDirectory(dir);
        var zhinuDb = Path.Combine(dir, "zhinu.db");
        var hongxianRoot = Path.Combine(dir, "hongxian");
        var planFile = Path.Combine(dir, "1.json");
        var logPath = Path.Combine(dir, "invocations.log");
        var ct = CancellationToken.None;

        // Declared Fuwen topology (also written for phase-2 plan loading).
        var plan = BuildPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        await File.WriteAllTextAsync(planFile, JsonSerializer.Serialize(plan), ct);

        // Hongxian session + first event.
        await using var hongxian = new HongxianSqliteStoreSet(
            new HongxianSqliteOptions { RootPath = hongxianRoot, Pooling = false });
        var session = await hongxian.SessionStore.CreateAsync("restart-ctx", "resource/restart", cancellationToken: ct);
        await hongxian.EventStore.AppendAsync(
            new SessionEventRequest(
                session.Id,
                SessionParticipantAttribution.System("restart-probe", "marang"),
                SessionEventTypes.ExecutionStarted,
                DateTimeOffset.UtcNow), ct);

        // Marang acceptance through the durable ordered protocol: the
        // descriptor plus visibility binding persist beside Zhinu/Hongxian
        // state so a fresh process can reattach the same caller.
        var authorityDb = Path.Combine(dir, "authority.db");
        var catalog = new MarangDelegationCatalog(
            new SqliteAcceptedDelegationStore(authorityDb),
            new SqliteDelegationVisibilityStore(authorityDb));
        var acceptance = await catalog.AcceptAsync(
            new DelegationCallerScope(Caller),
            new DelegationRequest(
                "restart-1",
                "Run one bounded restartable task",
                "test-provider",
                new WorkspaceReference("local", "workspace", "revision"),
                ["Done"],
                [],
                new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
            ct);
        // A secondary acceptance whose visibility the fresh process will
        // revoke, proving terminal revocation without disturbing the main
        // binding the driver independently verifies.
        var secondary = await catalog.AcceptAsync(
            new DelegationCallerScope(Caller),
            new DelegationRequest(
                "restart-revoke-me",
                "Secondary acceptance for revocation proof",
                "test-provider",
                new WorkspaceReference("local", "workspace", "revision"),
                ["Done"],
                [],
                new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
            ct);
        var delegationId = acceptance.DelegationId;
        var generation = delegationId.Value.ToString("D");

        // Zhinu run correlated to the delegation, plan, and session.
        var correlation = new RunCorrelation(
            delegationId,
            generation,
            Guid.Empty,
            "1",
            map.ExecutionFingerprint,
            session.Id.Value.ToString("D"));
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuDb });
        var workflow = new FetchThenWaitWorkflow(logPath);
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(WorkflowName, WorkflowVersion, workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
        var runId = await engine.StartAsync(WorkflowName, WorkflowVersion, "go", cancellationToken: ct);
        correlation = correlation with { WorkflowRunId = runId };
        await store.UpdateRunMetadataAsync(runId, correlation.ToMetadataJson(), ct);

        // Run in the background; the foreground polls until the wait parks,
        // then this process exits (the crash) without awaiting completion.
        var execution = engine.ExecuteAsync(runId, CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            var steps = await engine.GetStepsAsync(runId, ct);
            var fetch = steps.FirstOrDefault(s => s.StepKey == "demo/fetch");
            var approval = steps.FirstOrDefault(s => s.StepKey == "demo/approval");
            if (fetch?.Status == StepStatus.Completed && approval?.Status == StepStatus.Waiting)
            {
                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The workflow did not park at the wait step.");
            }

            await Task.Delay(50, ct);
        }

        await hongxian.EventStore.AppendAsync(
            new SessionEventRequest(
                session.Id,
                SessionParticipantAttribution.System("restart-probe", "marang"),
                SessionEventTypes.UserMessage,
                DateTimeOffset.UtcNow), ct);
        var events = await engine.GetEventsAsync(runId, 0, 1000, ct);
        var manifest = new Dictionary<string, string>
        {
            ["runId"] = runId.ToString("D"),
            ["sessionId"] = session.Id.Value.ToString("D"),
            ["delegationId"] = delegationId.Value.ToString("D"),
            ["secondaryDelegationId"] = secondary.DelegationId.Value.ToString("D"),
            ["generation"] = generation,
            ["planRevision"] = "1",
            ["executionFingerprint"] = map.ExecutionFingerprint,
            ["caller"] = Caller,
            ["tenant"] = MarangPrincipal.DefaultTenant,
            ["requestKey"] = "restart-1",
            ["acceptanceKey"] = AcceptanceIdentity.AcceptanceKey(MarangPrincipal.DefaultTenant, Caller, "restart-1"),
            ["fingerprint"] = acceptance.Fingerprint.ToString(),
            ["zhinuDb"] = zhinuDb,
            ["hongxianRoot"] = hongxianRoot,
            ["authorityDb"] = authorityDb,
            ["planFile"] = planFile,
            ["logPath"] = logPath,
            ["maxEventSequence"] = events.Count == 0 ? "0" : events.Max(e => e.Sequence).ToString(),
            ["invocationCount"] = (File.Exists(logPath) ? await File.ReadAllLinesAsync(logPath, ct) : []).Length.ToString(),
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "phase1.json"), JsonSerializer.Serialize(manifest), ct);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _ = execution;
    }

    private static async Task Phase2Async(string dir)
    {
        var ct = CancellationToken.None;
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(Path.Combine(dir, "phase1.json"), ct))
            ?? throw new InvalidOperationException("Phase 1 manifest is missing.");
        string Get(string key) => manifest.TryGetValue(key, out var value) ? value : throw new InvalidOperationException($"Manifest lacks '{key}'.");
        var runId = Guid.Parse(Get("runId"));
        var sessionId = SessionId.Parse(Get("sessionId"));
        var delegationId = new DelegationId(Guid.Parse(Get("delegationId")));
        var generation = Get("generation");
        var fingerprint = Get("executionFingerprint");
        var zhinuDb = Get("zhinuDb");
        var hongxianRoot = Get("hongxianRoot");
        var planFile = Get("planFile");
        var logPath = Get("logPath");
        var oldMaxSequence = long.Parse(Get("maxEventSequence"));
        var oldInvocations = int.Parse(Get("invocationCount"));

        var results = new Dictionary<string, string>();
        void Check(string name, bool condition, string detail = "")
        {
            results[name] = condition ? "pass" : $"FAIL:{detail}";
            if (!condition) throw new InvalidOperationException($"Check failed: {name} {detail}");
        }

        // Reopen durable stores (fresh objects, same files).
        var store = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuDb });
        await using var hongxian = new HongxianSqliteStoreSet(
            new HongxianSqliteOptions { RootPath = hongxianRoot, Pooling = false });

        // Identity continuity from durable rows.
        var run = await store.GetRunAsync(runId, ct);
        Check("sameRun", run is not null && run.Id == runId);
        Check("runNotTerminal", run!.Status == WorkflowStatus.Running);
        var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, runId);
        Check("sameCorrelation", correlation is not null
            && correlation.DelegationId == delegationId
            && correlation.Generation == generation
            && correlation.ExecutionFingerprint == fingerprint
            && correlation.SessionId == sessionId.Value.ToString("D"));
        Check("sameSession", correlation!.SessionId == sessionId.Value.ToString("D"));

        // Declared topology from the durable plan file; fingerprint must match.
        var planJson = await File.ReadAllTextAsync(planFile, ct);
        var plan = JsonSerializer.Deserialize<WorkflowPlan>(planJson)
            ?? throw new InvalidOperationException("The plan file did not deserialize.");
        var map = FuwenZhinuStepMapper.Map(plan);
        Check("sameFingerprint", map.ExecutionFingerprint == fingerprint);
        Check("samePlanRevision", map.PlanRevision == "1");

        // Execution state + authoritative step classification (published contract).
        var steps = await store.GetStepsAsync(runId, ct);
        Check("fetchCompleted", steps.Any(s => s.StepKey == "demo/fetch" && s.Status == StepStatus.Completed));
        Check("approvalWaiting", steps.Any(s => s.StepKey == "demo/approval" && s.Status == StepStatus.Waiting));
        foreach (var step in steps)
        {
            Check($"match:{step.StepKey}", FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out _));
        }

        // Durable sequences are monotonic across the restart.
        var events = await store.GetEventsAsync(runId, 0, 1000, ct);
        var maxSequence = events.Count == 0 ? 0 : events.Max(e => e.Sequence);
        Check("sequenceMonotonic", maxSequence >= oldMaxSequence, $"{maxSequence} < {oldMaxSequence}");

        // Session journal reconstructs with consistent state.
        var snapshot = await hongxian.ReadReconciledProjectionAsync(sessionId, ct);
        Check("journalAvailable", snapshot is not null);
        Check("journalConsistent", snapshot!.State.TotalEvents >= 2);

        // Caller-visibility reattachment through durable authority: a fresh
        // process with empty memory resolves the same caller via the
        // descriptor plus visibility binding (verify-only, never repaired).
        var authorityDb = Get("authorityDb");
        var durableCatalog = new MarangDelegationCatalog(
            new SqliteAcceptedDelegationStore(authorityDb),
            new SqliteDelegationVisibilityStore(authorityDb));
        Check("preReattachHidden", durableCatalog.Find(delegationId, Caller) is null);
        Check("reattach", await durableCatalog.ReattachAsync(Caller, delegationId, ct));
        var record = durableCatalog.Find(delegationId, Caller);
        Check("detailEnvelope", record is not null
            && record.Objective == "Run one bounded restartable task"
            && record.Provider == "test-provider"
            && record.Workspace == "workspace"
            && string.Equals(record.Caller, Caller, StringComparison.Ordinal));

        // Same-caller operator reads through the public endpoint surface,
        // served from durable Zhinu/Hongxian truth after reattachment.
        var sources = RunStoreComposition.Create(Get("zhinuDb"), hongxianRoot, dir);
        try
        {
            var (delegationRunStatus, delegationRunBody) = await RunProjectionEndpoints.GetDelegationRunAsync(
                durableCatalog, Caller, delegationId.Value, sources, ct);
            Check("delegationRun", delegationRunStatus == 200, delegationRunStatus.ToString());
            Check("delegationRunId", delegationRunBody is not null
                && delegationRunBody.GetType().GetProperty("runId")?.GetValue(delegationRunBody)?.ToString() == runId.ToString("D"));

            var (runStatus, _) = await RunProjectionEndpoints.GetRunAsync(
                durableCatalog, Caller, runId, sources, static () => DateTimeOffset.UtcNow, ct);
            Check("runProjection", runStatus == 200, runStatus.ToString());

            var (journalStatus, journalBody) = await SessionJournalEndpoints.GetJournalAsync(
                durableCatalog, Caller, runId, sources, sources, ct);
            Check("journalEndpoint", journalStatus == 200, journalStatus.ToString());
            Check("journalAvailableEndpoint", journalBody is SessionJournalView { Available: true });
        }
        finally
        {
            sources.Dispose();
        }

        // Isolation: possession of a delegation ID is not authority, and
        // Hongxian participant strings are never consulted. A volatile
        // catalog (no durable state) and a foreign subject both fail closed.
        var volatileCatalog = new MarangDelegationCatalog();
        var malloryCatalog = new MarangDelegationCatalog(
            new SqliteAcceptedDelegationStore(authorityDb),
            new SqliteDelegationVisibilityStore(authorityDb));
        var isolationSources = RunStoreComposition.Create(Get("zhinuDb"), hongxianRoot, dir);
        try
        {
            Check("volatileHidden", volatileCatalog.Find(delegationId, Caller) is null);
            var (volatileRun, _) = await RunProjectionEndpoints.GetRunAsync(
                volatileCatalog, Caller, runId, isolationSources, static () => DateTimeOffset.UtcNow, ct);
            Check("volatileDenied", volatileRun == 404, volatileRun.ToString());
            var (malloryRun, _) = await RunProjectionEndpoints.GetRunAsync(
                malloryCatalog, "mallory", runId, isolationSources, static () => DateTimeOffset.UtcNow, ct);
            Check("malloryDenied", malloryRun == 404, malloryRun.ToString());
            var (malloryJournal, _) = await SessionJournalEndpoints.GetJournalAsync(
                malloryCatalog, "mallory", runId, isolationSources, isolationSources, ct);
            Check("malloryJournalDenied", malloryJournal == 404, malloryJournal.ToString());
            Check("malloryReattachDenied", !await malloryCatalog.ReattachAsync("mallory", delegationId, ct));
        }
        finally
        {
            isolationSources.Dispose();
        }

        // Conflicting retry after restart targets the same durable slot and
        // fails closed instead of minting a second delegation.
        var conflicted = false;
        try
        {
            await durableCatalog.AcceptAsync(
                new DelegationCallerScope(Caller),
                new DelegationRequest(
                    "restart-1", "A different objective", "test-provider",
                    new WorkspaceReference("local", "workspace", "revision"),
                    ["Done"], [],
                    new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
                ct);
        }
        catch (DelegationRequestKeyConflictException)
        {
            conflicted = true;
        }

        Check("conflictRejected", conflicted);

        // Revocation is terminal: revoke the secondary acceptance and prove
        // its visibility can no longer be reattached. The main binding stays
        // live for the driver's independent verification.
        var visibility = new SqliteDelegationVisibilityStore(authorityDb);
        var secondaryId = new DelegationId(Guid.Parse(Get("secondaryDelegationId")));
        var revoked = await visibility.RevokeAsync(
            MarangPrincipal.DefaultTenant, Caller, secondaryId.Value.ToString("D"),
            AcceptanceIdentity.InitialGeneration, "probe-revoke-1", "probe.revoked", ct);
        Check("revoked", revoked == DelegationVisibilityRevokeStatus.Revoked);
        var revokedCatalog = new MarangDelegationCatalog(
            new SqliteAcceptedDelegationStore(authorityDb),
            new SqliteDelegationVisibilityStore(authorityDb));
        Check("revokeBlocked", !await revokedCatalog.ReattachAsync(Caller, secondaryId, ct));

        // Expiry blocks reattachment without mutating anything: the main
        // binding (30-day validity) is expired under a future clock.
        var expiredCatalog = new MarangDelegationCatalog(
            new SqliteAcceptedDelegationStore(authorityDb),
            new SqliteDelegationVisibilityStore(authorityDb),
            () => DateTimeOffset.UtcNow.AddDays(31));
        Check("expiredBlocked", !await expiredCatalog.ReattachAsync(Caller, delegationId, ct));

        // Resume through the engine: deliver the signal and run to terminal.
        var workflow = new FetchThenWaitWorkflow(logPath);
        var engine = new WorkflowEngine(
            store,
            new WorkflowRegistry().Register(WorkflowName, WorkflowVersion, workflow),
            new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
        await engine.SendSignalAsync(runId, "approval_request", "approved", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var output = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        var completed = await engine.GetRunAsync(runId, ct);
        Check("terminal", completed!.Status == WorkflowStatus.Completed, completed.Status.ToString());
        Check("output", output.Contains("approved", StringComparison.Ordinal), output);

        // No duplicate provider work: the file-logged invocation ran exactly once.
        var invocations = await File.ReadAllLinesAsync(logPath, ct);
        Check("invocationCount", invocations.Length == 1 && oldInvocations == 1, $"lines={invocations.Length}");

        var eventsAfter = await engine.GetEventsAsync(runId, 0, 1000, ct);
        var maxAfter = eventsAfter.Count == 0 ? 0 : eventsAfter.Max(e => e.Sequence);
        Check("sequenceMonotonicAfter", maxAfter >= maxSequence);
        var artifacts = await store.GetArtifactsAsync(runId, ct);
        Check("zeroArtifacts", artifacts.Count == 0);

        // Post-terminal operator reads without any live Qingniao runtime:
        // detail and evidence reconstruct from durable sources for the
        // authorized caller and stay closed for everyone else.
        var bareRuntime = new DelegationRuntime(
            new MarangDelegationCatalog(), null,
            new InMemoryProviderRegistry(), new InMemoryExternalOperationProviderCatalog());
        var historySources = RunStoreComposition.Create(Get("zhinuDb"), hongxianRoot, dir);
        try
        {
            var (detailStatus, detailBody) = await SupervisionHttpEndpoints.GetDetailAsync(
                bareRuntime, durableCatalog, Caller, delegationId.Value, ct, historySources);
            Check("detailStatus", detailStatus == 200, detailStatus.ToString());
            var detail = JsonSerializer.SerializeToDocument(detailBody, new JsonSerializerOptions(JsonSerializerDefaults.Web)).RootElement;
            Check("detailHistory", detail.GetProperty("runtimeAvailable").GetBoolean() == false);
            Check("detailRunStatus", detail.GetProperty("runStatus").GetString() == "Completed");
            Check("detailResult", (detail.GetProperty("resultSummary").GetString() ?? "").Contains("approved", StringComparison.Ordinal));
            Check("detailEnvelope", detail.GetProperty("objective").GetString() == "Run one bounded restartable task"
                && detail.GetProperty("sessionId").GetString() == sessionId.Value.ToString("D"));
            Check("detailNoSynthesis", detail.GetProperty("state").ValueKind == JsonValueKind.Null
                && detail.GetProperty("canCancel").GetBoolean() == false);

            var (evidenceStatus, evidenceBody) = await SupervisionHttpEndpoints.GetEvidenceAsync(
                bareRuntime, durableCatalog, Caller, delegationId.Value, ct, historySources);
            Check("evidenceStatus", evidenceStatus == 200, evidenceStatus.ToString());
            var evidence = JsonSerializer.SerializeToDocument(evidenceBody, new JsonSerializerOptions(JsonSerializerDefaults.Web)).RootElement;
            Check("evidenceAbsent", evidence.GetProperty("hasResult").GetBoolean() == false);
            Check("evidenceOutput", (evidence.GetProperty("terminalOutput").GetString() ?? "").Contains("approved", StringComparison.Ordinal));
            Check("evidenceRunStatus", evidence.GetProperty("runStatus").GetString() == "Completed");

            var (malloryDetail, _) = await SupervisionHttpEndpoints.GetDetailAsync(
                bareRuntime, durableCatalog, "mallory", delegationId.Value, ct, historySources);
            Check("malloryDetailDenied", malloryDetail == 404, malloryDetail.ToString());
            var (malloryEvidence, _) = await SupervisionHttpEndpoints.GetEvidenceAsync(
                bareRuntime, durableCatalog, "mallory", delegationId.Value, ct, historySources);
            Check("malloryEvidenceDenied", malloryEvidence == 404, malloryEvidence.ToString());
            var (revokedDetail, _) = await SupervisionHttpEndpoints.GetDetailAsync(
                bareRuntime, revokedCatalog, Caller, secondaryId.Value, ct, historySources);
            Check("revokedDetailDenied", revokedDetail == 404, revokedDetail.ToString());
            var (expiredDetail, _) = await SupervisionHttpEndpoints.GetDetailAsync(
                bareRuntime, expiredCatalog, Caller, delegationId.Value, ct, historySources);
            Check("expiredDetailDenied", expiredDetail == 404, expiredDetail.ToString());
        }
        finally
        {
            historySources.Dispose();
        }

        results["output"] = output;
        results["runId"] = runId.ToString("D");
        results["sessionId"] = sessionId.Value.ToString("D");
        results["delegationId"] = delegationId.Value.ToString("D");
        results["executionFingerprint"] = fingerprint;
        await File.WriteAllTextAsync(Path.Combine(dir, "phase2.json"), JsonSerializer.Serialize(results), ct);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static WorkflowPlan BuildPlan()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var fetchPath = StructuralNodeIdentity.Create("demo", "fetch");
        var approvalPath = StructuralNodeIdentity.Create("demo", "approval");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion, "fuwen-language/v1", FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion, FuwenContracts.ExecutionFingerprintVersion,
            "demo", "1", str, str, "routing/1", [], [activityDesc], new CapabilityManifest([]),
            [
                new ActivityNode("fetch", fetchPath, activityDesc, [new ArgumentBinding("value", new InputBinding([]))], str),
                new WaitNode("approval", approvalPath, "approval_request", str),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(approvalPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([fetchPath]),
                    new WorkflowExecutionPhase([approvalPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
    }

    private sealed class FetchThenWaitWorkflow(string logPath) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            var fetched = await context.StepAsync(
                "demo/fetch",
                input,
                async (_, token) =>
                {
                    await File.AppendAllTextAsync(logPath, "fetch\n", token);
                    return input + "-fetched";
                },
                cancellationToken: cancellationToken);
            var approved = await context.WaitForSignalAsync<string>(
                "demo/approval", "approval_request", timeout: null, cancellationToken: cancellationToken);
            return fetched + "|" + approved;
        }
    }
}
