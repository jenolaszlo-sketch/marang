using System.IO.Pipes;

namespace Marang.Tests.LifecycleBroker;

/// <summary>Probe-side client. It carries descriptions and receipts only; the
/// xUnit fixture remains the only holder of Gagamba execution handles.</summary>
public sealed class LifecycleBrokerClient
{
    private readonly string _pipeName;

    public LifecycleBrokerClient(string pipeName) =>
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("pipe name is required", nameof(pipeName))
            : pipeName;

    public async Task<LifecycleBrokerRunResult> RunCapturedAsync(
        LifecycleBrokerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var pipe = new NamedPipeClientStream(".", _pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000, cancellationToken).ConfigureAwait(false);
        var requestId = Guid.NewGuid();
        await LifecycleBrokerProtocol.WriteAsync(pipe,
            new LifecycleBrokerRequest(requestId, "Run", command),
            LifecycleBrokerProtocol.MaxRequestBytes, cancellationToken).ConfigureAwait(false);
        var response = await LifecycleBrokerProtocol.ReadAsync<LifecycleBrokerResponse>(pipe,
            LifecycleBrokerProtocol.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (response.RequestId != requestId)
            throw new InvalidDataException("broker response request id did not match");
        if (response.ProtocolError is { } error)
            throw new InvalidOperationException($"lifecycle broker rejected RPC: {error}");
        return response.Result ?? throw new InvalidDataException("broker returned no run result");
    }
}
