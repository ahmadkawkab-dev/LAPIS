namespace Wukna.IntegrationTests;

using Wukna.Shared.Data.AppDbContext;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public class WuknaWebApplicationFactory(
    PostgresFixture postgres,
    ManualTimeProvider clock) : WebApplicationFactory<Program>
{
    public const string JwtIssuer = "Wukna.IntegrationTests";
    public const string JwtAudience = "Wukna.IntegrationTests.Client";
    public const string JwtSigningKey = "integration-test-signing-key-at-least-32-bytes-long";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", postgres.ConnectionString);
        // Existing tests inspect pending outbox records deterministically. Chat
        // realtime tests explicitly enable polling or drive the dispatcher.
        builder.UseSetting("Chat:Outbox:Enabled", "false");
        builder.UseSetting("Jwt:Issuer", JwtIssuer);
        builder.UseSetting("Jwt:Audience", JwtAudience);
        builder.UseSetting("Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("Jwt:AccessTokenMinutes", "60");
        builder.UseSetting("Jwt:RefreshTokenDays", "7");
        builder.UseSetting("Authentication:Google:ClientId", "integration-test-client");
        builder.UseSetting("Authentication:Google:ClientSecret", "integration-test-secret");
        builder.UseSetting("Authentication:Google:FrontendBaseUrl", "http://localhost:5173");
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<WuknaDbContext>();
            services.RemoveAll<DbContextOptions<WuknaDbContext>>();
            services.AddDbContext<WuknaDbContext>(options =>
                options.UseNpgsql(postgres.ConnectionString)
                    .UseSnakeCaseNamingConvention());
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
    }
}
