using System.Globalization;
using DecisionEngine.Ai;
using DecisionEngine.Core.Logic;
using DecisionEngine.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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
// IncludeScopes: HandleInvoiceSubmittedAsync opens a BeginScope(CorrelationId) — without
// this the scope's fields are computed and then silently dropped, defeating M14's point.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

builder.Configuration.AddJsonFile("Policies/policies.json", optional: false, reloadOnChange: true);
builder.Configuration.AddJsonFile("Ai/vendor-directory.json", optional: false, reloadOnChange: true);

builder.Services.AddDaprClient();
builder.Services.AddLogging();
builder.Services.AddSingleton<PolicyEngine>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "ApprovalFlow DecisionEngine", Version = "v1" }));

// N5: parsed once at startup — docs/policy.md is bind-mounted read-only (docker-compose.yml),
// same "no rebuild to tune it" spirit as Policies/policies.json, though PolicyRetriever
// doesn't watch it for live changes the way IConfiguration's reloadOnChange does.
var policyDocumentPath = builder.Configuration.GetValue<string>("PolicyDocumentPath", "docs/policy.md")!;
builder.Services.AddSingleton(PolicyRetriever.LoadFromFile(Path.Combine(builder.Environment.ContentRootPath, policyDocumentPath)));

var aiProviderName = builder.Configuration.GetValue<string>("AiProvider", "Stub");
if (string.Equals(aiProviderName, "Groq", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IAiModelProvider, GroqAiModelProvider>();
}
else
{
    builder.Services.AddSingleton<IAiModelProvider, StubAiModelProvider>();
}

// N4: traces + metrics, exported to the local Jaeger/Prometheus stack (docker-compose.yml).
// AddSource(DecisionEngineTelemetry.ActivitySourceName) picks up this service's own
// "ai.analyze_invoice"/"policy.evaluate" spans (InvoiceEndpoints.cs) alongside the
// auto-instrumented ASP.NET Core/HttpClient/gRPC ones (the last of which covers every Dapr
// state/pub-sub call, since DaprClient talks gRPC) — all landing in one distributed trace
// per request instead of separate, disconnected ones.
var otlpEndpoint = builder.Configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT", "http://jaeger:4317")!;
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: "decision-engine"))
    .WithTracing(tracing => tracing
        .AddSource(DecisionEngineTelemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddGrpcClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApprovalFlow DecisionEngine v1"));
app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapInvoiceEndpoints();
app.MapAdminEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
app.MapPrometheusScrapingEndpoint(); // N4: GET /metrics

app.Run();
