using Marang;
using Marang.Mcp;
using Penghou.Qingniao;

var builder = WebApplication.CreateBuilder(args);

// Composition root: Marang product policy (admission + verification) over
// the Qingniao delegated-execution runtime. No execution providers are
// registered yet, so delegations honestly wait for supervision until the
// Milestone 5 provider integration lands.
builder.Services.AddSingleton(_ => new DelegationRuntime(
    new InMemoryDelegationAcceptanceRegistry(),
    new MarangAdmissionVerifier(),
    new InMemoryProviderRegistry(),
    new InMemoryExternalOperationProviderCatalog(),
    verificationPolicy: new ImplementVerificationPolicy()));

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        // Stateless: Marang keeps delegation state in its own runtime.
        // No server-to-client sampling or elicitation is needed.
        options.Stateless = true;
    })
    .WithToolsFromAssembly(typeof(MarangConnectivityTools).Assembly);

// Delegation tools land here as Marang.Mcp tool types are implemented:
// .WithTools<MarangDelegationTools>()

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("marang"));
app.MapMcp("/mcp");

app.Run();
