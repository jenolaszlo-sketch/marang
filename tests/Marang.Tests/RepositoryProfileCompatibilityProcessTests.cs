#if NET10_0_OR_GREATER
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marang.Tests.LifecycleBroker;
using Microsoft.Data.Sqlite;

namespace Marang.Tests;

[Collection("RepositoryRecoveryIntegrity")]
public sealed class RepositoryProfileCompatibilityProcessTests
{
    private const string OldSourceSha = "5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29";
    private const string WorkflowId = "dd7e19af-e327-4886-b286-ec96c26835a7";
    private const string ProfileIdentity = "marang/bounded-repository/m6.1@5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29;zhinu-schema=6;effect-json=web-v1;profile=1;effect-record=1";
    private static readonly string ProfileDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProfileIdentity))).ToLowerInvariant();
    [Theory]
    [InlineData("K3", "ValidationPassed")]
    [InlineData("K4", "Applying")]
    public async Task Exact_old_writer_state_upgrades_without_rewriting_payloads_and_resumes(string barrier, string expectedStage)
    {
        var (root, oldRoot, oldProbe, newProbe, directory) = CreateRun(barrier);
        await using (var old = OwnedProbeRun.Start(oldProbe, oldRoot, directory, barrier, resume: false, phase: "multi-effect"))
        {
            await StopAtBarrierAsync(old, directory, barrier);
        }

        var database = Path.Combine(directory, "runtime", "execution.db");
        await CaptureLegacyFixtureAsync(directory, TestContext.Current.CancellationToken);
        File.Exists(database).Should().BeTrue("the fixture must be emitted by the immutable M6.1 process");
        var before = await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken);
        before.Rows.Should().Contain(expectedStage, "the selected old-writer barrier must be present");
        var oldStages = await ReadEffectStagesAsync(database, TestContext.Current.CancellationToken);
        if (barrier == "K3") oldStages[Guid.Parse("bf668e4e-721b-4611-aae6-e2712dc5a881")].Should().Be("ValidationPassed");
        if (barrier == "K4")
        {
            oldStages[Guid.Parse("bb288558-11d7-4a4d-91e2-a4332cecedc8")].Should().Be("Applying");
            oldStages[Guid.Parse("912081ef-5f01-4d72-890a-75a381f6abdd")].Should().Be("Prepared");
        }
        await RunNaturalAsync(newProbe, root, directory, "profile-upgrade", barrier: null);
        var after = await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "legacy-cells-before.json"), JsonSerializer.Serialize(before), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "legacy-cells-after.json"), JsonSerializer.Serialize(after), TestContext.Current.CancellationToken);
        after.Should().Be(before, "the transition is additive and must preserve every existing typed value and schema except the new profile tag");
        await AssertProfileAsync(database, TestContext.Current.CancellationToken);
        using (var firstUpgrade = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "profile-upgrade-result.json"), TestContext.Current.CancellationToken)))
            firstUpgrade.RootElement.GetProperty("result").GetString().Should().Be("Upgraded");

        // Idempotence is exercised in a separate process against the same store.
        await RunNaturalAsync(newProbe, root, directory, "profile-upgrade", barrier: null);
        using (var upgraded = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "profile-upgrade-result.json"), TestContext.Current.CancellationToken)))
            upgraded.RootElement.GetProperty("result").GetString().Should().Be("AlreadyCurrent");

        await RunNaturalAsync(newProbe, root, directory, "multi-effect-v1", barrier: null, timeout: TimeSpan.FromMinutes(30));
        File.Exists(Path.Combine(directory, "profile-current-resume.json")).Should().BeTrue();
        var firstApplyLog = await File.ReadAllTextAsync(Path.Combine(directory, "physical-applies.log"), TestContext.Current.CancellationToken);
        var firstLineage = await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken);
        await RunNaturalAsync(newProbe, root, directory, "multi-effect-v1", barrier: null, timeout: TimeSpan.FromMinutes(30));
        (await File.ReadAllTextAsync(Path.Combine(directory, "physical-applies.log"), TestContext.Current.CancellationToken)).Should().Be(firstApplyLog);
        (await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken)).Should().Be(firstLineage,
            "repeated current-profile admission and resume must not append or rewrite existing effects");
        var report = new
        {
            Schema = "aaaw-m6-2-profile-process-case/v1", OldSourceSha, NewSourceSha = Git(root, "rev-parse", "HEAD"),
            OldProbeSha256 = Hash(File.ReadAllBytes(oldProbe)), NewProbeSha256 = Hash(File.ReadAllBytes(newProbe)),
            Barrier = barrier, ExpectedStage = expectedStage, LegacyPayloadSnapshotSha256 = Hash(before.Rows + before.Schema),
            ProfileDigest, Database = database, ProcessEvidenceDirectory = directory
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "profile-compatibility-case.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("AfterBegin", false)]
    [InlineData("AfterDdl", false)]
    [InlineData("BeforeCommit", false)]
    [InlineData("AfterCommit", true)]
    public async Task Stopped_migration_checkpoint_is_atomic_and_retryable(string checkpoint, bool committed)
    {
        var (root, oldRoot, oldProbe, newProbe, directory) = CreateRun("K1-" + checkpoint);
        await using (var old = OwnedProbeRun.Start(oldProbe, oldRoot, directory, "K1", resume: false, phase: "multi-effect"))
            await StopAtBarrierAsync(old, directory, "K1");

        var database = Path.Combine(directory, "runtime", "execution.db");
        await CaptureLegacyFixtureAsync(directory, TestContext.Current.CancellationToken);
        var before = await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken);
        await using (var migration = OwnedProbeRun.Start(newProbe, root, directory, checkpoint, resume: false, phase: "profile-upgrade"))
        {
            var ready = Path.Combine(directory, "profile-upgrade-checkpoint-" + checkpoint + ".json");
            await WaitForFileAsync(ready, migration, directory, TimeSpan.FromMinutes(2));
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(ready, TestContext.Current.CancellationToken));
            marker.RootElement.GetProperty("checkpoint").GetString().Should().Be(checkpoint);
            var pid = marker.RootElement.GetProperty("processId").GetInt32();
            await migration.StopAtBarrierAsync(directory, "profile-" + checkpoint, pid, requireActive: false,
                TestContext.Current.CancellationToken);
        }

        if (committed)
        {
            await AssertProfileAsync(database, TestContext.Current.CancellationToken);
            (await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken)).Should().Be(before);
        }
        else
        {
            await AssertLegacyProfileAsync(database, TestContext.Current.CancellationToken);
            (await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken)).Should().Be(before);
        }

        await RunNaturalAsync(newProbe, root, directory, "profile-upgrade", barrier: null);
        await AssertProfileAsync(database, TestContext.Current.CancellationToken);
        (await ReadLegacyPayloadSnapshotAsync(database, TestContext.Current.CancellationToken)).Should().Be(before);
        using (var retry = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "profile-upgrade-result.json"), TestContext.Current.CancellationToken)))
            retry.RootElement.GetProperty("result").GetString().Should().Be("Upgraded");
        await File.WriteAllTextAsync(Path.Combine(directory, "profile-checkpoint-case.json"), JsonSerializer.Serialize(new
        {
            Schema = "aaaw-m6-2-profile-checkpoint-case/v1", OldSourceSha,
            NewSourceSha = Git(root, "rev-parse", "HEAD"), OldProbeSha256 = Hash(File.ReadAllBytes(oldProbe)),
            NewProbeSha256 = Hash(File.ReadAllBytes(newProbe)), Checkpoint = checkpoint, Committed = committed,
            LegacyPayloadSnapshotSha256 = Hash(before.Rows + before.Schema), StopReceipt = Path.Combine(directory, "lifecycle-stop-profile-" + checkpoint + ".json"),
            Database = database, Directory = directory
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("partial-column")]
    [InlineData("unknown-future")]
    public async Task Unsupported_profile_refuses_repeatedly_without_touching_database_or_checkout(string damage)
    {
        var (root, oldRoot, oldProbe, newProbe, directory) = CreateRun("refusal-" + damage);
        await using (var old = OwnedProbeRun.Start(oldProbe, oldRoot, directory, "K1", resume: false, phase: "multi-effect"))
            await StopAtBarrierAsync(old, directory, "K1");
        var database = Path.Combine(directory, "runtime", "execution.db");
        await CaptureLegacyFixtureAsync(directory, TestContext.Current.CancellationToken);
        if (damage == "partial-column")
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE repository_effects ADD COLUMN record_format INTEGER NOT NULL DEFAULT 1 CHECK(record_format=1)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            await RunNaturalAsync(newProbe, root, directory, "profile-upgrade", barrier: null);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                BEGIN IMMEDIATE;
                ALTER TABLE marang_repository_profile RENAME TO marang_repository_profile_old;
                CREATE TABLE marang_repository_profile (
                  singleton INTEGER PRIMARY KEY CHECK(singleton=1), owner TEXT NOT NULL,
                  format_version INTEGER NOT NULL CHECK(format_version=99),
                  contract_identity TEXT NOT NULL, contract_digest TEXT NOT NULL);
                INSERT INTO marang_repository_profile(singleton,owner,format_version,contract_identity,contract_digest)
                  SELECT singleton,owner,99,contract_identity,contract_digest FROM marang_repository_profile_old;
                DROP TABLE marang_repository_profile_old;
                COMMIT;
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            (await integrity.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string).Should().Be("ok");
        }

        var checkout = (await File.ReadAllTextAsync(Path.Combine(directory, "checkout-path.txt"), TestContext.Current.CancellationToken)).Trim();
        var before = await PersistentStateFingerprintAsync(database, Path.Combine(directory, "runtime", "hufu.db"), checkout,
            TestContext.Current.CancellationToken);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await RunRefusalAsync(newProbe, root, directory, "profile-upgrade");
            var after = await PersistentStateFingerprintAsync(database, Path.Combine(directory, "runtime", "hufu.db"), checkout,
                TestContext.Current.CancellationToken);
            after.Digest.Should().Be(before.Digest, "a refusal must not mutate the SQLite stores or tracked checkout bytes");
            after.GitHead.Should().Be(before.GitHead);
            after.GitStatus.Should().Be(before.GitStatus);
            after.ExistingWalHashes.Should().Be(before.ExistingWalHashes,
                "an absent WAL and a zero-byte WAL are equivalent coordination state while existing WAL bytes must remain exact");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "profile-refusal-case.json"), JsonSerializer.Serialize(new
        {
            Schema = "aaaw-m6-2-profile-refusal-case/v1", OldSourceSha, NewSourceSha = Git(root, "rev-parse", "HEAD"),
            OldProbeSha256 = Hash(File.ReadAllBytes(oldProbe)), NewProbeSha256 = Hash(File.ReadAllBytes(newProbe)),
            Damage = damage, Attempts = 2, PersistentStateFingerprint = before.Digest,
            GitHead = before.GitHead, GitStatus = before.GitStatus, WalDisposition = before.WalDisposition,
            ExistingWalHashes = before.ExistingWalHashes, Directory = directory
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), TestContext.Current.CancellationToken);
    }

    private static (string Root, string OldRoot, string OldProbe, string NewProbe, string Directory) CreateRun(string label)
    {
        var root = FindRoot();
        var oldRoot = Environment.GetEnvironmentVariable("MARANG_M6_2_OLD_ROOT")
            ?? Path.Combine(root, "artifacts", "aaaw-m6-2", "oldwriter-source");
        var oldProbe = Environment.GetEnvironmentVariable("MARANG_M6_2_OLD_PROBE")
            ?? Path.Combine(oldRoot, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        var buildManifest = Path.Combine(root, "artifacts", "aaaw-m6-2", "oldwriter-build.json");
        if (!File.Exists(buildManifest)) throw new FileNotFoundException("The exact-source old writer must be built before these process cases.", buildManifest);
        using (var build = JsonDocument.Parse(File.ReadAllText(buildManifest)))
        {
            var json = build.RootElement;
            json.GetProperty("passed").GetBoolean().Should().BeTrue();
            json.GetProperty("oldSourceSha").GetString().Should().Be(OldSourceSha);
            json.GetProperty("oldSourceEnd").GetProperty("clean").GetBoolean().Should().BeTrue();
            json.GetProperty("oldProbe").GetProperty("sha256").GetString().Should().Be(Hash(File.ReadAllBytes(oldProbe)));
        }
        Git(oldRoot, "rev-parse", "HEAD").Should().Be(OldSourceSha);
        Git(oldRoot, "status", "--porcelain=v1", "--untracked-files=all").Should().BeEmpty();

        var newProbe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        if (!File.Exists(newProbe)) throw new FileNotFoundException("The current restart probe has not been built.", newProbe);
        var directory = Path.Combine(root, "artifacts", "aaaw-m6-2", "process-probes",
            label + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return (root, oldRoot, oldProbe, newProbe, directory);
    }

    private static async Task StopAtBarrierAsync(OwnedProbeRun run, string directory, string barrier)
    {
        var ready = Path.Combine(directory, "barrier-ready-" + barrier);
        await WaitForFileAsync(ready, run, directory, TimeSpan.FromMinutes(10));
        var pidPath = Path.Combine(directory, "workflow-process-id.txt");
        var pid = int.Parse((await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await run.StopAtBarrierAsync(directory, barrier, pid, requireActive: false, TestContext.Current.CancellationToken);
    }

    private static async Task WaitForFileAsync(string path, OwnedProbeRun run, string directory, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (run.RootTask.IsCompleted)
            {
                var receipt = await run.RootTask;
                await run.StopAfterFailureAsync(directory, "before-" + Path.GetFileName(path));
                throw new InvalidOperationException("Process exited before its qualification marker: " + OwnedProbeRun.Describe(receipt));
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                await run.StopAfterFailureAsync(directory, "timeout-" + Path.GetFileName(path));
                throw new TimeoutException("Process did not publish its marker within " + timeout + ".");
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static async Task RunNaturalAsync(string probe, string root, string directory, string phase, string? barrier,
        TimeSpan? timeout = null)
    {
        await using var run = OwnedProbeRun.Start(probe, root, directory, barrier, resume: true, phase: phase);
        var receipt = await run.WaitForNaturalExitAsync(timeout ?? TimeSpan.FromMinutes(5), directory, TestContext.Current.CancellationToken);
        await WriteReceiptAsync(directory, "natural-" + phase, receipt);
        receipt.Cause.Should().Be(Gagamba.Runtime.RunCause.NaturalExit);
        receipt.RootExitCode.Should().Be(0);
        receipt.Cleanup.Should().Be(Gagamba.Runtime.CleanupStatus.Confirmed);
        receipt.Output?.Status.Should().Be(Gagamba.Execution.OutputCaptureStatus.Complete);
    }

    private static async Task RunRefusalAsync(string probe, string root, string directory, string phase)
    {
        await using var run = OwnedProbeRun.Start(probe, root, directory, barrier: null, resume: true, phase: phase);
        var receipt = await run.RootTask.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        receipt.Cause.Should().Be(Gagamba.Runtime.RunCause.NonzeroExit);
        receipt.RootExitCode.Should().NotBe(0);
        receipt.Cleanup.Should().Be(Gagamba.Runtime.CleanupStatus.Confirmed);
        receipt.Output?.Status.Should().Be(Gagamba.Execution.OutputCaptureStatus.Complete);
        await WriteReceiptAsync(directory, "refusal-" + phase, receipt);
        File.Exists(Path.Combine(directory, "phase2-error.json")).Should().BeTrue();
    }

    private static Task WriteReceiptAsync(string directory, string label, Gagamba.Runtime.RunReceipt receipt) =>
        File.WriteAllTextAsync(Path.Combine(directory, label + "-" + Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(new
            {
                Cause = receipt.Cause.ToString(), receipt.RootExitCode, Cleanup = receipt.Cleanup.ToString(),
                receipt.StopRequestAccepted, CaptureStatus = receipt.Output?.Status.ToString(),
                StdoutBytesSeen = receipt.Output?.StdoutBytesSeen ?? 0, StderrBytesSeen = receipt.Output?.StderrBytesSeen ?? 0,
                Stdout = receipt.Output is null ? "" : Encoding.UTF8.GetString(receipt.Output.StdoutRetained.Span),
                Stderr = receipt.Output is null ? "" : Encoding.UTF8.GetString(receipt.Output.StderrRetained.Span),
                receipt.Reasons
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    private sealed record PersistentFingerprint(string Digest, string GitHead, string GitStatus,
        string WalDisposition, string ExistingWalHashes);

    private static async Task<PersistentFingerprint> PersistentStateFingerprintAsync(string database, string hufu, string checkout, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[] { database, hufu })
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\0"));
            if (!File.Exists(path)) { hash.AppendData(Encoding.UTF8.GetBytes("missing\0")); continue; }
            hash.AppendData(Encoding.UTF8.GetBytes("present\0"));
            hash.AppendData(await ReadSharedFileAsync(path, ct));
        }
        var walPaths = new[] { database + "-wal", hufu + "-wal" };
        var walHashes = new List<string>();
        var walDisposition = new List<string>();
        foreach (var path in walPaths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\0"));
            if (!File.Exists(path)) { walDisposition.Add("absent"); continue; }
            var bytes = await ReadSharedFileAsync(path, ct);
            walDisposition.Add(bytes.Length == 0 ? "empty" : "nonempty");
            if (bytes.Length > 0) walHashes.Add(Path.GetFileName(path) + ":" + Hash(bytes));
            hash.AppendData(bytes);
        }
        var head = Git(checkout, "rev-parse", "HEAD");
        var status = Git(checkout, "status", "--porcelain=v1", "--untracked-files=all");
        hash.AppendData(Encoding.UTF8.GetBytes(head + "\0" + status + "\0"));
        var trackedFiles = Git(checkout, "ls-files", "--cached", "--others", "--exclude-standard", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal);
        foreach (var relative in trackedFiles)
        {
            var path = Path.Combine(checkout, relative.Replace('/', Path.DirectorySeparatorChar));
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            var linkTarget = new FileInfo(path).LinkTarget;
            if (linkTarget is not null) hash.AppendData(Encoding.UTF8.GetBytes("link\0" + linkTarget));
            else if (File.Exists(path)) hash.AppendData(await ReadSharedFileAsync(path, ct));
        }
        return new PersistentFingerprint(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), head, status,
            string.Join(',', walDisposition), string.Join(',', walHashes));
    }

    private static async Task<byte[]> ReadSharedFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, useAsync: true);
        if (stream.Length > int.MaxValue) throw new InvalidDataException("Qualification snapshot file exceeds the bounded in-memory limit.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task CaptureLegacyFixtureAsync(string directory, CancellationToken ct)
    {
        var output = Path.Combine(directory, "legacy-fixture");
        Directory.CreateDirectory(output);
        var files = new List<object>();
        foreach (var name in new[] { "execution.db", "execution.db-wal", "hufu.db", "hufu.db-wal" })
        {
            var source = Path.Combine(directory, "runtime", name);
            if (!File.Exists(source)) continue;
            var bytes = await ReadSharedFileAsync(source, ct);
            await File.WriteAllBytesAsync(Path.Combine(output, name), bytes, ct);
            files.Add(new { name, bytes = bytes.Length, sha256 = Hash(bytes) });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
        {
            oldSourceSha = OldSourceSha, files,
            scope = "Unmodified old-writer test evidence; no backup/restore feature or recovery operation."
        }), ct);
    }

    private sealed record ProfilePayloadSnapshot(string Schema, string Rows);

    private static async Task<ProfilePayloadSnapshot> ReadLegacyPayloadSnapshotAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        var schemaRows = new List<string>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type,name,COALESCE(sql,'') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' OR name='sqlite_sequence' ORDER BY type,name";
            await using var reader = await schema.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                if (type == "table" && name == "marang_repository_profile") continue;
                // ALTER TABLE necessarily changes the stored CREATE TABLE text. Its unchanged
                // legacy columns and all independent indexes/triggers are checked separately.
                var sql = type == "table" && name == "repository_effects" ? "<legacy-effect-table-plus-profile-column>" : reader.GetString(2);
                schemaRows.Add(JsonSerializer.Serialize(new[] { type, name, sql }));
            }
        }

        var tables = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name<>'marang_repository_profile' ORDER BY name";
            await using var reader = await list.ExecuteReaderAsync(ct);
            var names = new List<string>();
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
            await reader.DisposeAsync();
            foreach (var table in names)
            {
                var columns = new List<string>();
                var tableInfo = new List<string>();
                await using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA table_info(" + Quote(table) + ")";
                    await using var columnReader = await pragma.ExecuteReaderAsync(ct);
                    while (await columnReader.ReadAsync(ct))
                    {
                        var name = columnReader.GetString(1);
                        if (table == "repository_effects" && name == "record_format") continue;
                        columns.Add(name);
                        tableInfo.Add(JsonSerializer.Serialize(new object?[]
                        {
                            columnReader.GetInt32(0), name, columnReader.GetString(2), columnReader.GetInt32(3),
                            columnReader.IsDBNull(4) ? null : columnReader.GetValue(4), columnReader.GetInt32(5)
                        }));
                    }
                }
                var indexes = new List<string>();
                var indexHeaders = new List<(string Name, int Unique, string Origin, int Partial)>();
                await using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA index_list(" + Quote(table) + ")";
                    await using var indexReader = await pragma.ExecuteReaderAsync(ct);
                    while (await indexReader.ReadAsync(ct))
                        indexHeaders.Add((indexReader.GetString(1), indexReader.GetInt32(2), indexReader.GetString(3), indexReader.GetInt32(4)));
                }
                foreach (var index in indexHeaders)
                {
                    var info = new List<string>();
                    await using var details = connection.CreateCommand();
                    details.CommandText = "PRAGMA index_info(" + Quote(index.Name) + ")";
                    await using var detailReader = await details.ExecuteReaderAsync(ct);
                    while (await detailReader.ReadAsync(ct))
                        info.Add(JsonSerializer.Serialize(new object?[] { detailReader.GetInt32(0), detailReader.GetInt32(1), detailReader.IsDBNull(2) ? null : detailReader.GetString(2) }));
                    indexes.Add(JsonSerializer.Serialize(new object?[] { index.Name, index.Unique, index.Origin, index.Partial, info }));
                }
                schemaRows.Add(JsonSerializer.Serialize(new { Table = table, Columns = tableInfo, Indexes = indexes }));
                if (columns.Count == 0) { tables[table] = []; continue; }
                var expressions = columns.Select(name =>
                {
                    var q = Quote(name);
                    return "typeof(" + q + "),quote(" + q + "),hex(CAST(" + q + " AS BLOB))";
                });
                var rows = new List<string>();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT " + string.Join(",", expressions) + " FROM " + Quote(table) + " ORDER BY rowid";
                await using var rowReader = await command.ExecuteReaderAsync(ct);
                while (await rowReader.ReadAsync(ct))
                {
                    var cells = new string?[rowReader.FieldCount];
                    for (var index = 0; index < rowReader.FieldCount; index++)
                        cells[index] = rowReader.IsDBNull(index) ? null : rowReader.GetString(index);
                    rows.Add(JsonSerializer.Serialize(cells));
                }
                tables[table] = rows;
            }
        }
        return new ProfilePayloadSnapshot(JsonSerializer.Serialize(schemaRows), JsonSerializer.Serialize(tables));
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<Dictionary<Guid, string>> ReadEffectStagesAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT effect_id,stage FROM repository_effects WHERE workflow_id=$workflow ORDER BY effect_id";
        command.Parameters.AddWithValue("$workflow", WorkflowId);
        var result = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Guid.Parse(reader.GetString(0)), reader.GetString(1));
        return result;
    }

    private static async Task AssertProfileAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner,format_version,contract_identity,contract_digest FROM marang_repository_profile WHERE singleton=1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).Should().BeTrue();
        reader.GetString(0).Should().Be("marang.bounded-repository");
        reader.GetInt32(1).Should().Be(1);
        reader.GetString(2).Should().Be(ProfileIdentity);
        reader.GetString(3).Should().Be(ProfileDigest);
        await reader.DisposeAsync();
        await using var records = connection.CreateCommand();
        records.CommandText = "SELECT COUNT(*) FROM repository_effects WHERE record_format<>1";
        Convert.ToInt64(await records.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture).Should().Be(0);
    }

    private static async Task AssertLegacyProfileAsync(string database, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(database), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE (type='table' AND name='marang_repository_profile') OR (type='table' AND name='repository_effects')";
        Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
        await using var schema = connection.CreateCommand();
        schema.CommandText = "PRAGMA table_info(repository_effects)";
        await using var reader = await schema.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) reader.GetString(1).Should().NotBe("record_format");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Marang.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the Marang repository root.");
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(" ", args) + " failed: " + stderr);
        return stdout.Trim();
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
#endif
