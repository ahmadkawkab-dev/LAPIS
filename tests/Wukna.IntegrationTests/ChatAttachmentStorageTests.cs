namespace Wukna.IntegrationTests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Wukna.Features.Chat;
using Xunit;

public sealed class ChatAttachmentStorageTests
{
    private sealed class Environment(string name, string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Wukna";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = System.IO.Path.Combine(root, "wwwroot");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/../../private")]
    [InlineData("/tmp/file")]
    [InlineData("a/0000000000000000000000000000000x")]
    public void Private_keys_reject_traversal_and_unrecognized_identifiers(string key) =>
        Assert.Throws<ArgumentException>(() => ChatAttachmentKeys.Validate(key));

    [Fact]
    public async Task Private_quarantine_copies_are_not_available_until_promotion_and_cleanup_does_not_delete_live_images()
    {
        var root = Path.Combine(Path.GetTempPath(), "wukna-private-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalChatAttachmentStore(Options.Create(new ChatAttachmentOptions { Directory = root }),
                new Environment("Development", Path.GetTempPath()), new ConfigurationBuilder().Build());
            var key = ChatAttachmentKeys.New(true); var ct = TestContext.Current.CancellationToken;
            using var input = new MemoryStream("canonical image"u8.ToArray());
            await store.PutQuarantineAsync(key, input, ct);
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenAsync(key, ct));
            await store.PromoteAsync(key, ct); await store.DeleteAsync(ChatAttachmentKeys.Quarantine(key), ct);
            await using (var available = await store.OpenAsync(key, ct)) Assert.Equal(input.Length, available.Length);
            await store.DeleteAsync(key, ct); await store.DeleteAsync(key, ct);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Local_storage_rejects_production_and_public_roots_including_configured_avatar_storage()
    {
        var root = Path.Combine(Path.GetTempPath(), "wukna-store-guard-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ProfileImages:Directory"] = Path.Combine(root, "public-avatars")
        }).Build();
        var production = new Environment("Production", root); var development = new Environment("Development", root);
        Assert.Throws<InvalidOperationException>(() => new LocalChatAttachmentStore(Options.Create(new ChatAttachmentOptions { Directory = root }), production, configuration));
        Assert.Throws<InvalidOperationException>(() => new LocalChatAttachmentStore(Options.Create(new ChatAttachmentOptions { Directory = development.WebRootPath }), development, configuration));
        Assert.Throws<InvalidOperationException>(() => new LocalChatAttachmentStore(Options.Create(new ChatAttachmentOptions { Directory = Path.Combine(root, "public-avatars", "chat") }), development, configuration));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Production_cannot_enable_attachments_without_private_object_storage(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Chat:Attachments:Enabled"] = enabled.ToString()
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IWebHostEnvironment>(new Environment("Production", Path.GetTempPath())); services.AddChatMessages();
        using var provider = services.BuildServiceProvider();
        if (enabled) Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ChatAttachmentOptions>>().Value);
        else Assert.False(provider.GetRequiredService<IOptions<ChatAttachmentOptions>>().Value.Enabled);
    }

    [Fact]
    public void Production_can_select_valid_oci_configuration_but_rejects_missing_or_unsafe_fields()
    {
        var valid = new Dictionary<string, string?> {
            ["Chat:Attachments:Enabled"] = "true", ["Chat:Attachments:Provider"] = "Oci",
            ["Chat:Attachments:Oci:Region"] = "eu-frankfurt-1", ["Chat:Attachments:Oci:Namespace"] = "private-ns",
            ["Chat:Attachments:Oci:Bucket"] = "private-chat", ["Chat:Attachments:Oci:Prefix"] = "wukna-chat"
        };
        Check(valid, accepted: true);
        foreach (var entry in new[] {
            ("Chat:Attachments:Oci:Bucket", ""), ("Chat:Attachments:Oci:Prefix", "../other"),
            ("Chat:Attachments:Oci:Authentication", "Unknown"), ("Chat:Attachments:Oci:Authentication", "ConfigFile")
        })
        {
            var invalid = new Dictionary<string, string?>(valid) { [entry.Item1] = entry.Item2 };
            Check(invalid, accepted: false);
        }

        static void Check(Dictionary<string, string?> values, bool accepted)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IWebHostEnvironment>(new Environment("Production", Path.GetTempPath())); services.AddChatMessages();
            using var provider = services.BuildServiceProvider();
            if (accepted) Assert.Equal("Oci", provider.GetRequiredService<IOptions<ChatAttachmentOptions>>().Value.Provider);
            else Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ChatAttachmentOptions>>().Value);
        }
    }
}
