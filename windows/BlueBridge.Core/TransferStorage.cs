using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BlueBridge.Core;
public sealed record TransferMetadata(string Name, long Length, string Kind, string Mime)
{
    public static void Validate(TransferMetadata value)
    {
        string? name = value.Name;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            throw new ProtocolException("文件名无效");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new ProtocolException("文件名是系统保留名称");
        if (value.Length < 0 || value.Length > 1L << 40) throw new ProtocolException("文件长度无效");
        if (value.Kind is not ("file" or "image") || string.IsNullOrWhiteSpace(value.Mime) || value.Mime.Length > 200) throw new ProtocolException("文件类型无效");
        if (value.Kind == "image" && (value.Mime != "image/png" || value.Length > 32L << 20)) throw new ProtocolException("仅支持不超过 32 MiB 的 PNG 剪贴板图片");
    }
}
public sealed record ReceivedItem(Guid Id, string Path, TransferMetadata Metadata);
public sealed record TransferProgress(Guid Id, string Name, long Bytes, long Total, bool Incoming);
public sealed class IncomingTransfer : IAsyncDisposable
{
    private readonly FileStream stream;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly string temporary;
    private readonly string directory;
    private bool closed;
    public Guid Id { get; }
    public TransferMetadata Metadata { get; }
    public long Bytes { get; private set; }
    public IncomingTransfer(Guid id, TransferMetadata metadata, string destination)
    {
        TransferMetadata.Validate(metadata);
        Id = id; Metadata = metadata; directory = Path.GetFullPath(destination);
        Directory.CreateDirectory(directory);
        string tempDirectory = Path.Combine(directory, ".partial");
        Directory.CreateDirectory(tempDirectory);
        temporary = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".part");
        stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        if (closed || bytes.Length > 65536 || Bytes + bytes.Length > Metadata.Length) throw new ProtocolException("文件块长度无效");
        await stream.WriteAsync(bytes, token);
        hash.AppendData(bytes.Span); Bytes += bytes.Length;
    }
    public async Task<ReceivedItem> CompleteAsync(string expectedHash, CancellationToken token = default)
    {
        if (closed || Bytes != Metadata.Length || expectedHash is null || expectedHash.Length != 64 || expectedHash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ProtocolException("文件不完整或校验值无效");
        if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expectedHash) throw new ProtocolException("文件 SHA-256 校验失败");
        await stream.FlushAsync(token);
        if (Metadata.Kind == "image")
        {
            stream.Position = 0;
            byte[] bytes = new byte[(int)Metadata.Length];
            await stream.ReadExactlyAsync(bytes, token);
            PngValidation.Validate(bytes);
        }
        await stream.DisposeAsync(); closed = true;
        for (int suffix = 0; suffix < 100_000; suffix++)
        {
            string name = suffix == 0 ? Metadata.Name : $"{Path.GetFileNameWithoutExtension(Metadata.Name)} ({suffix}){Path.GetExtension(Metadata.Name)}";
            string target = Path.Combine(directory, name);
            try { File.Move(temporary, target, false); return new(Id, target, Metadata); }
            catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
        }
        throw new IOException("同名文件过多，请清理下载目录");
    }
    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync(); hash.Dispose(); closed = true;
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}
public static class PngValidation
{
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 45 || !bytes[..8].SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8))
            throw new ProtocolException("无效的 PNG 图片");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]), height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        if (width == 0 || height == 0 || (ulong)width * height > 32_000_000) throw new ProtocolException("PNG 图片尺寸超过 3200 万像素");
        int offset = 8; bool data = false, end = false;
        while (offset <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) throw new ProtocolException("PNG 数据损坏");
            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8)) data = true;
            if (type.SequenceEqual("IEND"u8)) { end = length == 0 && offset + 12 == bytes.Length; break; }
            offset += checked((int)length + 12);
        }
        if (!data || !end) throw new ProtocolException("PNG 缺少完整的图像数据");
    }
}
