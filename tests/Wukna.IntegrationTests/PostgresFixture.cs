namespace Wukna.IntegrationTests;

using Wukna.Shared.Data.AppDbContext;
using Wukna.Features.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("wukna_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => container.GetConnectionString();

    public WuknaDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<WuknaDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptors)
            .Options;
        return new WuknaDbContext(options);
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    // Historical migration tests must seed the old schema, not today's expanded User model.
    internal static Task<int> InsertHistoricalUserAsync(WuknaDbContext db, User user, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO asp_net_users
                (id, username, normalized_username, email, normalized_email, display_name,
                 email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
            VALUES ({user.Id}, {user.Username}, {user.NormalizedUsername}, {user.Email}, {user.NormalizedEmail},
                    {user.DisplayName}, {user.EmailConfirmed}, {user.PhoneNumberConfirmed}, {user.TwoFactorEnabled},
                    {user.LockoutEnabled}, {user.AccessFailedCount})
            """, ct);

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
