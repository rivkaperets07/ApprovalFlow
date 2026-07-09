using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// See DecisionEngine.cs for why this is pinned explicitly.
var currencyCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = currencyCulture;
CultureInfo.CurrentCulture = currencyCulture;

var builder = WebApplication.CreateBuilder(args);

// M14: structured (JSON) logging instead of the default plain-text console formatter, so
// every business log line's CorrelationId/TrackingId fields are machine-filterable.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

var daprHttpEndpoint = builder.Configuration.GetValue<string>("DAPR_HTTP_ENDPOINT", "http://localhost:3500");
builder.Services.AddDaprClient(client => client.UseHttpEndpoint(daprHttpEndpoint));
builder.Services.AddSingleton<IBudgetService, BudgetService>();
builder.Services.AddSingleton<IPaymentGateway, PaymentGateway>();
builder.Services.AddSingleton<IPaymentProcessor, PaymentProcessor>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "ApprovalFlow PaymentService", Version = "v1" }));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApprovalFlow PaymentService v1"));
app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapPaymentEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();
