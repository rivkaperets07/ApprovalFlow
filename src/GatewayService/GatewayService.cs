using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// One JSON object per log line (fields include the named CorrelationId/TrackingId
// placeholders every business log call uses) instead of the default human-readable text
// formatter, so a request can actually be filtered/followed end-to-end by tooling.
builder.Logging.ClearProviders();
// IncludeScopes: every handler opens a BeginScope(CorrelationId) — without this the
// scope's fields are computed and then silently dropped, defeating the whole point of
// structured logging.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

// GLOBAL-FRAUD's "brand-new vendor" signal needs the same known-vendor list DecisionEngine
// uses for classification. Bind-mounted (see docker-compose.yml) and shared as config
// rather than a runtime service call, so an unreachable DecisionEngine can never block or
// slow down the immediate-acknowledgement path.
builder.Configuration.AddJsonFile("Ai/vendor-directory.json", optional: false, reloadOnChange: true);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "ApprovalFlow Gateway", Version = "v1" }));

var daprHttpEndpoint = builder.Configuration.GetValue<string>("DAPR_HTTP_ENDPOINT", "http://localhost:3500");
builder.Services.AddDaprClient(client => client.UseHttpEndpoint(daprHttpEndpoint));
builder.Services.AddSingleton<IDaprStateClient, DaprStateClient>();
builder.Services.AddSingleton<ISubmissionStore, DaprSubmissionStore>();
builder.Services.AddSingleton<IInvoiceNotifier, InvoiceNotifier>();
builder.Services.AddSingleton<IUserStore, DaprUserStore>();

// Bulkhead around every Gateway call into DecisionEngine (vendor directory reads/
// writes, policy reads/writes) — isolates that one dependency so it being slow or down
// can't exhaust Gateway's own thread/connection pool and take unrelated requests with it.
// A shared cap across all of AdminEndpoints/SubmissionEndpoints' DecisionEngine calls
// rather than one bulkhead per endpoint: they all funnel into the same downstream
// dependency, so that's the actual resource being protected.
builder.Services.AddSingleton(new Bulkhead(maxConcurrentCalls: 10));

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// AuthN/AuthZ with roles (submitter / approver / admin) via a self-signed JWT. The
// symmetric key comes from config/env — the checked-in default is for local demo only
// and .env.example documents overriding it. Dapr-sidecar-invoked
// endpoints (pub/sub deliveries, /dapr/subscribe) stay anonymous: the sidecar has no JWT,
// and those routes are unreachable from outside the compose network anyway.
// IsNullOrWhiteSpace, not ??: docker-compose's ${JWT_SIGNING_KEY:-} materializes the env
// var as an *empty string* when unset in .env, which is not null — an empty key would
// crash the JWT middleware on every request (IDX10703, zero-length key).
var configuredJwtKey = builder.Configuration.GetValue<string>("JWT_SIGNING_KEY");
var jwtSigningKey = string.IsNullOrWhiteSpace(configuredJwtKey) ? "approvalflow-demo-signing-key-32ch!" : configuredJwtKey;
builder.Services.AddSingleton(new JwtSigningKey(jwtSigningKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = JwtTokenIssuer.Issuer,
            ValidAudience = JwtTokenIssuer.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };

        // EventSource (the UI's notification channel) cannot set an Authorization
        // header, so /notifications also accepts the token as an access_token query
        // parameter — same pattern SignalR uses for WebSocket/SSE transports.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/notifications"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.Submitter, policy => policy.RequireRole("submitter", "admin"));
    options.AddPolicy(AuthPolicies.Approver, policy => policy.RequireRole("approver", "admin"));
    options.AddPolicy(AuthPolicies.Admin, policy => policy.RequireRole("admin"));
});

// Gateway is the single external entry point, so rate limiting lives here: per-client
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

// Traces + metrics, exported to the local Jaeger/Prometheus stack (docker-compose.yml).
// AddGrpcClientInstrumentation covers every DaprClient call (state, pub/sub, service
// invocation to DecisionEngine) since Dapr's .NET SDK talks gRPC to the sidecar — so a
// submit that fans out into DecisionEngine and PaymentService shows up as one connected
// trace, not three separate ones, as long as the receiving side is instrumented too
// (it is — same block in DecisionEngine.cs/PaymentService.cs).
var otlpEndpoint = builder.Configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT", "http://jaeger:4317")!;
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: "gateway"))
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

// Idempotent: only creates accounts that don't already exist yet, so this runs safely on
// every restart (see DemoUserSeeder for the credentials). Deferred to ApplicationStarted
// rather than awaited here: the Dapr sidecar doesn't finish initializing until it detects
// this app accepting connections, which only happens once app.Run() below actually starts
// Kestrel — awaiting the seed here would block app.Run() and deadlock against the very
// sidecar readiness it's waiting on.
app.Lifetime.ApplicationStarted.Register(() =>
    _ = DemoUserSeeder.SeedAsync(app.Services.GetRequiredService<IUserStore>(), app.Logger));

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApprovalFlow Gateway v1"));
app.UseCloudEvents();
app.MapSubscribeHandler();

// Real credential-based sign-in — register an account, then log in with it. See
// AuthEndpoints for why this replaced the earlier no-password /token.
app.MapAuthEndpoints();

app.MapGatewayEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
app.MapPrometheusScrapingEndpoint(); // GET /metrics

app.Run();
