namespace Wukna.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oci.Common.Auth;
using Oci.Common.Model;
using Oci.ObjectstorageService;
using Wukna.Features.Chat;
using Xunit;

public sealed class OciChatAttachmentStoreTests
{
    private static ChatAttachmentOptions Settings() => new()
    {
        Enabled = true, Provider = "Oci", IoTimeoutSeconds = 2,
        MaxFileBytes = 1024, MaxImageBytes = 2048, MaxPreviewBytes = 1024,
        Oci = new() { Region = "eu-frankfurt-1", Namespace = "testns", Bucket = "chat-private", Prefix = "test-chat" }
    };

    private sealed class Credentials : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "wukna-oci-test-" + Guid.NewGuid().ToString("N"));
        public readonly IBasicAuthenticationDetailsProvider Provider;
        public Credentials()
        {
            Directory.CreateDirectory(directory);
            using var rsa = RSA.Create(2048);
            var keyPath = Path.Combine(directory, "key.pem");
            File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
            var configPath = Path.Combine(directory, "config");
            File.WriteAllText(configPath, $"[DEFAULT]\nuser=ocid1.user.oc1..test\nfingerprint=00:11:22\ntenancy=ocid1.tenancy.oc1..test\nregion=eu-frankfurt-1\nkey_file={keyPath}\n");
            var wrapped = DispatchProxy.Create<IBasicAuthenticationDetailsProvider, BasicCredentialsProxy>();
            ((BasicCredentialsProxy)(object)wrapped).Inner = new ConfigFileAuthenticationDetailsProvider(configPath, "DEFAULT");
            Provider = wrapped;
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    // The local test endpoint must not be replaced by region discovery in the SDK.
    public class BasicCredentialsProxy : DispatchProxy
    {
        public IBasicAuthenticationDetailsProvider Inner = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Invoke(Inner, args);
    }

    // This is an HTTP fixture, not a fake storage adapter: the production Oracle SDK
    // signs and serializes every request and parses every response.
    private sealed class StorageServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly Credentials credentials = new();
        public readonly ConcurrentDictionary<string, byte[]> Objects = new();
        public readonly ConcurrentQueue<(string Method, string Path)> Requests = new();
        public string Privacy = "NoPublicAccess", Tier = "Standard", Versioning = "Disabled";
        public bool Links, Retention, Lifecycle, BadChecksum, Denied, MissingBucket, ForbiddenOverwrite;
        public int DelayMilliseconds;
        public string Endpoint = "";
        private StorageServer(WebApplication app) { this.app = app; }
        public static async Task<StorageServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            var app = builder.Build(); var fixture = new StorageServer(app);
            app.Run(fixture.HandleAsync); await app.StartAsync(TestContext.Current.CancellationToken);
            fixture.Endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return fixture;
        }
        public OciChatAttachmentStore Store(ChatAttachmentOptions? settings = null) => new(Options.Create(settings ?? Settings()), ct =>
        {
            ct.ThrowIfCancellationRequested();
            var client = new ObjectStorageClient(credentials.Provider, OciChatAttachmentClients.Configuration(settings ?? Settings()), Endpoint);
            Assert.Equal(new Uri(Endpoint), client.GetEndpoint());
            return Task.FromResult(client);
        });
        private async Task HandleAsync(HttpContext context)
        {
            var path = Uri.UnescapeDataString(context.Request.Path.Value!);
            Requests.Enqueue((context.Request.Method, path));
            Assert.StartsWith("Signature ", context.Request.Headers.Authorization.ToString());
            Assert.StartsWith("/n/testns/b/chat-private", path);
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, context.RequestAborted);
            if (Denied) { await ErrorAsync(context, 403, "NotAuthorized"); return; }
            if (MissingBucket) { await ErrorAsync(context, 404, "BucketNotFound"); return; }
            if (path.EndsWith("/p")) { await context.Response.WriteAsJsonAsync(Links ? new[] { new { id = "link" } } : []); return; }
            if (path.EndsWith("/retentionRules"))
            { await context.Response.WriteAsJsonAsync(new { items = Retention ? new[] { new { id = "retention" } } : [] }); return; }
            var index = path.IndexOf("/o/", StringComparison.Ordinal);
            if (index < 0)
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    name = "chat-private", @namespace = "testns", publicAccessType = Privacy,
                    storageTier = Tier, versioning = Versioning, isReadOnly = false,
                    objectLifecyclePolicyEtag = Lifecycle ? "lifecycle" : null
                }); return;
            }
            var key = path[(index + 3)..];
            Assert.StartsWith("test-chat/", key);
            if (context.Request.Method == "PUT")
            {
                Assert.Equal("*", context.Request.Headers.IfNoneMatch);
                using var data = new MemoryStream(); await context.Request.Body.CopyToAsync(data, context.RequestAborted);
                var bytes = data.ToArray(); var checksum = Convert.ToBase64String(SHA256.HashData(bytes));
                Assert.Equal(checksum, context.Request.Headers["opc-content-sha256"]);
                if (!Objects.TryAdd(key, bytes))
                { await ErrorAsync(context, ForbiddenOverwrite ? 403 : 412, ForbiddenOverwrite ? "NotAuthorized" : "PreconditionFailed"); return; }
                context.Response.Headers["opc-content-sha256"] = BadChecksum ? "wrong" : checksum;
                context.Response.StatusCode = 200; return;
            }
            if (context.Request.Method == "DELETE")
            {
                if (!Objects.TryRemove(key, out _)) { await ErrorAsync(context, 404, "ObjectNotFound"); return; }
                context.Response.StatusCode = 204; return;
            }
            if (!Objects.TryGetValue(key, out var body)) { await ErrorAsync(context, 404, "ObjectNotFound"); return; }
            context.Response.ContentLength = body.Length;
            context.Response.Headers["opc-content-sha256"] = BadChecksum ? "wrong" : Convert.ToBase64String(SHA256.HashData(body));
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
        private static async Task ErrorAsync(HttpContext context, int status, string code)
        { context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(new { code, message = "Storage fixture error" }); }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); credentials.Dispose(); }
    }

    [Fact]
    public async Task Signed_sdk_requests_preserve_quarantine_promotion_and_cleanup_ownership()
    {
        await using var server = await StorageServer.StartAsync(); var store = server.Store();
        var ct = TestContext.Current.CancellationToken; var key = ChatAttachmentKeys.New(true);
        using var data = new MemoryStream("canonical image"u8.ToArray());
        await store.PutQuarantineAsync(key, data, ct);
        await Assert.ThrowsAsync<OciException>(() => store.OpenAsync(key, ct));
        await store.PromoteAsync(key, ct);
        server.ForbiddenOverwrite = true;
        await store.PromoteAsync(key, ct);
        await store.DeleteAsync(ChatAttachmentKeys.Quarantine(key), ct);
        using (var available = await store.OpenAsync(key, ct)) Assert.Equal(data.ToArray(), ((MemoryStream)available).ToArray());
        await store.DeleteAsync(key, ct); await store.DeleteAsync(key, ct);
        Assert.Empty(server.Objects);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("unknown")]
    [InlineData("archive")]
    [InlineData("versioning")]
    [InlineData("links")]
    [InlineData("retention")]
    [InlineData("lifecycle")]
    public async Task Unsafe_bucket_configuration_rejects_uploads_and_reads_but_allows_cleanup(string unsafeSetting)
    {
        await using var server = await StorageServer.StartAsync(); var store = server.Store();
        var key = ChatAttachmentKeys.New(true); var ct = TestContext.Current.CancellationToken;
        switch (unsafeSetting)
        {
            case "public": server.Privacy = "ObjectRead"; break;
            case "unknown": server.Privacy = "future-value"; break;
            case "archive": server.Tier = "Archive"; break;
            case "versioning": server.Versioning = "Enabled"; break;
            case "links": server.Links = true; break;
            case "retention": server.Retention = true; break;
            case "lifecycle": server.Lifecycle = true; break;
        }
        using var input = new MemoryStream("image"u8.ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => store.PutQuarantineAsync(key, input, ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.OpenAsync(key, ct));
        Assert.DoesNotContain(server.Requests, request => request.Method == "PUT" || request.Path.Contains("/o/"));
        server.Objects["test-chat/" + key] = input.ToArray();
        await store.DeleteAsync(key, ct); Assert.Empty(server.Objects);
    }

    [Fact]
    public async Task Promotion_retry_rejects_different_existing_bytes_without_overwriting()
    {
        await using var server = await StorageServer.StartAsync(); var store = server.Store();
        var key = ChatAttachmentKeys.New(true); var ct = TestContext.Current.CancellationToken;
        using var input = new MemoryStream("clean"u8.ToArray()); await store.PutQuarantineAsync(key, input, ct);
        server.Objects["test-chat/" + key] = "different"u8.ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PromoteAsync(key, ct));
        Assert.Equal("different"u8.ToArray(), server.Objects["test-chat/" + key]);
    }

    [Fact]
    public async Task Oversize_and_checksum_failures_never_return_an_object_stream()
    {
        await using var server = await StorageServer.StartAsync(); var store = server.Store();
        var key = ChatAttachmentKeys.New(true); var ct = TestContext.Current.CancellationToken;
        using var large = new MemoryStream(new byte[2049]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PutQuarantineAsync(key, large, ct)); Assert.Empty(server.Requests);
        server.Objects["test-chat/" + key] = new byte[2049];
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenAsync(key, ct));
        server.Objects["test-chat/" + key] = "image"u8.ToArray(); server.BadChecksum = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenAsync(key, ct));
        using var input = new MemoryStream("image"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PutQuarantineAsync(key, input, ct));
    }

    [Fact]
    public async Task Permission_errors_are_not_retried_or_mistaken_for_successful_cleanup()
    {
        await using var server = await StorageServer.StartAsync(); server.Denied = true;
        var store = server.Store(); var ct = TestContext.Current.CancellationToken; var key = ChatAttachmentKeys.New(true);
        await Assert.ThrowsAsync<OciException>(() => store.DeleteAsync(key, ct)); Assert.Single(server.Requests);
        server.Requests.Clear(); using var input = new MemoryStream("image"u8.ToArray());
        await Assert.ThrowsAsync<OciException>(() => store.PutQuarantineAsync(key, input, ct)); Assert.Single(server.Requests);
        server.Denied = false; server.MissingBucket = true;
        await Assert.ThrowsAsync<OciException>(() => store.DeleteAsync(key, ct));
    }

    [Fact]
    public async Task Invalid_keys_and_pre_cancelled_operations_issue_no_requests()
    {
        await using var server = await StorageServer.StartAsync(); var store = server.Store();
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("../other-prefix", TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var input = new MemoryStream("image"u8.ToArray());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutQuarantineAsync(ChatAttachmentKeys.New(false), input, cancelled.Token));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Slow_storage_is_cancelled_within_the_operation_deadline()
    {
        await using var server = await StorageServer.StartAsync(); server.DelayMilliseconds = 10000;
        var settings = Settings(); settings.IoTimeoutSeconds = 1;
        var operation = server.Store(settings).OpenAsync(ChatAttachmentKeys.New(true), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<Exception>(() => operation.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken));
        Assert.True(operation.IsCompleted); Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Blocked_credential_acquisition_is_single_flight_and_cancellation_starts_no_storage_request()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var calls = 0;
        using var credentials = new Credentials();
        var clients = new OciChatAttachmentClients(Options.Create(Settings()), () =>
        {
            Interlocked.Increment(ref calls); entered.SetResult(); release.Wait(); return credentials.Provider;
        });
        try
        {
            using var first = new CancellationTokenSource(); var a = clients.CreateAsync(first.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var second = new CancellationTokenSource(); var b = clients.CreateAsync(second.Token);
            first.Cancel(); second.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
            Assert.Equal(1, calls);
        }
        finally { release.Set(); }
        using var client = await clients.CreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }
}
