using System.Buffers.Binary;
using System.Text.Json;

namespace Marang.Tests.LifecycleBroker;

/// <summary>
/// Test-only, bounded framing for the local xUnit-owner/probe RPC. A frame is
/// a little-endian Int32 byte count followed by one UTF-8 JSON value.
/// </summary>
internal static class LifecycleBrokerProtocol
{
    internal const int MaxRequestBytes = 1 * 1024 * 1024;
    internal const int MaxResponseBytes = 32 * 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task WriteAsync<T>(Stream stream, T value, int maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length == 0 || payload.Length > maxBytes)
            throw new InvalidDataException($"RPC frame size {payload.Length} is outside 1..{maxBytes} bytes.");

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, int maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxBytes)
            throw new InvalidDataException($"RPC frame size {length} is outside 1..{maxBytes} bytes.");

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
            ?? throw new InvalidDataException("RPC frame contained JSON null.");
    }
}

/// <summary>Serializable command description; no Gagamba handles cross RPC.</summary>
public sealed record LifecycleBrokerCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int DeadlineMilliseconds = 600_000,
    int StdoutLimitBytes = 8 * 1024 * 1024,
    int StderrLimitBytes = 8 * 1024 * 1024,
    int TotalLimitBytes = 16 * 1024 * 1024,
    int StopGraceMilliseconds = 5_000)
{
    /// <summary>
    /// Environment variables that affect command discovery and .NET/NuGet
    /// execution. Values are sent over the current-user-only local pipe and
    /// are never included in broker receipts or diagnostics.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CaptureExecutionEnvironment()
    {
        string[] exact = OperatingSystem.IsWindows()
            ? new[] { "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "WINDIR", "COMSPEC", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
                "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "ALLUSERSPROFILE",
                "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64",
                "DOTNET_CLI_HOME", "DOTNET_MULTILEVEL_LOOKUP", "NUGET_PACKAGES" }
            : new[] { "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TMPDIR", "TMP", "TEMP",
                "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_MULTILEVEL_LOOKUP",
                "NUGET_PACKAGES" };
        var names = new HashSet<string>(exact,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var values = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value) continue;
            if (names.Contains(name) || name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase))
                values[name] = value;
        }
        return values;
    }

    public static string ResolveExecutable(string executable)
    {
        if (Path.IsPathRooted(executable))
            return File.Exists(executable) ? Path.GetFullPath(executable)
                : throw new FileNotFoundException("Qualification executable is absent.", executable);
        var name = OperatingSystem.IsWindows() && !Path.HasExtension(executable) ? executable + ".exe" : executable;
        foreach (var directory in (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Qualification executable was not found on PATH.", executable);
    }
}

/// <summary>Safe-to-serialize result of one Gagamba-owned invocation.</summary>
public sealed record LifecycleBrokerRunResult(
    Guid BrokerRunId,
    bool Accepted,
    string? Failure,
    string Platform,
    string Cause,
    string Cleanup,
    int? ExitCode,
    string? CaptureStatus,
    long StdoutBytesSeen,
    long StderrBytesSeen,
    byte[] Stdout,
    byte[] Stderr,
    IReadOnlyList<string> Reasons)
{
    public string StandardOutputText => System.Text.Encoding.UTF8.GetString(Stdout);
    public string StandardErrorText => System.Text.Encoding.UTF8.GetString(Stderr);
    public string CombinedOutput => StandardOutputText + Environment.NewLine + StandardErrorText;
}

internal sealed record LifecycleBrokerRequest(
    Guid RequestId,
    string Operation,
    LifecycleBrokerCommand? Command);

internal sealed record LifecycleBrokerResponse(
    Guid RequestId,
    LifecycleBrokerRunResult? Result,
    string? ProtocolError);
