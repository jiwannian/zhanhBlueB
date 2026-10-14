using System.ComponentModel;
using System.Diagnostics;

namespace BlueBridge.Desktop;
public static class TransferFileOpener
{
    private const string MissingFile = "文件已被移动或删除，请到下载目录检查";
    public static string? Open(TransferRow row, Action<ProcessStartInfo> launch)
    {
        if (!row.CanOpen) return "此记录没有已完成的本地文件可打开";
        string path = row.OpenPath!;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return "无法打开：文件路径无效";
            if (!File.Exists(path)) return MissingFile;
            launch(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open", WorkingDirectory = Path.GetDirectoryName(path)! });
            return null;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3) { return MissingFile; }
        catch (Win32Exception error) when (error.NativeErrorCode == 1155) { return "Windows 尚未设置此类文件的默认应用，请先设置后重试"; }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return "无法打开文件，请检查默认应用或文件权限"; }
    }
}
