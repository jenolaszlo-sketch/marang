using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gagamba.Execution;
using Gagamba.Runtime;

const int readyTimeoutSeconds = 12;
const int operationTimeoutSeconds = 12;
string outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory, "macos-probe.json");
string scratch = Path.Combine(Path.GetTempPath(), $"aaaw-m5-macos-probe-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
string normalMarker = Path.Combine(scratch, "normal-ready");
string capturedMarker = Path.Combine(scratch, "captured-ready");
string invalidMarker = Path.Combine(scratch, "invalid-options-dispatched");
string ceilingMarker = Path.Combine(scratch, "over-ceiling-dispatched");
string expectedStdout = $"capture-stdout-{nonce}\n";
string expectedStderr = $"capture-stderr-{nonce}\n";

var result = new Dictionary<string, object?>
{
    ["schema"] = "aaaw-m5-macos-profile-probe-v2",
    ["startedAtUtc"] = DateTimeOffset.UtcNow,
    ["host"] = new { osDescription = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), machine = Environment.MachineName },
    ["source"] = await ReadPackageIdentityAsync(),
    ["canonicalSupported"] = false,
    ["canonicalSupportedScope"] = "Positive and negative profile preflight controls only; this does not prove Marang M5 semantic qualification.",
    ["canonicalQualification"] = "not performed by this test-only profile probe"
};

