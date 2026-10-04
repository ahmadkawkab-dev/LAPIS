namespace Wukna.IntegrationTests;

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Wukna.Features.Chat;
using Xunit;

public sealed class ClamAttachmentScannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("stream: OK\0", AttachmentScanVerdict.Clean)]
    [InlineData("stream: Eicar-Signature FOUND\0", AttachmentScanVerdict.Infected)]
    public async Task Uses_nul_framed_instream_with_big_endian_chunks_and_fragmented_replies(string reply, AttachmentScanVerdict verdict)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var bytes = new byte[140000]; new Random(7).NextBytes(bytes);
        var server = Task.Run(async () => {
            using var client = await listener.AcceptTcpClientAsync(Ct); await using var socket = client.GetStream();
            var command = new byte[10]; await socket.ReadExactlyAsync(command, Ct); Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));
            using var received = new MemoryStream(); var size = new byte[4];
            while (true)
            {
                await socket.ReadExactlyAsync(size, Ct); var length = BinaryPrimitives.ReadInt32BigEndian(size);
                if (length == 0) break; Assert.InRange(length, 1, 65536);
                var chunk = new byte[length]; await socket.ReadExactlyAsync(chunk, Ct); received.Write(chunk);
            }
            Assert.Equal(bytes, received.ToArray());
            foreach (var value in Encoding.ASCII.GetBytes(reply)) await socket.WriteAsync(new[] { value }, Ct);
        }, Ct);
        var options = Options.Create(new ChatAttachmentOptions { ClamPort = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var content = new MemoryStream(bytes, false);
        Assert.Equal(verdict, await new ClamAttachmentScanner(options).ScanAsync(content, Ct)); await server;
    }

    [Theory]
    [InlineData("stream: size limit exceeded ERROR\0")]
    [InlineData("stream: OK")]
    [InlineData("unknown command\0")]
    [InlineData("stream: ignored OK\0")]
    public async Task Error_malformed_and_unterminated_responses_fail_closed(string reply)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () => {
            using var client = await listener.AcceptTcpClientAsync(Ct); await using var socket = client.GetStream();
            var request = new byte[14]; await socket.ReadExactlyAsync(request, Ct);
            await socket.WriteAsync(Encoding.ASCII.GetBytes(reply), Ct);
        }, Ct);
        var options = Options.Create(new ChatAttachmentOptions { ClamPort = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var empty = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => new ClamAttachmentScanner(options).ScanAsync(empty, Ct)); await server;
    }

    [Fact]
    public async Task Response_length_and_socket_time_are_bounded()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () => {
            using var client = await listener.AcceptTcpClientAsync(Ct); await using var socket = client.GetStream();
            var request = new byte[14]; await socket.ReadExactlyAsync(request, Ct);
            await socket.WriteAsync(Encoding.ASCII.GetBytes(new string('x', 4096)), Ct);
            using var second = await listener.AcceptTcpClientAsync(Ct);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }, Ct);
        var options = Options.Create(new ChatAttachmentOptions { ClamPort = ((IPEndPoint)listener.LocalEndpoint).Port, ClamTimeoutSeconds = 1 });
        var scanner = new ClamAttachmentScanner(options); using var empty = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => scanner.ScanAsync(empty, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(empty, Ct)); await server;
    }
}
