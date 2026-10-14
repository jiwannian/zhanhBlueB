using System.IO;
using BlueBridge.Desktop;

internal static class RecycleBinChecks
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zhanhBlueB-recycle-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "测试 中文 & 可恢复.txt");
        byte[] bytes = "仅测试新建临时文件\n"u8.ToArray();
        File.WriteAllBytes(source, bytes);
        try
        {
            var recycler = new WindowsFileRecycler();
            var result = recycler.RecycleAsync(source).GetAwaiter().GetResult();
            if (result.Status != RecycleStatus.Success) throw new InvalidOperationException("Recycle smoke failed: " + result);
            if (File.Exists(source) || result.RecycledPath is null || !File.Exists(result.RecycledPath))
                throw new InvalidOperationException("File is not recoverable from recycle bin: " + result);
            if (!File.ReadAllBytes(result.RecycledPath).SequenceEqual(bytes)) throw new InvalidOperationException("Recycled content changed");
            // Restore only this fixture, then remove its own recycle metadata; never empty the recycle bin.
            File.Move(result.RecycledPath, source);
            string metadata = Path.Combine(Path.GetDirectoryName(result.RecycledPath)!, "$I" + Path.GetFileName(result.RecycledPath)[2..]);
            if (File.Exists(metadata)) File.Delete(metadata);
            if (!File.ReadAllBytes(source).SequenceEqual(bytes)) throw new InvalidOperationException("Recovery content mismatch");
            if (recycler.RecycleAsync(Path.Combine(directory, "missing")).GetAwaiter().GetResult().Status != RecycleStatus.NotFound)
                throw new InvalidOperationException("Missing file was not recognized");
            if (recycler.RecycleAsync(directory).GetAwaiter().GetResult().Status != RecycleStatus.Failed)
                throw new InvalidOperationException("Recycler accepted a directory");
            using (var locked = File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var blocked = recycler.RecycleAsync(source).GetAwaiter().GetResult();
                if (blocked.Status is RecycleStatus.Success or RecycleStatus.NotFound || !File.Exists(source))
                    throw new InvalidOperationException("Locked fixture was deleted or reported missing");
            }
            if (!File.ReadAllBytes(source).SequenceEqual(bytes)) throw new InvalidOperationException("Locked fixture content changed");
            Console.WriteLine("PASS: real IFileOperation recycle, recovery path, restored bytes, missing-file/directory/locked-file protection; only isolated fixtures used");
        }
        finally { Directory.Delete(directory, true); }
    }
}
