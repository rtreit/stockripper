using Stockripper.Host.Components;
using Stockripper.Host.Runtime;
using Stockripper.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Stockripper");
var strategyAddressText = builder.Configuration["Strategy:Address"];
var strategyAccessToken = builder.Configuration["Strategy:AccessToken"];
var simulatedPaperEnabled = string.Equals(
    builder.Configuration["PaperBroker:Adapter"], "SIMULATED",
    StringComparison.Ordinal);
if (string.IsNullOrWhiteSpace(connectionString)
    || !Uri.TryCreate(strategyAddressText, UriKind.Absolute, out var strategyAddress)
    || string.IsNullOrWhiteSpace(strategyAccessToken))
{
    throw new InvalidOperationException(
        "Set the Stockripper database connection and Strategy access token before startup.");
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddStockripperInfrastructure(new StockripperInfrastructureOptions(
    connectionString,
    strategyAddress,
    strategyAccessToken,
    TimeSpan.FromSeconds(builder.Configuration.GetValue("Strategy:DeadlineSeconds", 10)),
    builder.Configuration.GetValue("Strategy:MaximumResponseBytes", 65_536),
    PaperMvpConfiguration.RiskPolicy,
    simulatedPaperEnabled
        ? PaperMvpConfiguration.BrokerOptions
        : null));
builder.Services.AddSingleton<StockripperRuntimeState>();
builder.Services.AddSingleton(new StockripperPeriodicReconciliationOptions(
    TimeSpan.FromSeconds(builder.Configuration.GetValue(
        "Reconciliation:IntervalSeconds", 60))));
builder.Services.AddHostedService<StockripperStartupService>();
builder.Services.AddHostedService<StockripperPeriodicReconciliationService>();
builder.Services.AddScoped<PhaseZeroWorkflow>();
builder.Services.AddScoped<PaperMvpWorkflow>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapGet("/health", (StockripperRuntimeState state) => state.IsReady
    ? Results.Ok(new
    {
        state.Status,
        ExecutionEnabled = simulatedPaperEnabled,
        Environment = simulatedPaperEnabled ? "PAPER" : "NONE",
        BrokerAdapter = simulatedPaperEnabled ? "SIMULATED" : "UNAVAILABLE",
        state.RecoveredSessionCount,
        state.RecoveredSubmissionCount,
        state.RecoveredCancellationCount,
        state.ReconciliationTargetCount,
        state.ReconciliationCurrentCount,
        state.ReconciliationLockedCount,
        state.ReconciliationUnavailableCount,
        state.LastReconciliationAt,
        state.PeriodicReconciliationFailed
    })
    : Results.Json(
        new
        {
            state.Status,
            ExecutionEnabled = simulatedPaperEnabled,
            Environment = simulatedPaperEnabled ? "PAPER" : "NONE",
            BrokerAdapter = simulatedPaperEnabled ? "SIMULATED" : "UNAVAILABLE"
        },
        statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();
