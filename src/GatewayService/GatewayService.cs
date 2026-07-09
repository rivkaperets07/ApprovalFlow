using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// M14: one JSON object per log line (fields include the named CorrelationId/TrackingId
// placeholders every business log call uses) instead of the default human-readable text
// formatter, so a request can actually be filtered/followed end-to-end by tooling.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// GLOBAL-FRAUD's "brand-new vendor" signal needs the same known-vendor list DecisionEngine
// uses for classification. Bind-mounted (see docker-compose.yml) and shared as config
// rather than a runtime service call, so an unreachable DecisionEngine can never block or
// slow down the immediate-acknowledgement path (M8).
builder.Configuration.AddJsonFile("Ai/vendor-directory.json", optional: false, reloadOnChange: true);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "ApprovalFlow Gateway", Version = "v1" }));

var daprHttpEndpoint = builder.Configuration.GetValue<string>("DAPR_HTTP_ENDPOINT", "http://localhost:3500");
builder.Services.AddDaprClient(client => client.UseHttpEndpoint(daprHttpEndpoint));
builder.Services.AddSingleton<IDaprStateClient, DaprStateClient>();
builder.Services.AddSingleton<ISubmissionStore, DaprSubmissionStore>();
builder.Services.AddSingleton<IInvoiceNotifier, InvoiceNotifier>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Gateway is the single external entry point (M6), so rate limiting lives here: per-client
// IP, fixed window. Requests over the limit get a 429 instead of being queued indefinitely.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            }));
});

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApprovalFlow Gateway v1"));
app.UseCloudEvents();
app.MapSubscribeHandler();

app.MapGatewayEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();
