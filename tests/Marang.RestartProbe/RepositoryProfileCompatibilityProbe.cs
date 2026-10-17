using System.Text.Json;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Marang.Runs.Integrity;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.RestartProbe;

/// <summary>Process boundary for the single qualified legacy-to-current repository profile transition.</summary>
internal static class RepositoryProfileCompatibilityProbe
{
    private static readonly Guid WorkflowId = Guid.Parse("dd7e19af-e327-4886-b286-ec96c26835a7");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task UpgradeAsync(string directory, string? checkpoint)
    {
        directory = Path.GetFullPath(directory);
        var database = Path.Combine(directory, "runtime", "execution.db");
        var hufu = Path.Combine(directory, "runtime", "hufu.db");
        Func<RepositoryProfileUpgradeCheckpoint, CancellationToken, ValueTask>? observer = null;
        if (checkpoint is not null)
        {
            if (!Enum.TryParse<RepositoryProfileUpgradeCheckpoint>(checkpoint, ignoreCase: false, out var requested))
                throw new ArgumentException("Unknown profile-upgrade checkpoint.", nameof(checkpoint));
            observer = async (observed, cancellationToken) =>
            {
                if (observed != requested) return;
                var evidence = new
                {
                    Checkpoint = observed.ToString(),
                    ProcessId = Environment.ProcessId,
                    SourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA")
                };
                var readyPath = Path.Combine(directory, "profile-upgrade-checkpoint-" + checkpoint + ".json");
                var temporaryPath = readyPath + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(evidence, JsonOptions), cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, readyPath, overwrite: true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            };
        }

        var result = await RepositoryProfileCompatibility.UpgradeForQualificationAsync(database, WorkflowId,
            [hufu], observer).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "profile-upgrade-result.json"),
            JsonSerializer.Serialize(new
            {
                Result = result.ToString(),
                SourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                ProcessId = Environment.ProcessId,
                DatabasePath = database
            }, JsonOptions)).ConfigureAwait(false);
    }

    public static async Task ResumeCurrentProfileAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        var database = Path.Combine(directory, "runtime", "execution.db");
        var hufu = Path.Combine(directory, "runtime", "hufu.db");
        await RepositoryProfileCompatibility.RequireCurrentAsync(database, WorkflowId, [hufu]).ConfigureAwait(false);
        await MultiEffectProbe.RunAsync(directory, barrier: null, resume: true).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "profile-current-resume.json"),
            JsonSerializer.Serialize(new
            {
                SourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                ProcessId = Environment.ProcessId,
                DatabasePath = database,
                CurrentProfileRequired = true
            }, JsonOptions)).ConfigureAwait(false);
    }

    public static async Task ResumeCurrentProfilePartialCatchUpAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        var database = Path.Combine(directory, "runtime", "execution.db");
        var hufu = Path.Combine(directory, "runtime", "hufu.db");
        await RepositoryProfileCompatibility.RequireCurrentAsync(database, WorkflowId, [hufu]).ConfigureAwait(false);
        var checkout = (await File.ReadAllTextAsync(Path.Combine(directory, "checkout-path.txt")).ConfigureAwait(false)).Trim();
        var sqlite = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = database, Pooling = false, BusyTimeout = TimeSpan.FromSeconds(10) });
        var revisions = new SqliteAdaptiveRevisionRepository(sqlite);
        var effects = new SqliteRepositoryEffectJournal(sqlite);
        var graphJournal = new SqliteRepositoryEffectGraphJournal(sqlite, effects);
        var supervisorCatchUp = new SupervisorCatchUp(sqlite, name => name == "qualified-operator");
        var repository = ControlledRepository.Open(checkout,
            ["tests/Marang.Tests/Marang.Tests.csproj", "src/Marang/WorkflowPlanReference.cs", "README.md", "tests/Marang.Tests/WorkflowPlanResolutionTests.cs"]);
        var view = await new RepositoryEffectGraphSupervisor(revisions, graphJournal, effects, repository, supervisorCatchUp)
            .ReadAsync(WorkflowId, "qualified-operator").ConfigureAwait(false);
        var b = await effects.GetAsync(Guid.Parse("bb288558-11d7-4a4d-91e2-a4332cecedc8")).ConfigureAwait(false)
            ?? throw new InvalidDataException("Persisted B effect is missing during profile-1 catch-up.");
        var c = await effects.GetAsync(Guid.Parse("912081ef-5f01-4d72-890a-75a381f6abdd")).ConfigureAwait(false)
            ?? throw new InvalidDataException("Persisted C effect is missing during profile-1 catch-up.");
        var cView = view.Effects.Single(effect => effect.EffectId == c.Intent.EffectId);
        await File.WriteAllTextAsync(Path.Combine(directory, "profile-current-partial-catchup.json"),
            JsonSerializer.Serialize(new
            {
                SourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                ProcessId = Environment.ProcessId,
                DatabasePath = database,
                CurrentProfileRequired = true,
                MutationBlocked = view.MutationBlocked,
                BClassification = b.Classification?.ToString(),
                BStage = b.Stage.ToString(),
                CStage = c.Stage.ToString(),
                CBlockingPrerequisites = cView.BlockingPrerequisites,
                ObservedStateDigest = view.ActualCheckoutState.StateDigest,
                DeclaredNoExecution = true
            }, JsonOptions)).ConfigureAwait(false);
    }
}
