using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace BlueBridge.Desktop;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private readonly bool startProduct;
    public App(bool startProduct = true) => this.startProduct = startProduct;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startProduct) return;
        if (e.Args.Contains("--bluetooth-smoke"))
        {
            try { BluetoothHost.Smoke(); Console.WriteLine("SECURE_LISTENER_START_STOP_OK"); Shutdown(0); }
            catch (Exception error) { Console.Error.WriteLine($"SECURE_LISTENER_FAILED: {error.GetType().Name}: {error.Message}"); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--render-ui")
        {
            var preview = new MainWindow(false); preview.Show();
            Dispatcher.BeginInvoke(() =>
            {
                preview.UpdateLayout();
                var visual = new RenderTargetBitmap((int)preview.ActualWidth, (int)preview.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                visual.Render(preview);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(visual));
                using (var file = File.Create(e.Args[1])) encoder.Save(file);
                Shutdown();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        instance = new Mutex(true, "Local\\zhanhBlueB.Desktop", out bool first);
        if (!first) { System.Windows.MessageBox.Show("zhanhBlueB已经运行，请从任务栏托盘打开。", "zhanhBlueB"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { System.Windows.MessageBox.Show("操作失败，请重试。若正在传输，请检查蓝牙连接。", "zhanhBlueB"); args.Handled = true; };
        var window = new MainWindow(); MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
