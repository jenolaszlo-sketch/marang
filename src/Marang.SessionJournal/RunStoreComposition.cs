using System.Text.Json;
using Penghou.Fuwen;
using Penghou.Hongxian.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Marang.SessionJournal;

/// <summary>
/// Composes durable run-projection stores from host configuration. Plans load
/// from JSON files named by revision under a plan directory; Zhinu and
/// Hongxian read from their Sqlite stores. Invalid configuration fails fast;
/// an absent configuration section means run projection stays unavailable
/// (404), never silently substituted.
/// </summary>
public static class RunStoreComposition
{
    /// <summary>Builds a store-backed source. Throws on invalid configuration.</summary>
    public static ConfiguredRunProjectionSource Create(
        string zhinuDatabasePath,
        string hongxianRootPath,
        string planDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zhinuDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hongxianRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(planDirectory);
        if (!Directory.Exists(planDirectory))
        {
            throw new DirectoryNotFoundException($"The run plan directory does not exist: '{planDirectory}'.");
        }

        var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuDatabasePath });
        var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions { RootPath = hongxianRootPath });
        return new ConfiguredRunProjectionSource(
            zhinu, zhinu,
            (revision, ct) => LoadPlanAsync(planDirectory, revision, ct),
            hongxian);
    }

    /// <summary>Loads one compiled plan by revision, or null when absent or malformed.</summary>
    public static async ValueTask<WorkflowPlan?> LoadPlanAsync(
        string planDirectory,
        string planRevision,
        CancellationToken cancellationToken = default)
    {
        var fileName = string.Concat(planRevision.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var path = Path.Combine(planDirectory, fileName + ".json");
        string json;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkflowPlan>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