ExecutionRuntime? runtime = null;
PreparedExecution? negativePreparation = null;
PreparedExecution? positivePreparation = null;
ExecutionHandle? normalHandle = null;
ExecutionHandle? captureHandle = null;
OutputCaptureSession? captureSession = null;
bool normalConfirmed = false;
bool positiveCapturePassed = false;
bool negativeCapturePassed = false;
bool captureUnexpectedStart = false;
bool runtimeDisposed = false;
bool scratchCleanupConfirmed = false;
bool invalidMarkerAbsentAfterDispose = false;
bool ceilingMarkerAbsentAfterDispose = false;
int exitCode = 0;
try
{
    runtime = ExecutionRuntime.Create();
    var described = runtime.Describe();
    result["provider"] = new { runtime.HasProvider, runtime.RefusalReason, platform = described.Platform };
    result["capabilities"] = described.Grants.ToDictionary(pair => pair.Key.ToString(), pair => new { level = pair.Value.Level.ToString(), kind = pair.Value.Kind.ToString(), pair.Value.Evidence });
    var requirements = new ExecutionRequirements(new[]
    {
        ExecutionRequirement.Require(ExecutionCapability.UnitTermination, CapabilityLevel.Partial, reason: "LifecycleBrokerOwner M4 stop barrier"),
        ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit, CapabilityLevel.Partial, reason: "LifecycleBrokerOwner M4 descendant ownership"),
        ExecutionRequirement.Require(ExecutionCapability.RecursiveMembership, CapabilityLevel.Partial, reason: "LifecycleBrokerOwner M4 nested workload ownership")
    });

    var prepare = runtime.Prepare(requirements);
    var lifecycle = new Dictionary<string, object?> { ["prepare"] = Describe(prepare) };
    result["lifecyclePositiveControl"] = lifecycle;
    if (prepare is PrepareResult.Accepted accepted)
    {
        string script = $"printf '%s' '{nonce}' > '{normalMarker}'; exec /bin/sleep 90";
        LaunchResult launch = runtime.Launch(accepted.Prepared, ProcessStartSpec.Vector("/bin/sh", new[] { "-c", script }, scratch));
        lifecycle["launch"] = Describe(launch);
        if (launch is LaunchResult.Started started)
        {
            normalHandle = started.Handle;
            bool markerObserved = await WaitForMarkerAsync(normalMarker, nonce, TimeSpan.FromSeconds(readyTimeoutSeconds));
            lifecycle["markerObserved"] = markerObserved;
            TerminateResult terminate = runtime.Terminate(started.Handle);
            lifecycle["terminate"] = Describe(terminate);
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(operationTimeoutSeconds));
            CompletionResult completion = await runtime.WaitForCompletionAsync(started.Handle, waitCts.Token);
            lifecycle["completion"] = Describe(completion);
            normalConfirmed = markerObserved && terminate is TerminateResult.Terminated && completion is CompletionResult.Terminated;
            lifecycle["confirmedCleanup"] = normalConfirmed;
            normalHandle = null;
        }
        lifecycle["passed"] = normalConfirmed;
    }
    else lifecycle["passed"] = false;
    if (!normalConfirmed) exitCode = 1;

    var positivePrepare = runtime.Prepare(requirements);
    var positive = new Dictionary<string, object?> { ["prepare"] = Describe(positivePrepare), ["markerPath"] = capturedMarker, ["expectedStdout"] = expectedStdout, ["expectedStderr"] = expectedStderr };
    result["captureProfileControlPositive"] = positive;
    if (positivePrepare is PrepareResult.Accepted positiveAccepted)
    {
        positivePreparation = positiveAccepted.Prepared;
        string script = $"printf '%s' '{nonce}' > '{capturedMarker}'; printf '%s\\n' 'capture-stdout-{nonce}'; printf '%s\\n' 'capture-stderr-{nonce}' >&2; exit 0";
        CaptureLaunchResult launch = runtime.LaunchCaptured(positiveAccepted.Prepared,
            ProcessStartSpec.Vector("/bin/sh", new[] { "-c", script }, scratch),
            new OutputCaptureOptions(512, 512, 1024, RetainBytes: true));
        positive["launchCaptured"] = Describe(launch);
        if (launch is CaptureLaunchResult.Started started)
        {
            positivePreparation = null;
            captureHandle = started.Handle;
            captureSession = started.Capture;
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(operationTimeoutSeconds));
            CompletionResult completion = await runtime.WaitForCompletionAsync(started.Handle, waitCts.Token);
            positive["completion"] = Describe(completion);
            OutputCaptureResult capture = await started.Capture.Completion.WaitAsync(waitCts.Token);
            string stdout = Encoding.UTF8.GetString(capture.StdoutRetained.Span);
            string stderr = Encoding.UTF8.GetString(capture.StderrRetained.Span);
            positive["capture"] = new { status = capture.Status.ToString(), capture.StdoutBytesSeen, capture.StderrBytesSeen, stdout, stderr };
            bool markerObserved = await WaitForMarkerAsync(capturedMarker, nonce, TimeSpan.FromSeconds(readyTimeoutSeconds));
            positive["markerObserved"] = markerObserved;
            positiveCapturePassed = completion is CompletionResult.NaturalExit { RootExitCode: 0 }
                && capture.Status == OutputCaptureStatus.Complete
                && capture.StdoutBytesSeen == Encoding.UTF8.GetByteCount(expectedStdout)
                && capture.StderrBytesSeen == Encoding.UTF8.GetByteCount(expectedStderr)
                && stdout == expectedStdout && stderr == expectedStderr && markerObserved;
            positive["passed"] = positiveCapturePassed;
            captureHandle = null;
            captureSession = null;
        }
        else
        {
            DiscardResult discard = runtime.Discard(positiveAccepted.Prepared);
            positive["discard"] = Describe(discard);
            positive["preparationCleanupConfirmed"] = discard is DiscardResult.Discarded;
            if (discard is not DiscardResult.Discarded) exitCode = 1;
            positivePreparation = null;
            positive["passed"] = false;
        }
    }
    if (!positiveCapturePassed) exitCode = 1;

    var negativePrepare = runtime.Prepare(requirements);
    var negative = new Dictionary<string, object?> { ["prepare"] = Describe(negativePrepare), ["invalidMarkerPath"] = invalidMarker, ["overCeilingMarkerPath"] = ceilingMarker };
    result["captureProfileControlRefusalNegative"] = negative;
    if (negativePrepare is PrepareResult.Accepted negativeAccepted)
    {
        negativePreparation = negativeAccepted.Prepared;
        string invalidScript = $"printf '%s' '{nonce}' > '{invalidMarker}'; exec /bin/sleep 90";
        CaptureLaunchResult invalid = runtime.LaunchCaptured(negativeAccepted.Prepared,
            ProcessStartSpec.Vector("/bin/sh", new[] { "-c", invalidScript }, scratch),
            new OutputCaptureOptions(-1, 1, 2, RetainBytes: true));
        negative["invalidOptions"] = Describe(invalid);
        bool invalidRefused = invalid is CaptureLaunchResult.Failed;
        if (invalid is CaptureLaunchResult.Started unexpected)
        {
            captureUnexpectedStart = true;
            captureHandle = unexpected.Handle;
            captureSession = unexpected.Capture;
        }
        bool invalidMarkerAbsent = !File.Exists(invalidMarker);
        negative["invalidMarkerAbsentBeforeDiscard"] = invalidMarkerAbsent;

        bool ceilingRefused = false;
        bool ceilingMarkerAbsent = !File.Exists(ceilingMarker);
        if (invalidRefused && invalidMarkerAbsent)
        {
            string ceilingScript = $"printf '%s' '{nonce}' > '{ceilingMarker}'; exec /bin/sleep 90";
            CaptureLaunchResult overCeiling = runtime.LaunchCaptured(negativeAccepted.Prepared,
                ProcessStartSpec.Vector("/bin/sh", new[] { "-c", ceilingScript }, scratch),
                new OutputCaptureOptions(OutputCaptureOptions.MaxAllowedBytes + 1, 1, 2, RetainBytes: true));
            negative["overCeilingOptions"] = Describe(overCeiling);
            ceilingRefused = overCeiling is CaptureLaunchResult.Failed;
            if (overCeiling is CaptureLaunchResult.Started unexpectedCeiling)
            {
                captureUnexpectedStart = true;
                captureHandle = unexpectedCeiling.Handle;
                captureSession = unexpectedCeiling.Capture;
            }
            ceilingMarkerAbsent = !File.Exists(ceilingMarker);
        }
        negative["overCeilingMarkerAbsentBeforeDiscard"] = ceilingMarkerAbsent;
        DiscardResult discard = runtime.Discard(negativeAccepted.Prepared);
        negativePreparation = null;
        negative["discard"] = Describe(discard);
        negativeCapturePassed = invalidRefused && ceilingRefused && invalidMarkerAbsent && ceilingMarkerAbsent
            && discard is DiscardResult.Discarded && !captureUnexpectedStart;
        negative["passed"] = negativeCapturePassed;
    }
    else negative["passed"] = false;
    if (!negativeCapturePassed) exitCode = 1;
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
        if (captureHandle is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(operationTimeoutSeconds));
                result["finallyCaptureTerminate"] = Describe(runtime.Terminate(captureHandle));
                result["finallyCaptureCompletion"] = Describe(await runtime.WaitForCompletionAsync(captureHandle, cts.Token));
                if (captureSession is not null)
                {
                    captureSession.Abort();
                    try { result["finallyCaptureDrain"] = await captureSession.Completion.WaitAsync(cts.Token); }
                    catch (Exception ex) { result["finallyCaptureDrainError"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
                }
            }
            catch (Exception ex) { result["finallyCaptureCleanupError"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
        }
        if (negativePreparation is not null)
        {
            try { result["finallyDiscard"] = Describe(runtime.Discard(negativePreparation)); }
            catch (Exception ex) { result["finallyDiscardError"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
        }
        if (positivePreparation is not null)
        {
            try { result["finallyPositiveDiscard"] = Describe(runtime.Discard(positivePreparation)); }
            catch (Exception ex) { result["finallyPositiveDiscardError"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
        }
        try { await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(operationTimeoutSeconds)); runtimeDisposed = true; }
        catch (Exception ex) { result["runtimeDispose"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
    }
    invalidMarkerAbsentAfterDispose = !File.Exists(invalidMarker);
    ceilingMarkerAbsentAfterDispose = !File.Exists(ceilingMarker);
    result["captureProfileControlRefusalNegativePostDispose"] = new { invalidMarkerAbsent = invalidMarkerAbsentAfterDispose, overCeilingMarkerAbsent = ceilingMarkerAbsentAfterDispose };
    negativeCapturePassed &= invalidMarkerAbsentAfterDispose && ceilingMarkerAbsentAfterDispose;
    if (result.TryGetValue("captureProfileControlRefusalNegative", out object? negativeControl) && negativeControl is Dictionary<string, object?> negativeData)
    {
        negativeData["passed"] = negativeCapturePassed;
        negativeData["invalidMarkerAbsentAfterDispose"] = invalidMarkerAbsentAfterDispose;
        negativeData["overCeilingMarkerAbsentAfterDispose"] = ceilingMarkerAbsentAfterDispose;
    }
    if (!negativeCapturePassed) exitCode = 1;
    try { Directory.Delete(scratch, recursive: true); scratchCleanupConfirmed = true; }
    catch (Exception ex) { result["scratchCleanupError"] = $"{ex.GetType().Name}: {ex.Message}"; exitCode = 1; }
    result["runtimeDisposeConfirmed"] = runtimeDisposed;
    result["scratchCleanupConfirmed"] = scratchCleanupConfirmed;
    bool controlsPassed = normalConfirmed && positiveCapturePassed && negativeCapturePassed && !captureUnexpectedStart && runtimeDisposed && scratchCleanupConfirmed && exitCode == 0;
    result["canonicalSupported"] = controlsPassed;
    result["endedAtUtc"] = DateTimeOffset.UtcNow;
    result["exitCode"] = exitCode;
    result["summary"] = new { lifecyclePositiveControlPassed = normalConfirmed, captureProfileControlPositivePassed = positiveCapturePassed, captureProfileControlRefusalNegativePassed = negativeCapturePassed, captureUnexpectedStart, runtimeDisposed, scratchCleanupConfirmed, canonicalSupported = controlsPassed, canonicalSupportedScope = result["canonicalSupportedScope"] };
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
            var runtimePackage = document.RootElement.EnumerateArray().First(item => item.GetProperty("id").GetString() == "Gagamba.Runtime");
            return new { manifestPath = manifest, packageId = runtimePackage.GetProperty("id").GetString(), version = runtimePackage.GetProperty("version").GetString(), sourceCommit = runtimePackage.GetProperty("commit").GetString(), packageSha256 = runtimePackage.GetProperty("sha256").GetString() };
        }
        directory = Directory.GetParent(directory)?.FullName;
    }
    return new { error = "aaaw-m5-runtime-packages.json not found from application base directory" };
}
