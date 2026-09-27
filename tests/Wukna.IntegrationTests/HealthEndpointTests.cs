namespace Wukna.IntegrationTests;

using System.Net;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed class HealthEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Health_reports_pending_migrations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(cancellationToken);

        await using var factory = new WuknaWebApplicationFactory(
            postgres, new ManualTimeProvider(DateTimeOffset.UtcNow));
        using var client = factory.CreateClient();
        using var healthy = await client.GetAsync("/health", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        await using (var db = postgres.CreateContext())
        {
            var latest = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Last();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"__EFMigrationsHistory\" WHERE migration_id = {latest}",
                cancellationToken);
        }

        using var unhealthy = await client.GetAsync("/health", cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
    }
}
