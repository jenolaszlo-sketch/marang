using Marang.Mcp;

var builder = WebApplication.CreateBuilder(args);

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
