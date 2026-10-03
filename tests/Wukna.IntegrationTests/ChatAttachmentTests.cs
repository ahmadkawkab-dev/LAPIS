namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wukna.Features.Chat;
using Wukna.Shared.Data.AppDbContext;
using Xunit;
using static ChatControlTestSupport;

public sealed class ChatAttachmentTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed class Scanner : IAttachmentScanner
    {
        public AttachmentScanVerdict Verdict { get; set; }
        public bool Outage { get; set; }
        public int Calls { get; private set; }
        public Func<Task>? BeforeScan { get; set; }
        public async Task<AttachmentScanVerdict> ScanAsync(Stream content, CancellationToken ct)
        {
            Calls++; if (BeforeScan is { } before) await before();
            if (Outage) throw new IOException("scanner_offline");
            await content.CopyToAsync(Stream.Null, ct); return Verdict;
        }
    }
    private sealed class Factory : WuknaWebApplicationFactory
    {
        private readonly PostgresFixture postgres;
        public Factory(PostgresFixture postgres, ManualTimeProvider clock) : base(postgres, clock) { this.postgres = postgres; }
        public string Root { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wukna-attachment-tests-" + Guid.NewGuid().ToString("N"));
        public Scanner Scanner { get; } = new();
        public Action<ChatAttachmentOptions>? Configure { get; set; }
        public IChatAttachmentStore? Store { get; set; }
        public bool RealScanner { get; set; }
        public bool PreserveFiles { get; set; }
        public Func<IChatAttachmentStore, IChatAttachmentStore>? DecorateStore { get; set; }
        public IInterceptor? Interceptor { get; set; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Chat:Attachments:Enabled", "true"); builder.UseSetting("Chat:Attachments:WorkerEnabled", "false");
            builder.UseSetting("Chat:Attachments:Directory", Root);
            builder.ConfigureTestServices(services => {
                if (!RealScanner) { services.RemoveAll<IAttachmentScanner>(); services.AddSingleton<IAttachmentScanner>(Scanner); }
                if (Configure is { } configure) services.PostConfigure(configure);
                if (Store is { } store) { services.RemoveAll<IChatAttachmentStore>(); services.AddSingleton(store); }
                if (DecorateStore is { } decorate)
                {
                    services.RemoveAll<IChatAttachmentStore>();
                    services.AddSingleton(provider => decorate(new LocalChatAttachmentStore(provider.GetRequiredService<IOptions<ChatAttachmentOptions>>(),
                        provider.GetRequiredService<IWebHostEnvironment>(), provider.GetRequiredService<IConfiguration>())));
                }
                if (Interceptor is { } interceptor)
                {
                    services.RemoveAll<WuknaDbContext>(); services.RemoveAll<DbContextOptions<WuknaDbContext>>();
                    services.AddDbContext<WuknaDbContext>(options => options.UseNpgsql(postgres.ConnectionString)
                        .UseSnakeCaseNamingConvention().AddInterceptors(interceptor));
                }
            });
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync(); if (!PreserveFiles && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private static async Task<byte[]> ImageBytes()
    {
        using var image = new Image<Rgba32>(36, 20, Color.CornflowerBlue);
        using var output = new MemoryStream(); await image.SaveAsync(output, new PngEncoder(), Ct); return output.ToArray();
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient http, SeedData seed, Guid? operation = null,
        byte[]? bytes = null, string name = "photo.png", string type = "image/png", bool chunked = false)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent((operation ?? Guid.NewGuid()).ToString()), "clientMessageId");
        form.Add(new StringContent(" A caption\r\nSecond line "), "body");
        var content = new ByteArrayContent(bytes ?? await ImageBytes()); content.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(content, "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(seed) + "/attachments") { Content = form };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        return await http.SendAsync(request, Ct);
    }
    private static async Task<ChatSendResultDto> Sent(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return Assert.IsType<ChatSendResultDto>(await response.Content.ReadFromJsonAsync<ChatSendResultDto>(Ct));
    }
    private static ChatAttachmentJobs Jobs(Factory factory) => factory.Services.GetRequiredService<ChatAttachmentJobs>();
    private async Task<ChatAttachment> Claim(Factory factory)
    {
        await using var db = postgres.CreateContext(); return Assert.IsType<ChatAttachment>(await Jobs(factory).ClaimScanAsync(db, Ct));
    }
    private async Task DrainCleanup(Factory factory)
    {
        for (var count = 0; count < 20; count++)
        {
            await using var db = postgres.CreateContext(); var claim = await Jobs(factory).ClaimCleanupAsync(db, Ct);
            if (claim is null) return; Assert.True(await Jobs(factory).CleanupAsync(claim, Ct));
        }
        Assert.Fail("Cleanup did not drain in its bounded test window.");
    }

    [Fact]
    public async Task Upload_is_pending_private_idempotent_and_normalized_then_clean_scan_enables_authorized_downloads()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest); using var owner = Client(factory, seed.Owner); using var outsider = Client(factory, seed.Outsider);
        var operation = Guid.NewGuid(); var bytes = await ImageBytes();
        var sent = await Sent(await Upload(guest, seed, operation, bytes, "C:\\temp\\photo.png"));
        Assert.Equal("attachment", sent.Message.Type); Assert.Equal("A caption\nSecond line", sent.Message.Body);
        var attachment = Assert.IsType<ChatAttachmentDto>(sent.Message.Attachment);
        Assert.Equal("photo.png", attachment.FileName); Assert.Equal("image/webp", attachment.ContentType); Assert.Equal("Pending", attachment.ScanStatus);
        var url = Path(seed) + $"/attachments/{attachment.Id}";
        Assert.Equal(HttpStatusCode.Conflict, (await guest.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/boards/{Guid.NewGuid()}/chat/attachments/{attachment.Id}", Ct)).StatusCode);
        var response = await Upload(guest, seed, operation, bytes, "photo.png"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var replay = Assert.IsType<ChatSendResultDto>(await response.Content.ReadFromJsonAsync<ChatSendResultDto>(Ct));
        Assert.True(replay.IsReplay); Assert.Equal(sent.Message.Id, replay.Message.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(guest, seed, operation, bytes, "renamed.png")).StatusCode);
        await using (var db = postgres.CreateContext())
        {
            Assert.Equal(1, await db.ChatMessages.CountAsync(Ct)); Assert.False(await db.ChatBlobWork.AnyAsync(Ct));
            Assert.Equal(seed.Board.UpdatedAt, (await db.Boards.SingleAsync(Ct)).UpdatedAt);
            var stored = await db.ChatAttachments.SingleAsync(Ct);
            Assert.False(File.Exists(System.IO.Path.Combine(factory.Root, stored.StorageKey[..1], stored.StorageKey[2..])));
            var wire = await (await guest.GetAsync(Path(seed) + "/messages", Ct)).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(stored.StorageKey, wire); Assert.DoesNotContain(factory.Root, wire);
        }
        var claim = await Claim(factory); Assert.True(await Jobs(factory).ScanAsync(claim, Ct)); Assert.Equal(3, factory.Scanner.Calls);
        await DrainCleanup(factory);
        var download = await owner.GetAsync(url, Ct); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("photo.webp", download.Content.Headers.ContentDisposition?.FileNameStar);
        using var normalized = Image.Load(await download.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(36, normalized.Width); Assert.Equal(20, normalized.Height); Assert.Null(normalized.Metadata.ExifProfile);
        var preview = await guest.GetAsync(url + "?preview=true", Ct); Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, seed, seed.Guest, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync(url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(guest, seed)).StatusCode);
    }

    [Theory]
    [InlineData("photo.exe", "image/png", false, 415)]
    [InlineData("photo.svg", "image/svg+xml", false, 415)]
    [InlineData("photo.jpg", "image/jpeg", false, 415)]
    [InlineData("photo.png", "image/png", true, 400)]
    public async Task Unsafe_extensions_mime_spoofs_and_malformed_images_are_rejected(string name, string type, bool malformed, int status)
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock); using var guest = Client(factory, seed.Guest);
        var response = await Upload(guest, seed, bytes: malformed ? "not an image"u8.ToArray() : await ImageBytes(), name: name, type: type);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await DrainCleanup(factory);
        await using var db = postgres.CreateContext(); Assert.False(await db.ChatAttachments.AnyAsync(Ct)); Assert.False(await db.ChatMessages.AnyAsync(Ct));
        Assert.Empty(Directory.Exists(factory.Root) ? Directory.GetFiles(factory.Root, "*", SearchOption.AllDirectories) : []);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Oversized_uploads_are_bounded_with_or_without_content_length(bool chunked)
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock) { Configure = options => options.MaxFileBytes = 1024 };
        using var guest = Client(factory, seed.Guest);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(guest, seed, bytes: new byte[2048], chunked: chunked)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(guest, seed, bytes: new byte[80 * 1024], chunked: chunked)).StatusCode);
        await using var db = postgres.CreateContext(); Assert.False(await db.ChatBlobWork.AnyAsync(Ct));
    }

    [Fact]
    public async Task Mute_and_slow_mode_apply_to_attachments_and_share_a_send_slot_with_text()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        var state = await State(guest, seed, Ct);
        Assert.Equal(HttpStatusCode.OK, (await Mute(owner, seed, seed.Guest, state, true, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(guest, seed)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Mute(owner, seed, seed.Guest, await State(guest, seed, Ct), false, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Settings(owner, seed, 60, (await State(owner, seed, Ct)).SettingsRevision, Ct)).StatusCode);
        var responses = await Task.WhenAll(Upload(guest, seed), Send(guest, seed, Ct));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Upload(guest, seed)).StatusCode);
        await Sent(await Upload(owner, seed)); await Sent(await Upload(owner, seed));
    }

    [Fact]
    public async Task Scanner_outage_retries_from_postgres_and_infected_attachments_never_download()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock);
        using var guest = Client(factory, seed.Guest); var sent = await Sent(await Upload(guest, seed));
        var url = Path(seed) + $"/attachments/{sent.Message.Attachment!.Id}";
        factory.Scanner.Outage = true; var first = await Claim(factory); Assert.False(await Jobs(factory).ScanAsync(first, Ct));
        await using (var db = postgres.CreateContext())
        {
            var failed = await db.ChatAttachments.SingleAsync(Ct); Assert.Equal(ChatAttachmentScanStatus.ScanFailed, failed.ScanStatus);
            Assert.Null(failed.ScanLeaseToken); Assert.True(failed.NextScanAttemptAt > seed.Clock.GetUtcNow());
            Assert.Null(await Jobs(factory).ClaimScanAsync(db, Ct));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await guest.GetAsync(url, Ct)).StatusCode);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); factory.Scanner.Outage = false; factory.Scanner.Verdict = AttachmentScanVerdict.Infected;
        // A fresh service/scope can resume durable work; no in-memory queue carries the claim.
        var second = await Claim(factory); Assert.Equal(2, second.ScanAttempts); Assert.True(await Jobs(factory).ScanAsync(second, Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await guest.GetAsync(url, Ct)).StatusCode);
        await DrainCleanup(factory);
        await using var result = postgres.CreateContext(); Assert.Equal(ChatAttachmentScanStatus.Rejected, (await result.ChatAttachments.SingleAsync(Ct)).ScanStatus);
        Assert.Empty(Directory.GetFiles(factory.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Expired_scan_and_cleanup_claims_cannot_finalize_after_reclaim()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock); using var guest = Client(factory, seed.Guest);
        await Sent(await Upload(guest, seed)); var old = await Claim(factory);
        seed.Clock.Advance(TimeSpan.FromSeconds(181)); var current = await Claim(factory);
        Assert.NotEqual(old.ScanLeaseToken, current.ScanLeaseToken);
        Assert.False(await Jobs(factory).ScanAsync(old, Ct)); Assert.True(await Jobs(factory).ScanAsync(current, Ct));
        await using var db = postgres.CreateContext(); var oldCleanup = Assert.IsType<ChatBlobWork>(await Jobs(factory).ClaimCleanupAsync(db, Ct));
        seed.Clock.Advance(TimeSpan.FromSeconds(181)); var currentCleanup = Assert.IsType<ChatBlobWork>(await Jobs(factory).ClaimCleanupAsync(db, Ct));
        Assert.False(await Jobs(factory).CleanupAsync(oldCleanup, Ct)); Assert.True(await Jobs(factory).CleanupAsync(currentCleanup, Ct));
        Assert.Equal(ChatAttachmentScanStatus.Available, (await db.ChatAttachments.AsNoTracking().SingleAsync(Ct)).ScanStatus);
    }

    [Fact]
    public async Task Board_deletion_durably_cleans_available_and_pending_blobs_after_metadata_cascades()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock); using var owner = Client(factory, seed.Owner);
        await Sent(await Upload(owner, seed)); Assert.True(await Jobs(factory).ScanAsync(await Claim(factory), Ct));
        await Sent(await Upload(owner, seed)); var pending = await Claim(factory);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/boards/{seed.Board.Id}", Ct)).StatusCode);
        Assert.False(await Jobs(factory).ScanAsync(pending, Ct));
        await using (var db = postgres.CreateContext())
        {
            Assert.False(await db.ChatAttachments.AnyAsync(Ct)); Assert.False(await db.ChatMessages.AnyAsync(Ct)); Assert.True(await db.ChatBlobWork.AnyAsync(Ct));
        }
        await DrainCleanup(factory); Assert.Empty(Directory.GetFiles(factory.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_backlog_and_quota_reservations_cannot_exceed_limits()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock) {
            Configure = options => { options.MaxPendingPerBoard = 1; options.MaxPendingPerMember = 1; }
        };
        using var guest = Client(factory, seed.Guest); using var peer = Client(factory, seed.Peer);
        var responses = await Task.WhenAll(Upload(guest, seed), Upload(peer, seed));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        await using var db = postgres.CreateContext(); Assert.Equal(1, await db.ChatAttachments.CountAsync(Ct)); Assert.False(await db.ChatBlobWork.AnyAsync(Ct));
    }

    [Fact]
    public async Task Quota_includes_original_and_promotion_copies_and_upload_rate_is_separate()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock) {
            Configure = options => { options.BoardQuotaBytes = 1024; options.MemberQuotaBytes = 1024; options.UploadsPerMinute = 1; }
        };
        using var guest = Client(factory, seed.Guest);
        var quota = await Upload(guest, seed); Assert.Equal(HttpStatusCode.TooManyRequests, quota.StatusCode);
        Assert.Contains("chat_attachment_quota", await quota.Content.ReadAsStringAsync(Ct));
        var rate = await Upload(guest, seed); Assert.Equal(HttpStatusCode.TooManyRequests, rate.StatusCode);
        Assert.Contains("chat_upload_rate_limited", await rate.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Created, (await Send(guest, seed, Ct)).StatusCode);
    }

    [Fact]
    public async Task Scan_updates_publish_references_only_to_current_members()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner); using var guest = Client(factory, seed.Guest);
        await using var ownerHub = Connection(factory, seed.Owner); await using var guestHub = Connection(factory, seed.Guest);
        var ownerEvents = Listen<ChatAttachmentChangedEvent>(ownerHub, ChatRealtimeEvents.AttachmentChanged);
        var guestEvents = Listen<ChatAttachmentChangedEvent>(guestHub, ChatRealtimeEvents.AttachmentChanged);
        await Join(ownerHub, seed, Ct); await Join(guestHub, seed, Ct);
        var sent = await Sent(await Upload(owner, seed)); var claim = await Claim(factory);
        await Dispatch(factory, Ct); var observed = await Read(ownerEvents, Ct); await Read(guestEvents, Ct);
        Assert.Equal(sent.Message.Id, observed.MessageId); Assert.Equal(sent.Message.Attachment!.Id, observed.AttachmentId);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, seed, seed.Guest, Ct)).StatusCode);
        Assert.True(await Jobs(factory).ScanAsync(claim, Ct)); await Dispatch(factory, Ct);
        Assert.Equal(sent.Message.Attachment.Id, (await Read(ownerEvents, Ct)).AttachmentId); await None(guestEvents, Ct);
    }

    private sealed class FaultStore(IChatAttachmentStore inner) : IChatAttachmentStore
    {
        public bool FailSecondWrite { get; set; }
        public bool FailSecondPromotion { get; set; }
        private int writes, promotions;
        public async Task PutQuarantineAsync(string key, Stream input, CancellationToken ct)
        {
            await inner.PutQuarantineAsync(key, input, ct);
            if (++writes == 2 && FailSecondWrite) throw new IOException("upload_interrupted");
        }
        public Task<Stream> OpenAsync(string key, CancellationToken ct) => inner.OpenAsync(key, ct);
        public async Task PromoteAsync(string key, CancellationToken ct)
        {
            await inner.PromoteAsync(key, ct);
            if (++promotions == 2 && FailSecondPromotion) throw new IOException("promotion_interrupted");
        }
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
    }

    [Fact]
    public async Task Failed_upload_leaves_durable_cleanup_and_does_not_consume_a_message_or_cooldown()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock) {
            DecorateStore = store => new FaultStore(store) { FailSecondWrite = true }
        };
        using var guest = Client(factory, seed.Guest); var operation = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Upload(guest, seed, operation)).StatusCode);
        await using (var db = postgres.CreateContext())
        {
            Assert.False(await db.ChatMessages.AnyAsync(Ct)); Assert.False(await db.ChatAttachments.AnyAsync(Ct));
            var work = await db.ChatBlobWork.SingleAsync(Ct); Assert.Null(work.LeaseToken); Assert.Equal(operation, work.ClientMessageId);
            Assert.True(work.ReservedBytes > 0); Assert.Null((await db.BoardMemberChatStates.SingleAsync(Ct)).NextSendAllowedAt);
        }
        await DrainCleanup(factory); Assert.Empty(Directory.GetFiles(factory.Root, "*", SearchOption.AllDirectories));
        await Sent(await Upload(guest, seed, operation));
    }

    [Fact]
    public async Task Interrupted_promotion_fails_closed_and_a_restarted_app_recovers_from_durable_state()
    {
        var seed = await Seed(postgres, Ct); var first = new Factory(postgres, seed.Clock) {
            PreserveFiles = true, DecorateStore = store => new FaultStore(store) { FailSecondPromotion = true }
        };
        Guid attachmentId;
        try
        {
            using var guest = Client(first, seed.Guest); attachmentId = (await Sent(await Upload(guest, seed))).Message.Attachment!.Id;
            Assert.False(await Jobs(first).ScanAsync(await Claim(first), Ct));
            Assert.Equal(HttpStatusCode.Conflict, (await guest.GetAsync(Path(seed) + $"/attachments/{attachmentId}", Ct)).StatusCode);
        }
        finally { await first.DisposeAsync(); }
        seed.Clock.Advance(TimeSpan.FromSeconds(3));
        await using var restarted = new Factory(postgres, seed.Clock) { Root = first.Root };
        using var owner = Client(restarted, seed.Owner);
        var recovered = await Claim(restarted); Assert.Equal(2, recovered.ScanAttempts);
        Assert.True(await Jobs(restarted).ScanAsync(recovered, Ct)); await DrainCleanup(restarted);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Path(seed) + $"/attachments/{attachmentId}", Ct)).StatusCode);
    }

    private sealed class ReservationGate : DbTransactionInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int entered;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (data.Context?.ChangeTracker.Entries<ChatBlobWork>().Any(entry => entry.Entity.Purpose == ChatBlobWorkPurpose.UploadReservation) != true ||
                Interlocked.CompareExchange(ref entered, 1, 0) != 0) return;
            Started.TrySetResult(); await Release.Task.WaitAsync(ct);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Membership_removal_or_mute_after_reservation_is_rechecked_before_any_private_store_write(bool mute)
    {
        var seed = await Seed(postgres, Ct); var gate = new ReservationGate();
        await using var factory = new Factory(postgres, seed.Clock) { Interceptor = gate };
        using var guest = Client(factory, seed.Guest); using var owner = Client(factory, seed.Owner);
        var upload = Upload(guest, seed);
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            if (mute) Assert.Equal(HttpStatusCode.OK, (await Mute(owner, seed, seed.Guest, await State(guest, seed, Ct), true, Ct)).StatusCode);
            else Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, seed, seed.Guest, Ct)).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(mute ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound, (await upload).StatusCode);
        await DrainCleanup(factory);
        await using var db = postgres.CreateContext(); Assert.False(await db.ChatMessages.AnyAsync(Ct));
        Assert.Empty(Directory.GetFiles(factory.Root, "*", SearchOption.AllDirectories));
    }

    private sealed class PromotionGate(IChatAttachmentStore inner) : IChatAttachmentStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int promotions;
        public Task PutQuarantineAsync(string key, Stream content, CancellationToken ct) => inner.PutQuarantineAsync(key, content, ct);
        public Task<Stream> OpenAsync(string key, CancellationToken ct) => inner.OpenAsync(key, ct);
        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);
        public async Task PromoteAsync(string key, CancellationToken ct)
        {
            await inner.PromoteAsync(key, ct);
            if (Interlocked.Increment(ref promotions) != 1) return;
            Started.TrySetResult(); await Release.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task Board_deletion_waits_for_promotion_and_then_cleans_every_copy_without_orphans()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock) { DecorateStore = store => new PromotionGate(store) };
        using var owner = Client(factory, seed.Owner); var gate = Assert.IsType<PromotionGate>(factory.Services.GetRequiredService<IChatAttachmentStore>());
        await Sent(await Upload(owner, seed)); var scanning = Jobs(factory).ScanAsync(await Claim(factory), Ct);
        Task<HttpResponseMessage>? deletion = null;
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            deletion = owner.DeleteAsync($"/api/boards/{seed.Board.Id}", Ct);
            await Task.Delay(150, Ct); Assert.False(deletion.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.True(await scanning);
        Assert.NotNull(deletion);
        Assert.Equal(HttpStatusCode.NoContent, (await deletion).StatusCode);
        await DrainCleanup(factory); Assert.Empty(Directory.GetFiles(factory.Root, "*", SearchOption.AllDirectories));
        await using var db = postgres.CreateContext(); Assert.False(await db.ChatAttachments.AnyAsync(Ct)); Assert.False(await db.ChatBlobWork.AnyAsync(Ct));
    }

    [Fact]
    public async Task Cross_board_attachment_ids_and_image_dimension_bombs_are_rejected()
    {
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock); using var guest = Client(factory, seed.Guest);
        var sent = await Sent(await Upload(guest, seed));
        var other = new Wukna.Features.Board.Board { Title = "Other board" };
        other.Memberships.Add(new() { UserId = seed.Guest.Id, Role = Wukna.Features.Board.BoardRole.Owner, CanEdit = true });
        await using (var db = postgres.CreateContext()) { db.Boards.Add(other); await db.SaveChangesAsync(Ct); }
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/boards/{other.Id}/chat/attachments/{sent.Message.Attachment!.Id}", Ct)).StatusCode);
        using var oversized = new Image<Rgba32>(4097, 1); using var encoded = new MemoryStream(); await oversized.SaveAsync(encoded, new PngEncoder(), Ct);
        var response = await Upload(guest, seed, bytes: encoded.ToArray()); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("chat_image_dimensions", await response.Content.ReadAsStringAsync(Ct));
        await DrainCleanup(factory); await using var result = postgres.CreateContext(); Assert.Equal(1, await result.ChatAttachments.CountAsync(Ct));
    }

    [Fact(Explicit = true)]
    public async Task Real_clamd_scans_originals_detects_eicar_and_recovers_after_socket_outage()
    {
        // Run explicitly against the documented disposable official daemon on localhost.
        var seed = await Seed(postgres, Ct); await using var factory = new Factory(postgres, seed.Clock) {
            RealScanner = true, Configure = options => options.ClamPort = 53310
        };
        using var owner = Client(factory, seed.Owner);
        var clean = await Sent(await Upload(owner, seed)); Assert.True(await Jobs(factory).ScanAsync(await Claim(factory), Ct));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Path(seed) + $"/attachments/{clean.Message.Attachment!.Id}", Ct)).StatusCode);
        var eicar = System.Text.Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}" + "$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        await using (var eicarStream = new MemoryStream(eicar, false))
            Assert.Equal(AttachmentScanVerdict.Infected, await factory.Services.GetRequiredService<IAttachmentScanner>().ScanAsync(eicarStream, Ct));
        var unsafeImage = await Sent(await Upload(owner, seed));
        // Exercise the worker with a standard EICAR original in its private fixture.
        // An EICAR string appended to PNG is not the standard test-file form and
        // clamd may skip it. Actual HTTP uploads still require valid image content.
        await using (var db = postgres.CreateContext())
        {
            var quarantined = await db.ChatAttachments.SingleAsync(attachment => attachment.Id == unsafeImage.Message.Attachment!.Id, Ct);
            await File.WriteAllBytesAsync(System.IO.Path.Combine(factory.Root, quarantined.OriginalStorageKey[..1], quarantined.OriginalStorageKey[2..]), eicar, Ct);
            quarantined.InputByteSize = eicar.Length; await db.SaveChangesAsync(Ct);
        }
        Assert.True(await Jobs(factory).ScanAsync(await Claim(factory), Ct));
        await using (var db = postgres.CreateContext()) Assert.Equal(ChatAttachmentScanStatus.Rejected,
            (await db.ChatAttachments.SingleAsync(attachment => attachment.Id == unsafeImage.Message.Attachment!.Id, Ct)).ScanStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync(Path(seed) + $"/attachments/{unsafeImage.Message.Attachment!.Id}", Ct)).StatusCode);
        var retry = await Sent(await Upload(owner, seed));
        var configured = factory.Services.GetRequiredService<IOptions<ChatAttachmentOptions>>().Value;
        configured.ClamPort = 53311;
        Assert.False(await Jobs(factory).ScanAsync(await Claim(factory), Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await owner.GetAsync(Path(seed) + $"/attachments/{retry.Message.Attachment!.Id}", Ct)).StatusCode);
        seed.Clock.Advance(TimeSpan.FromSeconds(3)); configured.ClamPort = 53310;
        Assert.True(await Jobs(factory).ScanAsync(await Claim(factory), Ct));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Path(seed) + $"/attachments/{retry.Message.Attachment.Id}", Ct)).StatusCode);
        await DrainCleanup(factory);
    }
}
