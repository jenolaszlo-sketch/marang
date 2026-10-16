#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Marang.Tests;

[Collection("RepositoryRecoveryIntegrity")]
public sealed class RepositoryRecoveryInspectorProcessTests
{
    [Theory]
    [InlineData("healthy-pending", null, true)]
    [InlineData("healthy-initial", null, true)]
    [InlineData("healthy-committed", null, true)]
    [InlineData("intent-conflict", null, false)]
    [InlineData("revision-json", "UPDATE adaptive_revisions SET document='{' WHERE revision_id='r1'", false)]
    [InlineData("owner-missing", "DELETE FROM adaptive_run_ownership WHERE run_id=(SELECT run_id FROM adaptive_run_ownership LIMIT 1)", false)]
    [InlineData("event-digest", "UPDATE repository_effect_events SET event_digest='bad'", false)]
    [InlineData("graph-digest", "UPDATE repository_effect_graphs SET graph_digest='bad'", false)]
    [InlineData("table-missing", "DROP TABLE adaptive_provider_handles", false)]
    public async Task Fresh_process_observes_live_wal_and_preserves_database_evidence(string scenario, string? damageSql, bool ready)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync(prepareProposal: scenario != "healthy-initial");
        if (scenario == "healthy-committed") await fixture.CommitAsync(ct);
        if (scenario == "intent-conflict") await fixture.RebindJournalIntentAsync(ct);
        // Keep a connection open so committed state remains in WAL, rather than
        // testing only a checkpointed main file. Inspection must read that WAL.
        await using var keeper = await fixture.Database.OpenAsync(ct);
        await using (var command = keeper.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_autocheckpoint=0; CREATE TABLE integrity_process_control(value TEXT)";
            await command.ExecuteNonQueryAsync(ct);
        }
        if (damageSql is not null) await fixture.MutateAsync(damageSql, ct);
        var databaseBefore = await ReadEvidenceAsync(fixture.DatabasePath, ct);
        var walBefore = await ReadEvidenceAsync(fixture.DatabasePath + "-wal", ct);
        walBefore.Should().NotBeEmpty();
        string source = FindRoot();
        string evidence = Path.Combine(source, "artifacts", "aaaw-m6-1", "inspection-process-probes", scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = source, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            Path.Combine(source, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll"),
            "--phase", "integrity-inspect", "--dir", evidence,
            "--database", fixture.DatabasePath, "--workflow-id", fixture.WorkflowId.ToString("D")
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "stdout.log"), await stdout, ct);
        await File.WriteAllTextAsync(Path.Combine(evidence, "stderr.log"), await stderr, ct);
        process.ExitCode.Should().Be(ready ? 0 : 1);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "integrity-result.json"), ct));
        result.RootElement.GetProperty("canRecover").GetBoolean().Should().Be(ready);
        result.RootElement.GetProperty("status").GetString().Should().Be(ready ? "Ready"
            : scenario == "table-missing" ? "MissingStateShape" : "InconsistentState");
        (await ReadEvidenceAsync(fixture.DatabasePath, ct)).Should().Equal(databaseBefore);
        (await ReadEvidenceAsync(fixture.DatabasePath + "-wal", ct)).Should().Equal(walBefore);
        File.Exists(Path.Combine(evidence, "workflow-process-id.txt")).Should().BeFalse();
        await File.WriteAllTextAsync(Path.Combine(evidence, "preservation.json"), JsonSerializer.Serialize(new
        {
            scenario, ready, processId = process.Id, exitCode = process.ExitCode,
            databaseDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(databaseBefore)),
            walDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(walBefore)),
            databaseUnchanged = true, walUnchanged = true, walDisposition = "existing-byte-identical",
            sharedMemoryPresent = File.Exists(fixture.DatabasePath + "-shm")
        }), ct);
    }

    private static async Task<byte[]> ReadEvidenceAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes, ct);
        return bytes.ToArray();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Marang.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Marang source root not found.");
    }
}
#endif
