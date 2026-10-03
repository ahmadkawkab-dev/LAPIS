namespace Wukna.Features.Chat;

using Microsoft.Extensions.Options;
using Oci.Common;
using Oci.Common.Auth;
using Oci.ObjectstorageService;
using Org.BouncyCastle.Crypto.Parameters;

// Signing and credential refresh stay in Oracle's SDK. Only immutable, already acquired
// credentials reach an object-storage request: synchronous IMDS/federation refresh cannot
// start a late PUT after its caller's deadline has expired.
public sealed class OciChatAttachmentClients
{
    private readonly IOptions<ChatAttachmentOptions> options;
    private readonly Func<IBasicAuthenticationDetailsProvider> createCredentials;
    private readonly object gate = new();
    private IBasicAuthenticationDetailsProvider? source;
    private Task<CredentialSnapshot>? acquisition;

    public OciChatAttachmentClients(IOptions<ChatAttachmentOptions> options) : this(options, () =>
    {
        var settings = options.Value.Oci;
        return settings.Authentication == "InstancePrincipals"
            ? new InstancePrincipalsAuthenticationDetailsProvider()
            : new ConfigFileAuthenticationDetailsProvider(settings.ConfigFile, settings.Profile);
    }) { }

    public OciChatAttachmentClients(IOptions<ChatAttachmentOptions> options, Func<IBasicAuthenticationDetailsProvider> createCredentials)
    {
        this.options = options;
        this.createCredentials = createCredentials;
    }

    public async Task<ObjectStorageClient> CreateAsync(CancellationToken ct)
    {
        Task<CredentialSnapshot> pending;
        lock (gate)
        {
            if (acquisition is null || acquisition.IsCompleted)
            {
                acquisition = Task.Run(() =>
                {
                    source ??= createCredentials();
                    // KeyId may refresh/rotate the instance-principal session key. Read it first.
                    return new CredentialSnapshot(source.KeyId, source.GetPrivateKey(), options.Value.Oci.Region);
                });
                // Observe an eventual fault even if every waiting caller has cancelled.
                _ = acquisition.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            pending = acquisition;
        }
        var credentials = await pending.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        var client = new ObjectStorageClient(credentials, Configuration(options.Value));
        try { client.SetRegion(Region.FromRegionId(options.Value.Oci.Region)); return client; }
        catch { client.Dispose(); throw; }
    }

    public static ClientConfiguration Configuration(ChatAttachmentOptions settings) => new()
    {
        TimeoutMillis = settings.IoTimeoutSeconds * 1000,
        ResponseContentBufferBytes = Math.Max(settings.MaxPreviewBytes, Math.Max(settings.MaxFileBytes, settings.MaxImageBytes)),
        ClientCertificateOption = ClientCertificateOption.Manual
    };

    private sealed class CredentialSnapshot(string keyId, RsaKeyParameters key, string region) : IBasicAuthenticationDetailsProvider, IRegionProvider
    {
        public string KeyId => keyId;
        public char[] PassPhraseCharacters => [];
        public Region Region => Region.FromRegionId(region);
        public RsaKeyParameters GetPrivateKey() => key;
    }
}
