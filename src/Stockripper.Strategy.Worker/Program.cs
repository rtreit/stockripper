using Stockripper.Strategy.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration["Strategy:AccessToken"]))
{
    throw new InvalidOperationException("Set the Strategy access token before startup.");
}

builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<StrategyRuntimeService>();
app.MapGet("/", () => Results.NotFound());

app.Run();
