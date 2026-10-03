namespace Wukna.Features.Chat;

using System.Security.Cryptography;
using System.Net;
using Microsoft.Extensions.Options;
using Oci.Common.Model;
using Oci.Common.Retry;
using Oci.ObjectstorageService;
using Oci.ObjectstorageService.Models;
using Oci.ObjectstorageService.Requests;

// Small, bounded images use single-part uploads. No public URLs, multipart leftovers,
// server-side asynchronous copy jobs, or SDK retries outside PostgreSQL's durable jobs.
public sealed class OciChatAttachmentStore : IChatAttachmentStore
{
    private readonly ChatAttachmentOptions options;
    private readonly Func<CancellationToken, Task<ObjectStorageClient>> createClient;
    private static RetryConfiguration NoRetries => new() { MaxAttempts = 1 };

    public OciChatAttachmentStore(IOptions<ChatAttachmentOptions> options, OciChatAttachmentClients clients)
        : this(options, clients.CreateAsync) { }

    // An explicit client factory also permits signed SDK requests against a disposable test server.
    public OciChatAttachmentStore(IOptions<ChatAttachmentOptions> options,
        Func<CancellationToken, Task<ObjectStorageClient>> createClient)
    {
        this.options = options.Value;
        if (!this.options.Oci.IsValid()) throw new InvalidOperationException("Private OCI attachment configuration is invalid.");
        this.createClient = createClient;
    }

