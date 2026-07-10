using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GatewayService.IntegrationTests;

/// <summary>
/// Boots the real Gateway pipeline in-memory (JWT bearer auth, role-based authorization
/// policies, rate limiting, routing, model binding — all real) with only the Dapr-backed
/// persistence swapped for an in-memory fake, so these tests need no live Dapr sidecar,
/// Redis, or Docker. That persistence layer already has its own unit tests
/// (DaprUserStoreTests); this factory exists to prove the HTTP-facing wiring around it —
/// auth middleware, authorization policies, request/response shapes — actually works
/// end-to-end through a real ASP.NET Core request pipeline, not mocked away entirely.
/// </summary>
public class GatewayApiFactory : WebApplicationFactory<Program>
{
    public FakeUserStore UserStore { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // GatewayService.cs loads Ai/vendor-directory.json relative to the content root;
        // in Docker that's a bind mount, so on the bare host it only exists here (see the
        // csproj's CopyToOutputDirectory item for TestFixtures).
        builder.UseContentRoot(Path.Combine(AppContext.BaseDirectory, "TestFixtures"));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserStore>();
            services.AddSingleton<IUserStore>(UserStore);
        });
    }
}
