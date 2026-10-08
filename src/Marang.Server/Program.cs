using Marang;
using Marang.Http;
using Marang.Mcp;
using Penghou.Qingniao;

var builder = WebApplication.CreateBuilder(args);

// API keys and workspace roots come exclusively from host configuration
// (environment `Marang__ApiKeys__<caller>`, never committed or logged).
// Rotation is an env change plus restart; no code change.
builder.Services.Configure<MarangAuthenticationOptions>(
    builder.Configuration.GetSection("Marang"));
builder.Services.AddHttpContextAccessor();

// Fail fast on malformed authentication configuration.
var authenticationSection = builder.Configuration.GetSection("Marang");
var earlyAuthentication = authenticationSection.Get<MarangAuthenticationOptions>()
    ?? new MarangAuthenticationOptions();
earlyAuthentication.Validate();

// Composition root: Marang product policy (admission + verification) over
// the Qingniao delegated-execution runtime. No execution providers are
// registered yet, so delegations honestly wait for supervision until the
// Milestone 5 provider integration lands.
builder.Services.AddSingleton<MarangDelegationCatalog>();
builder.Services.AddSingleton(serviceProvider => new DelegationRuntime(
    serviceProvider.GetRequiredService<MarangDelegationCatalog>(),
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

// MarangDelegationTools is discovered from the assembly above. Wait,
// inspect, intervene, and artifact tools land here only after their
// authorization/fencing tests pass.

var app = builder.Build();

// Authentication lives at this boundary; Qingniao stays transport-unaware.
// Loopback requests skip it for local development. Everything else needs a
// key, and a rejection never touches delegation state.
var authentication = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MarangAuthenticationOptions>>().Value;
var authenticator = new MarangApiKeyAuthenticator(authentication.ApiKeys);
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/mcp") &&
        !context.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var remote = context.Connection.RemoteIpAddress;
    if (authentication.BypassLoopback && remote is not null && System.Net.IPAddress.IsLoopback(remote))
    {
        context.Items[MarangHttpContextKeys.CallerIdentity] = authentication.LocalCallerIdentity;
        await next();
        return;
    }

    var caller = authenticator.Authenticate(context.Request.Headers.Authorization.ToString());
    if (caller is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
        return;
    }

    context.Items[MarangHttpContextKeys.CallerIdentity] = caller;
    await next();
});

app.MapGet("/healthz", () => Results.Ok("marang"));
app.MapGet("/readyz", (DelegationRuntime _) => Results.Ok("ready"));
app.MapGet("/api/delegations", async (
    HttpContext context,
    MarangDelegationCatalog catalog,
    DelegationRuntime runtime,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var caller = (string)context.Items[MarangHttpContextKeys.CallerIdentity]!;
    var items = new List<object>();
    foreach (var record in catalog.List(caller))
    {
        var progress = await runtime.GetStatusAsync(record.Id, cancellationToken);
        if (progress is not null)
        {
            items.Add(new
            {
                id = record.Id.Value.ToString("D"),
                objective = record.Objective,
                provider = record.Provider,
                workspace = record.Workspace,
                state = progress.State.ToString(),
                updatedAt = progress.UpdatedAt,
                revision = progress.Revision,
            });
        }
    }

    return Results.Json(new { items });
});
app.MapGet("/api/delegations/{id:guid}", async (
    Guid id,
    HttpContext context,
    MarangDelegationCatalog catalog,
    DelegationRuntime runtime,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var caller = (string)context.Items[MarangHttpContextKeys.CallerIdentity]!;
    var (status, body) = await SupervisionHttpEndpoints.GetDetailAsync(
        runtime, catalog, caller, id, cancellationToken);
    return status == 200 ? Results.Json(body) : Results.NotFound();
});
app.MapGet("/api/delegations/{id:guid}/waiting", async (
    Guid id,
    HttpContext context,
    MarangDelegationCatalog catalog,
    DelegationRuntime runtime,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var caller = (string)context.Items[MarangHttpContextKeys.CallerIdentity]!;
    var (status, body) = await SupervisionHttpEndpoints.GetWaitingAsync(
        runtime, catalog, caller, id, cancellationToken);
    return status switch
    {
        200 => Results.Json(body),
        409 => Results.Conflict(body),
        _ => Results.NotFound(),
    };
});
app.MapPost("/api/delegations/{id:guid}/interventions", async (
    Guid id,
    HttpContext context,
    MarangDelegationCatalog catalog,
    DelegationRuntime runtime,
    InterventionHttpRequest request,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var caller = (string)context.Items[MarangHttpContextKeys.CallerIdentity]!;
    var (status, body) = await SupervisionHttpEndpoints.PostInterventionAsync(
        runtime, catalog, caller, id, request, cancellationToken);
    return status switch
    {
        200 => Results.Json(body),
        400 => Results.BadRequest(body),
        409 => Results.Conflict(body),
        _ => Results.NotFound(),
    };
});
app.MapMcp("/mcp");

app.Run();
