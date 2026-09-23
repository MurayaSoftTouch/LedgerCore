using LedgerCore.Ledger.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});

builder.Services
    .AddOptions<LedgerApiOptions>()
    .BindConfiguration(LedgerApiOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Ledger:ExposeOpenApi"))
{
    app.MapOpenApi();
}

// Liveness: the process is running. Readiness gains a database check in Milestone 1.
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();