    private string ObjectName(string key) { ChatAttachmentKeys.Validate(key); return options.Oci.Prefix + "/" + key; }
    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.IoTimeoutSeconds));
        return deadline;
    }

    private async Task RequirePrivateBucketAsync(ObjectStorageClient client, CancellationToken ct)
    {
        var response = await client.GetBucket(new GetBucketRequest
        {
            NamespaceName = options.Oci.Namespace, BucketName = options.Oci.Bucket
        }, NoRetries, ct);
        using (response.httpResponseMessage)
        {
            var bucket = response.Bucket;
            if (bucket.PublicAccessType != Bucket.PublicAccessTypeEnum.NoPublicAccess ||
                bucket.StorageTier != Bucket.StorageTierEnum.Standard ||
                bucket.Versioning != Bucket.VersioningEnum.Disabled || bucket.IsReadOnly == true ||
                !string.IsNullOrEmpty(bucket.ObjectLifecyclePolicyEtag))
                throw new InvalidOperationException("Chat storage requires a private, writable Standard bucket without versioning or lifecycle rules.");
        }
        var links = await client.ListPreauthenticatedRequests(new ListPreauthenticatedRequestsRequest
        {
            NamespaceName = options.Oci.Namespace, BucketName = options.Oci.Bucket, Limit = 1
        }, NoRetries, ct);
        using (links.httpResponseMessage)
        {
            if (links.Items.Count != 0 || !string.IsNullOrEmpty(links.OpcNextPage))
                throw new InvalidOperationException("Chat storage must not have preauthenticated access links.");
        }
        var retention = await client.ListRetentionRules(new ListRetentionRulesRequest
        {
            NamespaceName = options.Oci.Namespace, BucketName = options.Oci.Bucket
        }, NoRetries, ct);
        using (retention.httpResponseMessage)
        {
            if (retention.RetentionRuleCollection.Items.Count != 0 || !string.IsNullOrEmpty(retention.OpcNextPage))
                throw new InvalidOperationException("Chat storage must not have retention rules that prevent cleanup.");
        }
    }

    public async Task PutQuarantineAsync(string key, Stream content, CancellationToken ct)
    {
        var name = ObjectName(ChatAttachmentKeys.Quarantine(key));
        using var deadline = Deadline(ct);
        using var buffered = await BufferAsync(content, null, deadline.Token);
        using var client = await createClient(deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        await RequirePrivateBucketAsync(client, deadline.Token);
        await PutAsync(client, name, buffered, deadline.Token);
    }

    private async Task PutAsync(ObjectStorageClient client, string name, MemoryStream content, CancellationToken ct)
    {
        content.Position = 0;
        var checksum = Convert.ToBase64String(SHA256.HashData(content.GetBuffer().AsSpan(0, checked((int)content.Length))));
        var response = await client.PutObject(new PutObjectRequest
        {
            NamespaceName = options.Oci.Namespace, BucketName = options.Oci.Bucket, ObjectName = name,
            PutObjectBody = content, ContentLength = content.Length, ContentType = "application/octet-stream",
            IfNoneMatch = "*", OpcChecksumAlgorithm = ChecksumAlgorithm.Sha256, OpcContentSha256 = checksum
        }, NoRetries, ct);
        using (response.httpResponseMessage)
        {
            if (response.OpcContentSha256 != checksum) throw new InvalidDataException("OCI upload checksum was not confirmed.");
        }
    }

    public async Task<Stream> OpenAsync(string key, CancellationToken ct)
    {
        var name = ObjectName(key);
        using var deadline = Deadline(ct);
        using var client = await createClient(deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        await RequirePrivateBucketAsync(client, deadline.Token);
        return await GetAsync(client, name, deadline.Token);
    }

    private async Task<MemoryStream> GetAsync(ObjectStorageClient client, string name, CancellationToken ct)
    {
        var response = await client.GetObject(new GetObjectRequest
        {
            NamespaceName = options.Oci.Namespace, BucketName = options.Oci.Bucket, ObjectName = name
        }, NoRetries, ct, HttpCompletionOption.ResponseHeadersRead);
        using (response.httpResponseMessage)
        await using (response.InputStream)
        {
            if (response.ContentLength is not > 0) throw new InvalidDataException("OCI object has no valid length.");
            var content = await BufferAsync(response.InputStream, response.ContentLength, ct);
            var checksum = Convert.ToBase64String(SHA256.HashData(content.GetBuffer().AsSpan(0, checked((int)content.Length))));
            if (checksum != response.OpcContentSha256)
            {
                content.Dispose();
                throw new InvalidDataException("OCI object checksum did not match.");
            }
            return content;
        }
    }

    private async Task<MemoryStream> BufferAsync(Stream input, long? expected, CancellationToken ct)
    {
        var max = Math.Max(options.MaxPreviewBytes, Math.Max(options.MaxFileBytes, options.MaxImageBytes));
        if (expected > max) throw new InvalidDataException("OCI object exceeds the attachment limit.");
        var output = new MemoryStream();
        try
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + read > max) throw new InvalidDataException("OCI object exceeds the attachment limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (output.Length == 0 || expected.HasValue && output.Length != expected.Value)
                throw new InvalidDataException("OCI object length did not match.");
            output.Position = 0;
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public async Task PromoteAsync(string key, CancellationToken ct)
    {
        var name = ObjectName(key);
        if (key[0] != 'a') throw new ArgumentException("Only normalized images can be promoted.", nameof(key));
        using var deadline = Deadline(ct);
        using var client = await createClient(deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        await RequirePrivateBucketAsync(client, deadline.Token);
        using var source = await GetAsync(client, ObjectName(ChatAttachmentKeys.Quarantine(key)), deadline.Token);
        try { await PutAsync(client, name, source, deadline.Token); }
        catch (OciException failure) when (failure.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Forbidden)
        {
            // A previous fenced attempt may have committed its PUT before its DB transaction
            // failed. OCI can reject an existing target for missing OBJECT_OVERWRITE before
            // evaluating If-None-Match, so reconcile both 412 and 403 by reading the target.
            using var existing = await GetAsync(client, name, deadline.Token);
            if (!source.GetBuffer().AsSpan(0, (int)source.Length).SequenceEqual(existing.GetBuffer().AsSpan(0, (int)existing.Length)))
                throw new InvalidDataException("OCI promotion target differs from its quarantine source.");
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (key.Length == 0) return;
        var name = ObjectName(key);
        using var deadline = Deadline(ct);
        using var client = await createClient(deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        // Cleanup must still remove exposed objects if an administrator makes the bucket public.
        await DeleteObjectAsync(client, name, deadline.Token);
        if (key[0] == 'a') await DeleteObjectAsync(client, ObjectName(ChatAttachmentKeys.Quarantine(key)), deadline.Token);
    }

    private async Task DeleteObjectAsync(ObjectStorageClient client, string name, CancellationToken ct)
    {
        try
        {
            var response = await client.DeleteObject(new DeleteObjectRequest
            {
                NamespaceName = options.Oci.Namespace, BucketName = options.Oci.Bucket, ObjectName = name
            }, NoRetries, ct);
            response.httpResponseMessage.Dispose();
        }
        catch (OciException failure) when (failure.StatusCode == HttpStatusCode.NotFound && failure.ServiceCode == "ObjectNotFound") { }
    }
}
