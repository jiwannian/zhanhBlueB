using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlueBridge.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BlueBridge.Desktop;
public partial class MainWindow : Window
{
    public ObservableCollection<TransferRow> Transfers { get; } = [];
    private readonly SettingsStore settings = new();
    private readonly ClipboardSequenceGuard echo = new();
    private readonly Channel<TransferJob> queue = Channel.CreateBounded<TransferJob>(100);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<Guid, byte[]> retryImages = [];
    private readonly Queue<Guid> retryImageOrder = [];
    private readonly BluetoothHost host;
    private readonly string destination;
    private readonly Task queueWorker;
    private readonly Action<ProcessStartInfo> fileLauncher;
    private Forms.NotifyIcon? tray;
    private ClipboardMonitor? clipboard;
    private bool initialized, listening, exiting;
    private int imageQueued;
    private string? lastImage;
    public MainWindow(bool startHost = true, Action<ProcessStartInfo>? fileLauncher = null, string? downloadDirectory = null)
    {
        this.fileLauncher = fileLauncher ?? (info => Process.Start(info));
        InitializeComponent(); DataContext = this;
        destination = downloadDirectory ?? DownloadFolder.PathValue; DownloadFolder.Prepare(destination);
        DestinationText.Text = "保存位置：" + destination;
        AutoClipboardBox.IsChecked = settings.Value.AutoClipboard; StartupBox.IsChecked = settings.Value.StartWithWindows;
        host = new(new(settings.Value.DeviceId, Environment.MachineName), destination, Callbacks, SessionChanged, SetConnection);
        queueWorker = ProcessQueueAsync();
        initialized = true;
        if (startHost)
        {
            SourceInitialized += (_, _) => clipboard = new(this, ClipboardChanged);
            Loaded += (_, _) => { CreateTray(); StartListening(); };
        }
    }
    private SessionCallbacks Callbacks(string address) => new()
    {
        ApprovePeerCancelable = (peer, token) => Dispatcher.InvokeAsync(async () =>
        {
            if (settings.IsTrusted(peer.DeviceId, address)) return true;
            Show(); Activate();
            bool approved = await new TrustWindow(peer, address) { Owner = this }.RequestAsync(token);
            if (approved) settings.Trust(peer.DeviceId, address);
            return approved;
        }).Task.Unwrap(),
        ReceiveText = (_, text) => ApplyClipboardAsync(() => { if (text.Length == 0) System.Windows.Clipboard.Clear(); else System.Windows.Clipboard.SetText(text); echo.MarkApplied(ClipboardMonitor.Sequence); SetActivity("已接收文字并写入剪贴板"); }),
        ReceiveItem = ReceiveItemAsync,
        Progress = progress => Dispatcher.BeginInvoke(() => UpdateProgress(progress)),
        Status = SetConnection,
        TransferFailed = (id, reason) => Dispatcher.BeginInvoke(() => { UpdateRow(id, row => row with { State = "失败，可重试" }); SetActivity(reason); })
    };
    private void SessionChanged(BridgeSession? session)
    {
        if (session is not null) _ = ObserveReadyAsync(session);
        else
        {
            foreach (var cancel in cancellations.Values) cancel.Cancel();
            Dispatcher.BeginInvoke(() => { foreach (var row in Transfers.ToArray().Where(x => x.CanCancel)) UpdateRow(row.Id, x => x with { State = "失败，可重试" }); });
        }
    }
    private async Task ObserveReadyAsync(BridgeSession session) { try { await session.Ready; SetActivity("连接成功，电脑复制的内容将自动发送"); } catch (IOException) { } }
    private async Task ReceiveItemAsync(ReceivedItem item)
    {
        if (item.Metadata.Kind == "image")
        {
            BitmapSource bitmap;
            using (var file = File.OpenRead(item.Path)) { bitmap = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; bitmap.Freeze(); }
            await ApplyClipboardAsync(() => { System.Windows.Clipboard.SetImage(bitmap); echo.MarkApplied(ClipboardMonitor.Sequence); lastImage = item.Path; SaveImageButton.IsEnabled = true; });
        }
        await Dispatcher.InvokeAsync(() => { UpdateRow(item.Id, row => row with { State = "已完成", Bytes = item.Metadata.Length, SavedPath = item.Path }); SetActivity(item.Metadata.Kind == "image" ? "已接收图片并写入剪贴板" : "文件已保存至下载目录"); });
    }
    private async Task ApplyClipboardAsync(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await Dispatcher.InvokeAsync(action); return; }
            catch (COMException) when (attempt < 5) { await Task.Delay(80); }
        }
    }
    private async void ClipboardChanged()
    {
        if (!echo.ShouldProcess(ClipboardMonitor.Sequence)) return;
        if (!settings.Value.AutoClipboard || host.Session?.IsReady != true) return;
        await SendClipboardAsync(false);
    }
    private async Task SendClipboardAsync(bool manual)
    {
        try
        {
            var session = host.Session;
            if (session?.IsReady != true) { if (manual) SetActivity("请先在手机上连接并确认信任"); return; }
            if (System.Windows.Clipboard.ContainsImage())
            {
                var bitmap = System.Windows.Clipboard.GetImage();
                if (bitmap is null) return;
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32_000_000) throw new ProtocolException("剪贴板图片超过 3200 万像素");
                byte[] bytes = EncodePng(bitmap);
                if (bytes.Length > 32 << 20) throw new ProtocolException("剪贴板图片超过 32 MiB");
                if (Interlocked.CompareExchange(ref imageQueued, 1, 0) != 0) { SetActivity("上一张剪贴板图片仍在发送"); return; }
                var job = new TransferJob(Guid.NewGuid(), null, bytes, "clipboard.png", session);
                if (!Enqueue(job)) Interlocked.Exchange(ref imageQueued, 0);
            }
            else if (System.Windows.Clipboard.ContainsText())
            {
                string text = System.Windows.Clipboard.GetText();
                await session.SendTextAsync(text); SetActivity("文字已发送");
            }
            else if (manual) SetActivity("剪贴板中没有文字或图片；文件请用选择文件发送");
        }
        catch (Exception e) when (e is IOException or COMException or InvalidOperationException or NotSupportedException) { SetActivity("发送失败：" + e.Message); }
    }
    private static byte[] EncodePng(BitmapSource bitmap) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var memory = new MemoryStream(); encoder.Save(memory); return memory.ToArray(); }
    private void SendFilesClick(object sender, RoutedEventArgs args)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择发送到手机的文件" };
        if (picker.ShowDialog(this) == true) AddFiles(picker.FileNames);
    }
    private void AddFiles(IEnumerable<string> paths)
    {
        var session = host.Session;
        if (session?.IsReady != true) { SetActivity("请先连接手机"); return; }
        foreach (string path in paths)
        {
            if (!File.Exists(path)) { SetActivity("仅支持文件，暂不支持文件夹"); continue; }
            Enqueue(new(Guid.NewGuid(), path, null, Path.GetFileName(path), session));
        }
    }
    private bool Enqueue(TransferJob job)
    {
        cancellations[job.Id] = new();
        long length = job.Bytes?.LongLength ?? new FileInfo(job.Path!).Length;
        Transfers.Insert(0, new(job.Id, job.Name, "电脑 → 手机", "等待发送", 0, length, job.Path));
        if (!queue.Writer.TryWrite(job)) { cancellations.TryRemove(job.Id, out var cancel); cancel?.Dispose(); Transfers.RemoveAt(FindRow(job.Id)); SetActivity("传输队列已满，请等待完成后重试"); return false; }
        TrimRows(); return true;
    }
    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(shutdown.Token))
            {
                if (!cancellations.TryGetValue(job.Id, out var cancel)) continue;
                try
                {
                    if (cancel.IsCancellationRequested) continue;
                    var session = job.Session;
                    if (!ReferenceEquals(host.Session, session) || !session.IsReady) throw new IOException("原蓝牙会话已结束，请重新连接后手动重试");
                    await Dispatcher.InvokeAsync(() => UpdateRow(job.Id, x => x with { State = "正在发送" }));
                    if (job.Bytes is not null) await session.SendStreamAsync(new MemoryStream(job.Bytes, false), new(job.Name, job.Bytes.Length, "image", "image/png"), job.Id, cancel.Token);
                    else await session.SendFileAsync(job.Path!, messageId: job.Id, token: cancel.Token);
                    await CompleteSentAsync(job.Id, job.Bytes);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
                { await Dispatcher.InvokeAsync(() => { if (job.Bytes is not null && ReferenceEquals(host.Session, job.Session) && job.Session.IsReady) CacheRetryImage(job.Id, job.Bytes); UpdateRow(job.Id, x => x with { State = cancel.IsCancellationRequested ? "已取消" : "失败，可重试", ImageRetry = retryImages.ContainsKey(job.Id) }); SetActivity(cancel.IsCancellationRequested ? "传输已取消" : "文件发送失败：" + e.Message); }); }
                finally { cancellations.TryRemove(job.Id, out _); cancel.Dispose(); if (job.Bytes is not null) Interlocked.Exchange(ref imageQueued, 0); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task CompleteSentAsync(Guid id, byte[]? bytes)
    {
        string? savedPath = null;
        string? warning = null;
        if (bytes is not null)
        {
            try { savedPath = await ClipboardImageStore.SavePngAsync(bytes, destination, shutdown.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
            { warning = "图片已发送，本地保存失败，无法双击打开"; }
        }
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateRow(id, row => row with { State = "已完成", Bytes = row.Total, SavedPath = savedPath });
            if (warning is not null) SetActivity(warning);
        });
    }
    private void UpdateProgress(TransferProgress progress)
    {
        int index = FindRow(progress.Id);
        if (index < 0) { Transfers.Insert(0, new(progress.Id, progress.Name, "手机 → 电脑", "正在接收", progress.Bytes, progress.Total)); TrimRows(); }
        else Transfers[index] = Transfers[index] with { Bytes = progress.Bytes, Total = progress.Total };
    }
    private int FindRow(Guid id) { for (int i = 0; i < Transfers.Count; i++) if (Transfers[i].Id == id) return i; return -1; }
    private void UpdateRow(Guid id, Func<TransferRow, TransferRow> change) { int index = FindRow(id); if (index >= 0) Transfers[index] = change(Transfers[index]); }
    private void TrimRows() { while (Transfers.Count > 150) { int index = Transfers.Count - 1; if (Transfers[index].CanCancel) break; Transfers.RemoveAt(index); } }
    private async void CancelTransferClick(object sender, RoutedEventArgs args)
    {
        Guid id = (Guid)((System.Windows.Controls.Button)sender).Tag;
        if (cancellations.TryGetValue(id, out var cancel)) cancel.Cancel();
        try { if (host.Session is { } session) await session.CancelAsync(id); UpdateRow(id, x => x with { State = "已取消" }); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { SetActivity("连接已断开"); }
    }
    private void RetryTransferClick(object sender, RoutedEventArgs args)
    {
        Guid id = (Guid)((System.Windows.Controls.Button)sender).Tag; int index = FindRow(id);
        if (index < 0) return;
        if (Transfers[index].Source is { } path) AddFiles([path]);
        else if (retryImages.TryGetValue(id, out var bytes) && host.Session is { IsReady: true } session)
        {
            if (Interlocked.CompareExchange(ref imageQueued, 1, 0) != 0) { SetActivity("上一张图片仍在发送"); return; }
            if (!Enqueue(new(Guid.NewGuid(), null, bytes, "clipboard.png", session))) Interlocked.Exchange(ref imageQueued, 0);
        }
    }
    private void CacheRetryImage(Guid id, byte[] bytes)
    {
        retryImages[id] = bytes; retryImageOrder.Enqueue(id);
        while (retryImageOrder.Count > 3) { Guid old = retryImageOrder.Dequeue(); retryImages.Remove(old); UpdateRow(old, x => x with { ImageRetry = false }); }
    }
    private void FilesDragOver(object sender, System.Windows.DragEventArgs args) { args.Effects = args.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; args.Handled = true; }
    private void FilesDropped(object sender, System.Windows.DragEventArgs args) { if (args.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) AddFiles(paths); }
    private async void SendClipboardClick(object sender, RoutedEventArgs args) => await SendClipboardAsync(true);
    private void SaveImageClick(object sender, RoutedEventArgs args)
    {
        if (lastImage is null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "zhanhBlueB.png" };
        if (picker.ShowDialog(this) == true) try { File.Copy(lastImage, picker.FileName, true); SetActivity("图片已另存为"); } catch (IOException e) { SetActivity("保存失败：" + e.Message); }
    }
    private void TransferDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs args)
    {
        if (args.ChangedButton != System.Windows.Input.MouseButton.Left ||
            sender is not System.Windows.Controls.ListBoxItem { DataContext: TransferRow row }) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(args.OriginalSource as DependencyObject) is not null) return;
        string? error = TransferFileOpener.Open(row, fileLauncher);
        if (error is not null) SetActivity(error);
        else SetActivity("已打开：" + row.Name);
        args.Handled = true;
    }
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
    private void OpenFolderClick(object sender, RoutedEventArgs args) => Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
    private async void ListenClick(object sender, RoutedEventArgs args)
    { if (listening) { await host.DisposeAsync(); listening = false; ListenButton.Content = "开启连接"; SetConnection("蓝牙监听已停止"); } else StartListening(); }
    private void StartListening() { try { host.Start(); listening = true; ListenButton.Content = "停止连接"; } catch (Exception e) when (e is IOException or PlatformNotSupportedException or System.Net.Sockets.SocketException or TypeInitializationException) { SetConnection("无法开启蓝牙：" + e.Message); } }
    private void AutoClipboardChanged(object sender, RoutedEventArgs args) { if (initialized) settings.Save(settings.Value with { AutoClipboard = AutoClipboardBox.IsChecked == true }); }
    private void StartupChanged(object sender, RoutedEventArgs args) { if (initialized) try { settings.SetStartup(StartupBox.IsChecked == true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetActivity(e.Message); } }
    private void ForgetTrustClick(object sender, RoutedEventArgs args)
    { if (System.Windows.MessageBox.Show(this, "清除信任后，下次连接需要重新确认。", "清除设备信任", MessageBoxButton.OKCancel) == MessageBoxResult.OK) { settings.Save(settings.Value with { Trusted = [] }); SetActivity("设备信任已清除"); } }
    private void SetActivity(string text) { if (Dispatcher.CheckAccess()) ActivityText.Text = text; else Dispatcher.BeginInvoke(() => ActivityText.Text = text); }
    private void SetConnection(string text) { if (Dispatcher.CheckAccess()) ConnectionText.Text = text; else Dispatcher.BeginInvoke(() => ConnectionText.Text = text); }
    private void HideClick(object sender, RoutedEventArgs args) => Hide();
    private void WindowClosing(object? sender, CancelEventArgs args) { if (!exiting) { args.Cancel = true; Hide(); tray?.ShowBalloonTip(1500, "zhanhBlueB正在运行", "点击托盘图标可重新打开窗口。", Forms.ToolTipIcon.Info); } }
    private void CreateTray()
    {
        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/BlueBridge.ico")).Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        tray = new() { Text = "zhanhBlueB", Icon = (System.Drawing.Icon)icon.Clone(), Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => { Show(); Activate(); });
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("打开zhanhBlueB", null, (_, _) => Dispatcher.BeginInvoke(() => { Show(); Activate(); })); menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitAsync)); tray.ContextMenuStrip = menu;
    }
    private async void ExitAsync()
    {
        if (exiting) return; exiting = true;
        clipboard?.Dispose(); queue.Writer.TryComplete(); shutdown.Cancel(); foreach (var cancellation in cancellations.Values) cancellation.Cancel();
        await host.DisposeAsync(); await queueWorker; tray?.Dispose(); Close(); System.Windows.Application.Current.Shutdown();
    }
    private sealed record TransferJob(Guid Id, string? Path, byte[]? Bytes, string Name, BridgeSession Session);
}
