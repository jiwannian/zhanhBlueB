using System.Security.Cryptography;
using BlueBridge.Core;

namespace BlueBridge.Desktop;

public static class ClipboardImageStore
{
    public static async Task<string> SavePngAsync(ReadOnlyMemory<byte> bytes, string directory, CancellationToken token = default)
    {
        await using var transfer = new IncomingTransfer(Guid.NewGuid(), new("clipboard.png", bytes.Length, "image", "image/png"), directory);
        for (int offset = 0; offset < bytes.Length; offset += 65536)
            await transfer.WriteAsync(bytes.Slice(offset, Math.Min(65536, bytes.Length - offset)), token);
        string hash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        return (await transfer.CompleteAsync(hash, token)).Path;
    }
}
