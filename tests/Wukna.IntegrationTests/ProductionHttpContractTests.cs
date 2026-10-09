namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Wukna.Features.Board;
using Wukna.Features.Users;
using Wukna.Shared.Errors;
using Xunit;

public sealed class ProductionHttpContractTests(PostgresFixture postgres)
{
    private const string TraceId = "0123456789abcdef0123456789abcdef";
    private const string ParentSpanId = "0123456789abcdef";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("application/json", false)]
    [InlineData("text/html", false)]
    [InlineData("application/problem+json", true)]
    public async Task Unexpected_exception_returns_safe_500_problem_details_correlated_with_one_handler_log(
        string accept, bool dependencyCancellation)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await SeedAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient(user);
        var probe = factory.Inject(dependencyCancellation
            ? HttpFailureMode.DependencyCancellation : HttpFailureMode.Unhandled);
        using var request = FailureRequest(probe);
        request.Headers.Accept.ParseAdd(accept);
        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("Kestrel", response.Headers.Server.ToString());
        Assert.NotNull(response.Headers.Date);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("traceparent"));
        Assert.False(response.Headers.Contains("X-Trace-Id"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(["code", "status", "title", "traceId", "type"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.6.1", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("An unexpected error occurred.", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(500, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("server_error", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(TraceId, body.RootElement.GetProperty("traceId").GetString());
        Assert.False(probe.RequestToken.IsCancellationRequested);

        var log = await probe.HandledErrorLog.Task.WaitAsync(GateTimeout, ct);
        Assert.Equal(typeof(UnexpectedExceptionHandler).FullName, log.Category);
        Assert.Equal(1, log.EventId);
        AssertCorrelatedLog(probe, log);
        Assert.Equal(TraceId, log.Properties["TraceId"]);
        Assert.Equal(probe.RequestId, log.Properties["RequestId"]);
        using var healthy = await client.GetAsync("/api/profile", ct);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Single(probe.FailureLogs);
        Assert.False(probe.ErrorLog.Task.IsCompleted);
    }

    [Fact]
    public async Task Client_cancellation_reaches_the_database_request_token_and_leaves_the_host_usable()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await SeedAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient(user);
        var probe = factory.Inject(HttpFailureMode.Cancel);
        using var request = FailureRequest(probe);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = client.SendAsync(request, canceled.Token);
        try
        {
            await probe.Entered.Task.WaitAsync(GateTimeout, ct);
            Assert.Equal(probe.RequestToken, probe.DatabaseToken);
            Assert.True(probe.DatabaseToken.CanBeCanceled);
            Assert.Equal(TraceId, probe.TraceId);

            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await probe.CancellationObserved.Task.WaitAsync(GateTimeout, ct);
            Assert.True(probe.RequestToken.IsCancellationRequested);
            Assert.True(probe.DatabaseToken.IsCancellationRequested);
            Assert.IsAssignableFrom<OperationCanceledException>(probe.Exception);
        }
        finally { canceled.Cancel(); }

        using var healthy = await client.GetAsync("/api/profile", ct);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.False(probe.HandledErrorLog.Task.IsCompleted);
    }

    [Fact]
    public async Task Exception_after_headers_are_sent_preserves_partial_200_and_aborts_the_transport()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await SeedAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient(user);
        var probe = factory.Inject(HttpFailureMode.AfterResponseStarted);
        using var request = FailureRequest(probe);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            await probe.Started.Task.WaitAsync(GateTimeout, ct);
            Assert.True(probe.ResponseStarted);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.ToString());
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(HttpFailureProbe.Prefix.Length + 1, response.Content.Headers.ContentLength);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var prefix = new byte[HttpFailureProbe.Prefix.Length];
            await body.ReadExactlyAsync(prefix, ct);
            Assert.Equal(HttpFailureProbe.Prefix, Encoding.UTF8.GetString(prefix));
            probe.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<HttpIOException>(async () =>
                await body.ReadAsync(new byte[1], ct).AsTask().WaitAsync(GateTimeout, ct));
            Assert.Equal(HttpRequestError.ResponseEnded, failure.HttpRequestError);
            await AssertCorrelatedLogAsync(probe, ct);
            Assert.False(probe.HandledErrorLog.Task.IsCompleted);
        }
        finally { probe.Release.TrySetResult(); }

        using var healthy = await client.GetAsync("/api/profile", ct);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Theory]
    [InlineData("/api/boards", "{\"title\":\"\"}", "{\"error\":\"Title must contain 1 to 200 characters.\"}")]
    [InlineData("/api/tasks?view=invalid", null, "{\"code\":\"invalid_task_view\"}")]
    public async Task Expected_error_and_code_objects_keep_their_exact_json_contracts(
        string path, string? payload, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await SeedAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient(user);
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct);
        await AssertJsonAsync(response, HttpStatusCode.BadRequest, expected, ct);
    }

    [Fact]
    public async Task Identity_password_validation_remains_an_errors_array_with_no_problem_details()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient();
        using var csrf = await client.GetAsync("/api/auth/csrf", ct);
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        using var tokens = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync(ct));
        var cookie = Assert.Single(csrf.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("wukna.csrf=", StringComparison.Ordinal));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { email = "validation@wukna.test", password = "abcdefgh" })
        };
        request.Headers.Add("Cookie", cookie.Split(';', 2)[0]);
        request.Headers.Add("X-CSRF-TOKEN", tokens.RootElement.GetProperty("token").GetString());
        using var response = await client.SendAsync(request, ct);
        await AssertJsonAsync(response, HttpStatusCode.BadRequest,
            "{\"errors\":[\"Passwords must have at least one non alphanumeric character.\",\"Passwords must have at least one digit ('0'-'9').\",\"Passwords must have at least one uppercase ('A'-'Z').\"]}", ct);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Missing_csrf_keeps_the_mixed_code_and_error_object()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient();
        using var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "csrf@wukna.test", password = "ValidPassword123!" }, ct);
        await AssertJsonAsync(response, HttpStatusCode.BadRequest,
            "{\"code\":\"invalid_csrf\",\"error\":\"Invalid CSRF token.\"}", ct);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Existing_profile_problem_details_keep_their_fields_and_media_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await SeedAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient(user);
        using var response = await client.PatchAsJsonAsync("/api/profile",
            new { username = "!", displayName = "Valid" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(["code", "message", "status", "title", "type"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("https://tools.ietf.org/html/rfc9110#section-15.5.1", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("invalid_username", body.RootElement.GetProperty("code").GetString());
        const string message = "Use 3–30 letters, numbers, periods, underscores, or hyphens.";
        Assert.Equal(message, body.RootElement.GetProperty("title").GetString());
        Assert.Equal(message, body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Authorization_and_hidden_resource_responses_have_empty_bodies()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SeedAsync(ct);
        var guest = NewUser("guest@wukna.test");
        var board = new Board { Title = "HTTP contracts" };
        board.Memberships.Add(new BoardMembership { User = owner, Role = BoardRole.Owner, CanEdit = true });
        board.Memberships.Add(new BoardMembership { User = guest, Role = BoardRole.Guest, CanEdit = false });
        await using (var db = postgres.CreateContext())
        {
            db.Users.Attach(owner);
            db.Boards.Add(board);
            await db.SaveChangesAsync(ct);
        }
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var anonymous = factory.CreateHttpClient();
        using var unauthorized = await anonymous.GetAsync("/api/profile", ct);
        await AssertEmptyAsync(unauthorized, HttpStatusCode.Unauthorized, ct);
        Assert.Equal("Bearer", unauthorized.Headers.WwwAuthenticate.ToString());
        Assert.Null(unauthorized.Headers.CacheControl);

        using var client = factory.CreateHttpClient(guest);
        using var forbidden = await client.PutAsJsonAsync($"/api/boards/{board.Id}/guests",
            new { email = owner.Email, canEdit = true }, ct);
        await AssertEmptyAsync(forbidden, HttpStatusCode.Forbidden, ct);
        Assert.Empty(forbidden.Headers.WwwAuthenticate);
        using var hidden = await client.GetAsync($"/api/boards/{Guid.NewGuid()}/notes", ct);
        await AssertEmptyAsync(hidden, HttpStatusCode.NotFound, ct);
    }

    [Theory]
    [InlineData("/api/tasks", "{", "application/json", 400)]
    [InlineData("/api/tasks", "", "application/json", 400)]
    [InlineData("/api/tasks", "{}", "text/plain", 415)]
    [InlineData("/api/tasks?limit=abc", null, null, 400)]
    public async Task Minimal_api_binding_errors_have_empty_production_responses(
        string path, string? payload, string? mediaType, int status)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await SeedAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient(user);
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, mediaType!);
        using var response = await client.SendAsync(request, ct);
        await AssertEmptyAsync(response, (HttpStatusCode)status, ct);
        Assert.Null(response.Headers.CacheControl);
        Assert.Empty(response.Headers.Pragma);
        Assert.Null(response.Content.Headers.Expires);
    }

    [Theory]
    [InlineData("{", "application/json", false, 400)]
    [InlineData("{}", "text/plain", false, 415)]
    [InlineData(null, "application/json", false, 413)]
    [InlineData(null, "application/json", true, 413)]
    public async Task Bounded_chat_binding_errors_keep_their_empty_status_responses(
        string? payload, string mediaType, bool chunked, int status)
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await SeedAsync(ct);
        await using var factory = new ProductionKestrelApplicationFactory(postgres);
        using var client = factory.CreateHttpClient(user);
        payload ??= new string('x', 32 * 1024 + 1);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{Guid.NewGuid()}/chat/messages")
        {
            Content = new StringContent(payload, Encoding.UTF8, mediaType)
        };
        request.Headers.TransferEncodingChunked = chunked;
        using var response = await client.SendAsync(request, ct);
        await AssertEmptyAsync(response, (HttpStatusCode)status, ct);
        Assert.Null(response.Headers.CacheControl);
        Assert.Empty(response.Headers.Pragma);
        Assert.Null(response.Content.Headers.Expires);
    }

    private async Task<User> SeedAsync(CancellationToken ct)
    {
        await postgres.ResetAsync(ct);
        var user = NewUser("contracts@wukna.test");
        await using var db = postgres.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    private static User NewUser(string email) => new()
    {
        Email = email, NormalizedEmail = email.ToUpperInvariant(),
        UserName = email, NormalizedUserName = email.ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString()
    };

    private static HttpRequestMessage FailureRequest(HttpFailureProbe probe)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/profile");
        request.Headers.Add(HttpFailureProbe.Header, probe.Id);
        request.Headers.Add("traceparent", $"00-{TraceId}-{ParentSpanId}-01");
        return request;
    }

    private static async Task AssertCorrelatedLogAsync(HttpFailureProbe probe, CancellationToken ct)
    {
        var log = await probe.ErrorLog.Task.WaitAsync(GateTimeout, ct);
        Assert.Equal("Microsoft.AspNetCore.Server.Kestrel", log.Category);
        Assert.Equal(13, log.EventId); // Kestrel's ApplicationError event.
        AssertCorrelatedLog(probe, log);
    }

    private static void AssertCorrelatedLog(HttpFailureProbe probe, HttpFailureLog log)
    {
        Assert.Same(probe.Exception, log.Exception);
        Assert.Equal(TraceId, probe.TraceId);
        Assert.NotEqual(ParentSpanId, probe.SpanId);
        Assert.False(string.IsNullOrEmpty(probe.RequestId));
        Assert.Equal(probe.RequestId, log.Scopes["RequestId"]);
        Assert.Equal(probe.TraceId, log.Scopes["TraceId"]);
        Assert.Equal(probe.SpanId, log.Scopes["SpanId"]);
    }

    private static async Task AssertEmptyAsync(HttpResponseMessage response, HttpStatusCode status, CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal("", await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    private static async Task AssertJsonAsync(HttpResponseMessage response, HttpStatusCode status,
        string expected, CancellationToken ct)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(expected, await response.Content.ReadAsStringAsync(ct));
    }
}
