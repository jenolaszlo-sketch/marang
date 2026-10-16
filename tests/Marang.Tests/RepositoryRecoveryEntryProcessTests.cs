#if NET10_0_OR_GREATER
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Gagamba.Execution;
using Gagamba.Runtime;
using Marang.Tests.LifecycleBroker;
using Marang.Runs.Integrity;

namespace Marang.Tests;

[Collection("RepositoryRecoveryIntegrity")]
public sealed class RepositoryRecoveryEntryProcessTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("not-sqlite")]
    [InlineData("truncated-header")]
    [InlineData("logical-revision-json")]
    [InlineData("logical-event-digest")]
    public async Task Known_recovery_refuses_before_initialization_or_downstream_work(string damage)
    {
        var ct = TestContext.Current.CancellationToken;
        string root = FindRoot();
        string directory = Path.Combine(root, "artifacts", "aaaw-m6-1", "process-probes", damage + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "runtime", "execution.db");
        byte[]? before = damage switch
        {
            "empty" => [],
            "not-sqlite" => Encoding.UTF8.GetBytes("damaged durable state; do not initialize"),
            "truncated-header" => Encoding.UTF8.GetBytes("SQLite format 3\0"),
            _ => null
        };
        if (before is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, before, ct);
        }
        if (damage.StartsWith("logical-", StringComparison.Ordinal))
        {
            using var fixture = await RecoveryIntegrityFixture.CreateAsync(Guid.Parse("dd7e19af-e327-4886-b286-ec96c26835a7"));
            (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).CanRecover.Should().BeTrue();
            await fixture.MutateAsync(damage == "logical-revision-json"
                ? "UPDATE adaptive_revisions SET document='{' WHERE revision_id='r1'"
                : "UPDATE repository_effect_events SET event_digest='bad'", ct);
            before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, before, ct);
        }
        string probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        for (int attempt = 1; attempt <= 2; attempt++)
        await using (var run = OwnedProbeRun.Start(probe, root, directory, null, resume: true,
            phase: "multi-effect-reconcile", reconcileRun: 1))
        {
            var receipt = await run.RootTask.WaitAsync(TimeSpan.FromSeconds(60), ct);
            receipt.Cause.Should().Be(RunCause.NonzeroExit);
            receipt.RootExitCode.Should().Be(1);
            receipt.Cleanup.Should().Be(CleanupStatus.Confirmed);
            receipt.Output!.Status.Should().Be(OutputCaptureStatus.Complete);
            await File.WriteAllTextAsync(Path.Combine(directory, $"refusal-receipt-{attempt}.json"), JsonSerializer.Serialize(new
            {
                attempt, receipt.CorrelationId, receipt.Platform, Cause = receipt.Cause.ToString(), receipt.RootExitCode,
                Cleanup = receipt.Cleanup.ToString(), CaptureStatus = receipt.Output.Status.ToString(),
                Stdout = Encoding.UTF8.GetString(receipt.Output.StdoutRetained.Span),
                Stderr = Encoding.UTF8.GetString(receipt.Output.StderrRetained.Span)
            }), ct);
            File.Exists(Path.Combine(directory, "workflow-process-id.txt")).Should().BeFalse();
            File.Exists(Path.Combine(directory, "checkout-path.txt")).Should().BeFalse();
            File.Exists(Path.Combine(directory, "runtime", "hufu.db")).Should().BeFalse();
            if (before is null) File.Exists(path).Should().BeFalse();
            else (await File.ReadAllBytesAsync(path, ct)).Should().Equal(before);
            AssertWalUnwritten(path, damage);
        }
        using var error = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "phase2-error.json"), ct));
        error.RootElement.GetProperty("error").GetString().Should().Be("Marang.Runs.Integrity.RepositoryRecoveryRefusedException");
        string expectedStatus = damage.StartsWith("logical-", StringComparison.Ordinal) ? "InconsistentState"
            : damage == "missing" ? "MissingStore" : "DamagedStore";
        error.RootElement.GetProperty("message").GetString().Should().Contain(expectedStatus);
        File.Exists(Path.Combine(directory, "workflow-process-id.txt")).Should().BeFalse("the recovery guard precedes workflow admission");
        File.Exists(Path.Combine(directory, "checkout-path.txt")).Should().BeFalse("no checkout or downstream Git command is admitted");
        File.Exists(Path.Combine(directory, "runtime", "hufu.db")).Should().BeFalse("no authority store may be initialized or published");
        if (before is null) File.Exists(path).Should().BeFalse("a missing durable store must stay missing");
        else (await File.ReadAllBytesAsync(path, ct)).Should().Equal(before, "the refused recovery preserves damaged evidence");
        AssertWalUnwritten(path, damage);
        await File.WriteAllTextAsync(Path.Combine(directory, "preservation.json"), JsonSerializer.Serialize(new
        {
            damage, expectedStatus, freshProcessAttempts = 2, databaseUnchanged = true, walContentPreserved = true,
            walWasAbsent = true, walLengthAfterInspection = File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : (long?)null,
            walDisposition = File.Exists(path + "-wal") ? "absent-to-empty-created" : "absent-to-absent",
            databaseWasMissing = before is null,
            databaseDigest = before is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)),
            workflowAdmissionAbsent = true, checkoutAdmissionAbsent = true, authorityInitializationAbsent = true
        }), ct);
    }

    private static void AssertWalUnwritten(string databasePath, string damage)
    {
        // Read-only SQLite may materialize an empty WAL for a WAL-mode main
        // file copied without sidecars. No committed content may be written.
        if (damage.StartsWith("logical-", StringComparison.Ordinal) && File.Exists(databasePath + "-wal"))
            new FileInfo(databasePath + "-wal").Length.Should().Be(0);
        else File.Exists(databasePath + "-wal").Should().BeFalse();
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Marang.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Marang source root not found.");
    }
}
#endif
