using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace BlueBridge.Desktop;
public sealed record TrustedPeer(Guid DeviceId, string Address);
public sealed record Settings(Guid DeviceId, bool AutoClipboard, bool StartWithWindows, IReadOnlyList<TrustedPeer> Trusted)
{
    public static Settings Default => new(Guid.NewGuid(), true, false, []);
}
public sealed class SettingsStore
{
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zhanhBlueB", "settings.json");
    public Settings Value { get; private set; }
    public SettingsStore()
    {
        try { Value = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? Settings.Default; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Value = Settings.Default; }
        if (Value.DeviceId == Guid.Empty || Value.Trusted is null) Value = Settings.Default;
        Save(Value);
    }
    public void Save(Settings value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); Value = value;
    }
    public bool IsTrusted(Guid id, string address) => Value.Trusted.Any(x => x.DeviceId == id && x.Address == address);
    public void Trust(Guid id, string address) => Save(Value with { Trusted = Value.Trusted.Where(x => !(x.DeviceId == id && x.Address == address)).Append(new TrustedPeer(id, address)).ToArray() });
    public void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true) ?? throw new IOException("无法访问开机启动设置");
        if (enabled) key.SetValue("zhanhBlueB", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("zhanhBlueB", false);
        Save(Value with { StartWithWindows = enabled });
    }
}
public static class DownloadFolder
{
    public static void Prepare(string directory)
    {
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(Path.GetFullPath(directory), ".partial");
        if (!Directory.Exists(partial) || File.GetAttributes(partial).HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (string file in Directory.EnumerateFiles(partial, "*.part", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != 32 || name.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
            try { File.Delete(file); } catch (IOException) { }
        }
    }
    public static string PathValue
    {
        get
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            int result = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var pointer);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            try { return Path.Combine(Marshal.PtrToStringUni(pointer)!, "zhanhBlueB"); }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }
    }
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
