namespace Wukna.Features.Notifications;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

public static class WebPushConfiguration
{
    // An explicit operator command writes keys once, without printing them or starting the API.
    public static void WriteKeyPair(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute key-file path.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var values = key.ExportParameters(true);
        var publicKey = Base64UrlEncoder.Encode(new byte[] { 4 }.Concat(values.Q.X!).Concat(values.Q.Y!).ToArray());
        var privateKey = Base64UrlEncoder.Encode(values.D!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.WriteLine($"WEB_PUSH_PUBLIC_KEY={publicKey}"); writer.WriteLine($"WEB_PUSH_PRIVATE_KEY={privateKey}");
        Console.WriteLine("VAPID keys saved to the requested file. Existing files are never overwritten.");
    }
}
