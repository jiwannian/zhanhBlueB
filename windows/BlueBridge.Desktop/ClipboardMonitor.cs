using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BlueBridge.Desktop;
public sealed class ClipboardMonitor : IDisposable
{
    public static uint Sequence => GetClipboardSequenceNumber();
    private const int ClipboardUpdate = 0x031D;
    private readonly HwndSource source;
    private readonly Action changed;
    public ClipboardMonitor(Window window, Action changed)
    {
        this.changed = changed;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source.AddHook(Hook);
        if (!AddClipboardFormatListener(source.Handle)) throw new System.ComponentModel.Win32Exception();
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    { if (message == ClipboardUpdate) changed(); return IntPtr.Zero; }
    public void Dispose() { RemoveClipboardFormatListener(source.Handle); source.RemoveHook(Hook); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
