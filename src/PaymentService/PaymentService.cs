using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// See DecisionEngine.cs for why this is pinned explicitly.
var currencyCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentCulture = currencyCulture;
CultureInfo.CurrentCulture = currencyCulture;

var builder = WebApplication.CreateBuilder(args);

// Structured (JSON) logging instead of the default plain-text console formatter, so
// every business log line's CorrelationId/TrackingId fields are machine-filterable.
builder.Logging.ClearProviders();
// IncludeScopes: ProcessPaymentAsync/PaymentProcessor open a BeginScope(CorrelationId) —
// without this the scope's fields are computed and then silently dropped, defeating
// the whole point of structured logging.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

var daprHttpEndpoint = builder.Configuration.GetValue<string>("DAPR_HTTP_ENDPOINT", "http://localhost:3500");
builder.Services.AddDaprClient(client => client.UseHttpEndpoint(daprHttpEndpoint));
builder.Services.AddSingleton<IBudgetService, BudgetService>();
builder.Services.AddSingleton<IPaymentGateway, PaymentGateway>();
builder.Services.AddSingleton<IPaymentStateStore, DaprPaymentStateStore>();
builder.Services.AddSingleton<IPaymentProcessor, PaymentProcessor>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "ApprovalFlow PaymentService", Version = "v1" }));

// Traces + metrics, exported to the local Jaeger/Prometheus stack (docker-compose.yml).
// See GatewayService.cs for why AddGrpcClientInstrumentation matters here too — this is
// the tail end of the invoice.approved -> payment.completed chain, and needs to be
// instrumented for that whole chain to land in one trace instead of stopping short.
var otlpEndpoint = builder.Configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT", "http://jaeger:4317")!;
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: "payment-service"))
    .WithTracing(tracing => tracing
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
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApprovalFlow PaymentService v1"));
app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapPaymentEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
app.MapPrometheusScrapingEndpoint(); // GET /metrics

app.Run();
