using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

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

// N1: AuthN/AuthZ with roles (submitter / approver / admin) via a self-signed JWT, as the
// assignment allows. The symmetric key comes from config/env — the checked-in default is
// for local demo only and .env.example documents overriding it. Dapr-sidecar-invoked
// endpoints (pub/sub deliveries, /dapr/subscribe) stay anonymous: the sidecar has no JWT,
// and those routes are unreachable from outside the compose network anyway (M6).
// IsNullOrWhiteSpace, not ??: docker-compose's ${JWT_SIGNING_KEY:-} materializes the env
// var as an *empty string* when unset in .env, which is not null — an empty key would
// crash the JWT middleware on every request (IDX10703, zero-length key).
var configuredJwtKey = builder.Configuration.GetValue<string>("JWT_SIGNING_KEY");
var jwtSigningKey = string.IsNullOrWhiteSpace(configuredJwtKey) ? "approvalflow-demo-signing-key-32ch!" : configuredJwtKey;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = DemoTokenIssuer.Issuer,
            ValidAudience = DemoTokenIssuer.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };

        // EventSource (the UI's M8 notification channel) cannot set an Authorization
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
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ApprovalFlow Gateway v1"));
app.UseCloudEvents();
app.MapSubscribeHandler();

// N1: demo token issuance — anonymous by design (it IS the login). No password check on
// purpose: the demo demonstrates role-based authorization wiring, not identity management.
app.MapPost("/token", (TokenRequest request) =>
{
    var role = request.Role?.Trim().ToLowerInvariant() ?? "";
    if (!DemoTokenIssuer.ValidRoles.Contains(role))
    {
        return Results.BadRequest(new { error = $"Role must be one of: {string.Join(", ", DemoTokenIssuer.ValidRoles)}." });
    }

    var token = DemoTokenIssuer.IssueToken(role, string.IsNullOrWhiteSpace(request.Name) ? "demo-user" : request.Name!, jwtSigningKey);
    return Results.Ok(new { token, role });
});

app.MapGatewayEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();
