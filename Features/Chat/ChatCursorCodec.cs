namespace Wukna.Features.Chat;

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

public sealed class ChatCursorCodec(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("Wukna.BoardChat.Cursor.v1");

    public string Encode(Guid boardId, long sequence) => protector.Protect(
        $"1:{boardId:N}:{sequence.ToString(CultureInfo.InvariantCulture)}");

    public bool TryDecode(string token, Guid boardId, out long sequence)
    {
        sequence = 0;
        if (token.Length is 0 or > 1024) return false;
        try
        {
            var parts = protector.Unprotect(token).Split(':');
            return parts.Length == 3 && parts[0] == "1" &&
                Guid.TryParseExact(parts[1], "N", out var cursorBoard) && cursorBoard == boardId &&
                long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && sequence >= 0;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
