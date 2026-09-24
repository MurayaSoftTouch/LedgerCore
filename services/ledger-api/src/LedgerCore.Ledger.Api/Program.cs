using System.Text.Json;
using System.Text.Json.Serialization;
using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Configuration;
using LedgerCore.Ledger.Api.Endpoints;
using LedgerCore.Ledger.Api.Integration.Correlation;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Operations;
using LedgerCore.Ledger.Api.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});

builder.WebHost.ConfigureKestrel(RequestLimits.Apply);

builder.Services.AddLedgerOptions();

// No automatic schema changes at startup: migrations are applied explicitly by the schema owner.
builder.Services.AddDbContext<LedgerDbContext>((services, options) =>
    LedgerDatabase.Configure(options, services.GetRequiredService<IOptions<LedgerDatabaseOptions>>().Value.Ledger));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AccountCommands>();
builder.Services.AddScoped<JournalCommands>();
builder.Services.AddScoped<LedgerQueries>();
builder.Services.AddScoped<PolicyApproval>();
builder.Services.AddScoped<LedgerReconciliation>();
builder.Services.AddScoped<OutboxDiagnostics>();
builder.Services.AddPolicyDecisionClient();
builder.Services.AddHttpClient(PolicyServiceHealthCheck.ClientName);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
    options.SerializerOptions.MaxDepth = RequestLimits.MaximumJsonDepth;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LedgerExceptionHandler>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<LedgerDbContext>("ledger-database", tags: ["ready"])
    .AddCheck<LedgerSchemaHealthCheck>("ledger-schema", tags: ["ready"])
    .AddCheck<PolicyServiceHealthCheck>("policy-service", HealthStatus.Degraded, tags: ["dependency"]);
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseMiddleware<RequestSizeLimitMiddleware>();

if (app.Configuration.GetValue<bool>("Ledger:ExposeOpenApi"))
{
    app.MapOpenApi();
}

// Liveness: the process is running. Readiness: the ledger database is reachable and fully migrated.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
// Dependency state, reported separately from readiness: Degraded when the policy service is unreachable.
app.MapHealthChecks("/health/dependencies", new() { Predicate = check => check.Tags.Contains("dependency") });

app.MapLedgerEndpoints();
app.MapOperationsEndpoints();

app.Run();
