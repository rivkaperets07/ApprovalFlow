using System.Globalization;
using DecisionEngine.Ai;
using DecisionEngine.Core.Logic;
using DecisionEngine.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The container's default culture has no currency symbol (renders "$" as "¤"), which
// would otherwise leak into every escalation/approval Reason string. Pin it explicitly
// rather than relying on the host's locale.
var currencyCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = currencyCulture;
CultureInfo.CurrentCulture = currencyCulture;

var builder = WebApplication.CreateBuilder(args);

// M14: structured (JSON) logging instead of the default plain-text console formatter, so
// every business log line's CorrelationId/TrackingId fields are machine-filterable.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

builder.Configuration.AddJsonFile("Policies/policies.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("Ai/vendor-directory.json", optional: false, reloadOnChange: true);

builder.Services.AddDaprClient();
builder.Services.AddLogging();
builder.Services.AddSingleton<PolicyEngine>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "ApprovalFlow DecisionEngine", Version = "v1" }));

var aiProviderName = builder.Configuration.GetValue<string>("AiProvider", "Stub");
if (string.Equals(aiProviderName, "Groq", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IAiModelProvider, GroqAiModelProvider>();
}
else
{
    builder.Services.AddSingleton<IAiModelProvider, StubAiModelProvider>();
}

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApprovalFlow DecisionEngine v1"));
app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapInvoiceEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();
