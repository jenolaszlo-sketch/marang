using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Gagamba.Execution;
using Gagamba.Runtime;

const int readyTimeoutSeconds = 12;
const int operationTimeoutSeconds = 12;
string outputPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Environment.CurrentDirectory, "macos-probe.json");
string scratch = Path.Combine(Path.GetTempPath(), $"aaaw-m5-macos-probe-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
string normalMarker = Path.Combine(scratch, "normal-ready");
string capturedMarker = Path.Combine(scratch, "captured-ready");

var result = new Dictionary<string, object?>
{
    ["schema"] = "aaaw-m5-macos-profile-probe-v1",
    ["startedAtUtc"] = DateTimeOffset.UtcNow,
    ["host"] = new { osDescription = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), machine = Environment.MachineName },
    ["source"] = await ReadPackageIdentityAsync(),
    ["canonicalSupported"] = false,
    ["canonicalQualification"] = "not performed by this test-only profile probe"
};

ExecutionRuntime? runtime = null;
PreparedExecution? capturedPreparation = null;
ExecutionHandle? normalHandle = null;
bool normalConfirmed = false;
bool captureUnexpectedStart = false;
int exitCode = 0;
try
{
    runtime = ExecutionRuntime.Create();
    var described = runtime.Describe();
    result["provider"] = new { runtime.HasProvider, runtime.RefusalReason, platform = described.Platform };
    result["capabilities"] = described.Grants.ToDictionary(
        pair => pair.Key.ToString(), pair => new { level = pair.Value.Level.ToString(), kind = pair.Value.Kind.ToString(), pair.Value.Evidence });

    var requirements = new ExecutionRequirements(new[]
    {
        ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Partial, reason: "LifecycleBrokerOwner M4 stop barrier"),
        ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Partial, reason: "LifecycleBrokerOwner M4 descendant ownership"),
        ExecutionRequirement.Require(ExecutionCapability.RecursiveMembership, CapabilityLevel.Partial, reason: "LifecycleBrokerOwner M4 nested workload ownership")
    });

    // Control A exercises ordinary native launch and lifecycle termination without output capture.
    var prepare = runtime.Prepare(requirements);
    result["lifecyclePositiveControl"] = new Dictionary<string, object?> { ["prepare"] = Describe(prepare) };
    if (prepare is not PrepareResult.Accepted accepted)
    {
        result["lifecyclePositiveControl"] = new { prepare = Describe(prepare), passed = false, reason = "required profile preparation was refused" };
        exitCode = 1;
    }
    else
    {
        string script = $"printf '%s' '{nonce}' > '{normalMarker}'; exec /bin/sleep 90";
        var process = ProcessStartSpec.Vector("/bin/sh", new[] { "-c", script }, scratch);
        LaunchResult launch = runtime.Launch(accepted.Prepared, process);
        var control = (Dictionary<string, object?>)result["lifecyclePositiveControl"]!;
        control["launch"] = Describe(launch);
        if (launch is LaunchResult.Started started)
        {
            normalHandle = started.Handle;
            bool markerObserved = await WaitForMarkerAsync(normalMarker, nonce, TimeSpan.FromSeconds(readyTimeoutSeconds));
            control["markerObserved"] = markerObserved;
            control["markerPath"] = normalMarker;
            if (!markerObserved) exitCode = 1;
            TerminateResult terminate = runtime.Terminate(started.Handle);
            control["terminate"] = Describe(terminate);
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(operationTimeoutSeconds));
            CompletionResult completion = await runtime.WaitForCompletionAsync(started.Handle, waitCts.Token);
            control["completion"] = Describe(completion);
            normalConfirmed = terminate is TerminateResult.Terminated && completion is CompletionResult.Terminated;
            control["confirmedCleanup"] = normalConfirmed;
            if (!normalConfirmed) exitCode = 1;
            normalHandle = null;
        }
        else
        {
            // Launch failure consumes and cleans its preparation according to the public contract.
            control["passed"] = false;
            exitCode = 1;
        }
    }

    // Control B proves capture refusal is pre-dispatch and the unlaunched domain is reclaimable.
    var capturedPrepare = runtime.Prepare(requirements);
    var captureControl = new Dictionary<string, object?> { ["prepare"] = Describe(capturedPrepare), ["markerPath"] = capturedMarker };
    result["captureRefusalControl"] = captureControl;
    if (capturedPrepare is PrepareResult.Accepted capturedAccepted)
    {
        capturedPreparation = capturedAccepted.Prepared;
        string script = $"printf '%s' '{nonce}' > '{capturedMarker}'; exec /bin/sleep 90";
        CaptureLaunchResult captureLaunch = runtime.LaunchCaptured(capturedAccepted.Prepared,
            ProcessStartSpec.Vector("/bin/sh", new[] { "-c", script }, scratch),
            new OutputCaptureOptions(1024, 1024, 2048, RetainBytes: true));
        captureControl["launchCaptured"] = Describe(captureLaunch);
        if (captureLaunch is CaptureLaunchResult.Started startedCapture)
        {
            captureUnexpectedStart = true;
            captureControl["unexpectedStarted"] = true;
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(operationTimeoutSeconds));
            captureControl["emergencyTerminate"] = Describe(runtime.Terminate(startedCapture.Handle));
            captureControl["emergencyCompletion"] = Describe(await runtime.WaitForCompletionAsync(startedCapture.Handle, stopCts.Token));
            try
            {
                captureControl["captureCompletion"] = await startedCapture.Capture.Completion.WaitAsync(stopCts.Token);
            }
            catch (Exception ex) { captureControl["captureCompletionError"] = $"{ex.GetType().Name}: {ex.Message}"; }
            exitCode = 1;
        }
        else if (captureLaunch is CaptureLaunchResult.Failed failed)
        {
            captureControl["unexpectedStarted"] = false;
            captureControl["reasons"] = failed.Reasons;
        }
        captureControl["markerPresentBeforeCleanup"] = File.Exists(capturedMarker);
        DiscardResult discard = runtime.Discard(capturedAccepted.Prepared);
        capturedPreparation = null;
        captureControl["discard"] = Describe(discard);
        bool markerAbsent = !File.Exists(capturedMarker);
        captureControl["markerAbsent"] = markerAbsent;
        bool expectedRefusal = captureLaunch is CaptureLaunchResult.Failed expectedFailure
            && expectedFailure.Reasons.Any(reason => reason.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                && reason.Contains("no target dispatched", StringComparison.OrdinalIgnoreCase));
        captureControl["expectedPreDispatchRefusal"] = expectedRefusal;
        captureControl["passed"] = !captureUnexpectedStart && expectedRefusal
            && discard is DiscardResult.Discarded && markerAbsent;
        captureControl["canonicalBlocker"] = captureLaunch is CaptureLaunchResult.Failed refusal
            ? string.Join("; ", refusal.Reasons)
            : "capture unexpectedly started";
        if (captureControl["passed"] is not true) exitCode = 1;
    }
    else
    {
        captureControl["passed"] = false;
        captureControl["reason"] = "fresh preparation for capture control was refused";
        exitCode = 1;
    }
}
catch (Exception ex)
{
    result["probeException"] = new { type = ex.GetType().FullName, ex.Message, detail = ex.ToString() };
    exitCode = 1;
}
finally
{
    if (runtime is not null)
    {
        if (normalHandle is not null)
        {
            try
            {
                result["finallyTerminate"] = Describe(runtime.Terminate(normalHandle));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(operationTimeoutSeconds));
                result["finallyCompletion"] = Describe(await runtime.WaitForCompletionAsync(normalHandle, cts.Token));
            }
            catch (Exception ex) { result["finallyCleanupError"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
        }
        if (capturedPreparation is not null)
        {
            try { result["finallyDiscard"] = Describe(runtime.Discard(capturedPreparation)); }
            catch (Exception ex) { result["finallyDiscardError"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
        }
        try { await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(operationTimeoutSeconds)); }
        catch (Exception ex) { result["runtimeDispose"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
    }
    try
    {
        Directory.Delete(scratch, recursive: true);
        result["scratchCleanupConfirmed"] = true;
    }
    catch (Exception ex)
    {
        result["scratchCleanupConfirmed"] = false;
        result["scratchCleanupError"] = $"{ex.GetType().Name}: {ex.Message}";
        exitCode = 1;
    }
    result["endedAtUtc"] = DateTimeOffset.UtcNow;
    result["exitCode"] = exitCode;
    result["summary"] = new { lifecyclePositiveControlPassed = normalConfirmed, captureRefusalControlPassed = result.TryGetValue("captureRefusalControl", out object? c) && c is Dictionary<string, object?> cd && cd.TryGetValue("passed", out object? p) && p is true, captureUnexpectedStart, canonicalSupported = false };
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) { Console.Error.WriteLine($"Could not write evidence JSON: {ex.GetType().Name}: {ex.Message}"); exitCode = 1; }
}

Console.WriteLine(JsonSerializer.Serialize(new { outputPath, exitCode }));
return exitCode;

static object Describe(object value) => value switch
{
    PrepareResult.Accepted x => new { type = "Accepted", preparation = x.Prepared.PreparationId, met = x.Prepared.Met },
    PrepareResult.Rejected x => new { type = "Rejected", reasons = x.Reasons },
    LaunchResult.Started x => new { type = "Started", execution = x.Handle.ExecutionId },
    LaunchResult.Failed x => new { type = "Failed", reasons = x.Reasons },
    CaptureLaunchResult.Started x => new { type = "Started", execution = x.Handle.ExecutionId },
    CaptureLaunchResult.Failed x => new { type = "Failed", reasons = x.Reasons },
    TerminateResult.Terminated => new { type = "Terminated" },
    TerminateResult.Failed x => new { type = "Failed", reasons = x.Reasons },
    DiscardResult.Discarded => new { type = "Discarded" },
    DiscardResult.Failed x => new { type = "Failed", reasons = x.Reasons },
    CompletionResult.NaturalExit x => new { type = "NaturalExit", x.RootExitCode },
    CompletionResult.Terminated => new { type = "Terminated" },
    CompletionResult.Failed x => new { type = "Failed", reasons = x.Reasons },
    _ => new { type = value.GetType().Name, value = value.ToString() }
};

static async Task<bool> WaitForMarkerAsync(string path, string expected, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        while (!cts.IsCancellationRequested)
        {
            if (File.Exists(path) && await File.ReadAllTextAsync(path, cts.Token) == expected) return true;
            await Task.Delay(100, cts.Token);
        }
    }
    catch (OperationCanceledException) { }
    return false;
}

static async Task<object> ReadPackageIdentityAsync()
{
    string? directory = AppContext.BaseDirectory;
    while (directory is not null)
    {
        string manifest = Path.Combine(directory, "eng", "aaaw-m5-runtime-packages.json");
        if (File.Exists(manifest))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            var runtimePackage = document.RootElement.EnumerateArray()
                .First(item => item.GetProperty("id").GetString() == "Gagamba.Runtime");
            return new { manifestPath = manifest, packageId = runtimePackage.GetProperty("id").GetString(), version = runtimePackage.GetProperty("version").GetString(), sourceCommit = runtimePackage.GetProperty("commit").GetString(), packageSha256 = runtimePackage.GetProperty("sha256").GetString() };
        }
        directory = Directory.GetParent(directory)?.FullName;
    }
    return new { error = "aaaw-m5-runtime-packages.json not found from application base directory" };
}
