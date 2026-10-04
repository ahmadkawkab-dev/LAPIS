namespace Wukna.Features.Chat;

using Microsoft.Extensions.Options;

public interface IChatAttachmentStore
{
    Task PutQuarantineAsync(string key, Stream content, CancellationToken ct);
    Task<Stream> OpenAsync(string key, CancellationToken ct);
    Task PromoteAsync(string key, CancellationToken ct);
    // Available keys also own their quarantine copies; quarantine keys delete only that copy.
    Task DeleteAsync(string key, CancellationToken ct);
}

internal sealed class DisabledChatAttachmentStore : IChatAttachmentStore
{
    private static Exception Disabled() => new InvalidOperationException("Chat attachments are disabled.");
    public Task PutQuarantineAsync(string key, Stream content, CancellationToken ct) => throw Disabled();
    public Task<Stream> OpenAsync(string key, CancellationToken ct) => throw Disabled();
    public Task PromoteAsync(string key, CancellationToken ct) => throw Disabled();
    public Task DeleteAsync(string key, CancellationToken ct) => throw Disabled();
}

public static class ChatAttachmentKeys
{
    public static string New(bool available) => $"{(available ? 'a' : 'q')}/{Guid.NewGuid():N}";
    public static string Quarantine(string key) { Validate(key); return "q/" + key[2..]; }
    public static void Validate(string key)
    {
        if (key.Length != 34 || key[0] is not ('a' or 'q') || key[1] != '/' ||
            key.AsSpan(2).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw new ArgumentException("Invalid private attachment key.", nameof(key));
    }
}

// Development/test only. No static-files registration and no public URL generation.
public sealed class LocalChatAttachmentStore : IChatAttachmentStore
{
    private readonly string root;
    public LocalChatAttachmentStore(IOptions<ChatAttachmentOptions> options, IWebHostEnvironment environment, IConfiguration configuration)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Local chat attachment storage is development-only.");
        root = Path.GetFullPath(options.Value.Directory, environment.ContentRootPath);
        var publicRoot = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        var avatarRoot = Path.GetFullPath(configuration["ProfileImages:Directory"] ?? Path.Combine(environment.ContentRootPath, "App_Data/profile-images"));
        if (IsUnder(root, publicRoot) || IsUnder(root, avatarRoot))
            throw new InvalidOperationException("Chat attachment storage must be outside public image directories.");
        foreach (var path in new[] { root, Path.Combine(root, "q"), Path.Combine(root, "a") })
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
    private static bool IsUnder(string path, string parent) => path == parent || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private string PathFor(string key) { ChatAttachmentKeys.Validate(key); return Path.Combine(root, key[..1], key[2..]); }
    public Task PutQuarantineAsync(string key, Stream content, CancellationToken ct) => WriteAsync(ChatAttachmentKeys.Quarantine(key), content, ct);
    private async Task WriteAsync(string key, Stream content, CancellationToken ct)
    {
        var path = PathFor(key);
        // A crash during a write leaves a file owned by the durable reservation's key.
        // Workers cannot remove it while its reservation row is locked by the upload.
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await content.CopyToAsync(output, ct);
        await output.FlushAsync(ct);
    }
    public Task<Stream> OpenAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }
    public async Task PromoteAsync(string key, CancellationToken ct)
    {
        ChatAttachmentKeys.Validate(key);
        if (!key.StartsWith("a/", StringComparison.Ordinal)) throw new ArgumentException("Only normalized images can be promoted.", nameof(key));
        var target = PathFor(key);
        // Partial promotion from a failed transaction must be replaceable on retry.
        await using var input = await OpenAsync(ChatAttachmentKeys.Quarantine(key), ct);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, ct);
        await output.FlushAsync(ct);
    }
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (key.Length == 0) return Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        File.Delete(PathFor(key));
        if (key.StartsWith("a/", StringComparison.Ordinal)) File.Delete(PathFor(ChatAttachmentKeys.Quarantine(key)));
        return Task.CompletedTask;
    }
}
