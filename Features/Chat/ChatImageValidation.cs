namespace Wukna.Features.Chat;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;

internal sealed record ChatNormalizedImage(byte[] Image, byte[] Preview, int Width, int Height);
internal sealed class ChatUploadValidationException(string code, int status = 400) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

public sealed class ChatImageValidation
{
    // Bound simultaneous decodes as well as individual pixels/allocator buffers.
    private readonly SemaphoreSlim decoders = new(2, 2);
    private readonly DecoderOptions decoder;
    public ChatImageValidation()
    {
        var config = Configuration.Default.Clone();
        config.MaxDegreeOfParallelism = 2;
        config.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 128, MaximumPoolSizeMegabytes = 32 });
        decoder = new DecoderOptions { Configuration = config, MaxFrames = 1 };
    }

    internal async Task<ChatNormalizedImage> NormalizeAsync(byte[] bytes, string contentType, ChatAttachmentOptions options, CancellationToken ct)
    {
        if (!await decoders.WaitAsync(0, ct)) throw new ChatUploadValidationException("chat_image_busy", 429);
        try
        {
            using var input = new MemoryStream(bytes, false);
            var info = await Image.IdentifyAsync(decoder, input, ct);
            if (info is null || info.Width < 1 || info.Height < 1 || info.Width > 4096 || info.Height > 4096 ||
                (long)info.Width * info.Height > 16_000_000)
                throw new ChatUploadValidationException("chat_image_dimensions");
            var expected = contentType switch { "image/jpeg" => "JPEG", "image/png" => "PNG", "image/webp" => "Webp", _ => "" };
            if (info.Metadata.DecodedImageFormat?.Name != expected) throw new ChatUploadValidationException("chat_image_type", 415);
            input.Position = 0;
            using var image = await Image.LoadAsync(decoder, input, ct);
            // Animated inputs deliberately produce a static first-frame image.
            image.Mutate(processing => processing.AutoOrient());
            image.Metadata.ExifProfile = null; image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null; image.Metadata.IptcProfile = null;
            using var normalized = new BoundedImageStream(options.MaxImageBytes);
            await image.SaveAsync(normalized, new WebpEncoder { Quality = 82, SkipMetadata = true }, ct);
            using var previewImage = image.Clone(processing => processing.Resize(new ResizeOptions {
                Mode = ResizeMode.Max, Size = new Size(480, 480)
            }));
            using var preview = new BoundedImageStream(options.MaxPreviewBytes);
            await previewImage.SaveAsync(preview, new WebpEncoder { Quality = 75, SkipMetadata = true }, ct);
            return new(normalized.ToArray(), preview.ToArray(), image.Width, image.Height);
        }
        catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException or InvalidMemoryOperationException)
        {
            throw new ChatUploadValidationException("chat_invalid_image");
        }
        finally { decoders.Release(); }
    }

    private sealed class BoundedImageStream(int max) : MemoryStream
    {
        private void Check(int count) { if (Position + count > max) throw new ChatUploadValidationException("chat_encoded_image_too_large", 413); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
    }
}
