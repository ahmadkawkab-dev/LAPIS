namespace Wukna.Features.Chat;

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

public enum AttachmentScanVerdict { Clean, Infected }
public interface IAttachmentScanner
{
    Task<AttachmentScanVerdict> ScanAsync(Stream content, CancellationToken ct);
}

// Persistent private clamd daemon, using INSTREAM rather than daemon-visible paths.
// Only the exact positive response is clean. Errors/outages never grant availability.
public sealed class ClamAttachmentScanner(IOptions<ChatAttachmentOptions> options) : IAttachmentScanner
{
    public async Task<AttachmentScanVerdict> ScanAsync(Stream content, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.ClamTimeoutSeconds));
        var token = deadline.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(options.Value.ClamHost, options.Value.ClamPort, token);
        await using var socket = client.GetStream();
        await socket.WriteAsync("zINSTREAM\0"u8.ToArray(), token);
        var buffer = new byte[65536];
        var length = new byte[4];
        long total = 0;
        var limit = Math.Max(options.Value.MaxFileBytes, Math.Max(options.Value.MaxImageBytes, options.Value.MaxPreviewBytes));
        while (true)
        {
            var read = await content.ReadAsync(buffer, token);
            if (read == 0) break;
            total += read;
            if (total > limit) throw new IOException("clam_stream_limit");
            BinaryPrimitives.WriteInt32BigEndian(length, read);
            await socket.WriteAsync(length, token);
            await socket.WriteAsync(buffer.AsMemory(0, read), token);
        }
        await socket.WriteAsync(new byte[4], token);
        var response = new byte[4096];
        var used = 0;
        while (used < response.Length)
        {
            var read = await socket.ReadAsync(response.AsMemory(used), token);
            if (read == 0) throw new IOException("clam_incomplete_response");
            var terminator = response.AsSpan(used, read).IndexOf((byte)0);
            if (terminator >= 0)
            {
                var reply = Encoding.ASCII.GetString(response, 0, used + terminator);
                if (reply == "stream: OK") return AttachmentScanVerdict.Clean;
                if (reply.StartsWith("stream: ", StringComparison.Ordinal) && reply.EndsWith(" FOUND", StringComparison.Ordinal))
                    return AttachmentScanVerdict.Infected;
                throw new IOException("clam_scan_error");
            }
            used += read;
        }
        throw new IOException("clam_response_limit");
    }
}
