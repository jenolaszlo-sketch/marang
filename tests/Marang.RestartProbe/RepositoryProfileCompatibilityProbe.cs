using System.Text.Json;
using Marang.Runs.Integrity;

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
                await File.WriteAllTextAsync(Path.Combine(directory, "profile-upgrade-checkpoint-" + checkpoint + ".json"),
                    JsonSerializer.Serialize(evidence, JsonOptions), cancellationToken).ConfigureAwait(false);
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
}
