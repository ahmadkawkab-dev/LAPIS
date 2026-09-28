namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.Users;
using Xunit;

public sealed class BoardCreationLimitTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Only_five_owned_boards_are_allowed_and_deleting_one_reopens_a_slot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(cancellationToken);
        var owner = User("limit-owner@wukna.test");
        var otherOwner = User("limit-other@wukna.test");
        var ownedBoards = Enumerable.Range(0, 4)
            .Select(index => OwnedBoard(owner, $"Owned {index}"))
            .ToArray();
        var sharedBoard = OwnedBoard(otherOwner, "Shared with owner");
        sharedBoard.Memberships.Add(new BoardMembership
        {
            User = owner, Role = BoardRole.Guest, CanEdit = false
        });
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, otherOwner);
            db.Boards.AddRange([.. ownedBoards, sharedBoard]);
            await db.SaveChangesAsync(cancellationToken);
        }

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var client = Client(factory, owner, clock);

        using (var fifth = await client.PostAsJsonAsync(
            "/api/boards", new { title = "Fifth board" }, cancellationToken))
            Assert.Equal(HttpStatusCode.Created, fifth.StatusCode);
        using (var rejected = await client.PostAsJsonAsync(
            "/api/boards", new { title = "Sixth board" }, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            var error = await rejected.Content.ReadFromJsonAsync<Dictionary<string, string>>(
                cancellationToken);
            Assert.Equal("board_limit_reached", error?["error"]);
        }
        await AssertOwnedCount(owner.Id, 5, cancellationToken);

        using (var deleted = await client.DeleteAsync(
            $"/api/boards/{ownedBoards[0].Id}", cancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var replacement = await client.PostAsJsonAsync(
            "/api/boards", new { title = "Replacement board" }, cancellationToken))
            Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        await AssertOwnedCount(owner.Id, 5, cancellationToken);
    }

    [Fact]
    public async Task Concurrent_requests_can_create_only_one_remaining_board()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(cancellationToken);
        var owner = User("limit-concurrent@wukna.test");
        await using (var db = postgres.CreateContext())
        {
            db.Users.Add(owner);
            db.Boards.AddRange(Enumerable.Range(0, 4)
                .Select(index => OwnedBoard(owner, $"Existing {index}")));
            await db.SaveChangesAsync(cancellationToken);
        }

        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        using var firstClient = Client(factory, owner, clock);
        using var secondClient = Client(factory, owner, clock);
        var requests = new[]
        {
            firstClient.PostAsJsonAsync("/api/boards", new { title = "Concurrent A" }, cancellationToken),
            secondClient.PostAsJsonAsync("/api/boards", new { title = "Concurrent B" }, cancellationToken)
        };
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            await AssertOwnedCount(owner.Id, 5, cancellationToken);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    private async Task AssertOwnedCount(Guid userId, int expected, CancellationToken cancellationToken)
    {
        await using var db = postgres.CreateContext();
        Assert.Equal(expected, await db.BoardMemberships.CountAsync(
            membership => membership.UserId == userId && membership.Role == BoardRole.Owner,
            cancellationToken));
    }

    private static Board OwnedBoard(User owner, string title)
    {
        var board = new Board { Title = title };
        board.Memberships.Add(new BoardMembership
        {
            User = owner, Role = BoardRole.Owner, CanEdit = true
        });
        return board;
    }

    private static HttpClient Client(WuknaWebApplicationFactory factory, User user, TimeProvider clock)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtTokenGenerator(new JwtOptions
            {
                Issuer = WuknaWebApplicationFactory.JwtIssuer,
                Audience = WuknaWebApplicationFactory.JwtAudience,
                SigningKey = WuknaWebApplicationFactory.JwtSigningKey,
                AccessTokenMinutes = 60,
                RefreshTokenDays = 7
            }, clock).CreateAccessToken(user).Token);
        return client;
    }

    private static User User(string email) => new()
    {
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        EmailConfirmed = true
    };
}
