using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlueBridge.Core;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace BlueBridge.Desktop;
public sealed class TrustWindow : Window
{
    private readonly TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TrustWindow(PeerInfo peer, string address)
    {
        Title = "zhanhBlueB · 首次连接确认"; Width = 530; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(245, 247, 251));
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "信任这台已配对的手机？", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = $"名称：{peer.DeviceName}\n蓝牙地址：{address}\n设备编号：{peer.DeviceId}", FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "请核对手机上显示的电脑。确认后，此设备可自动发送剪贴板与文件。两分钟未确认会关闭本次连接。", FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var accept = new Button { Content = "信任连接", IsDefault = true };
        var reject = new Button { Content = "拒绝", Background = new SolidColorBrush(Color.FromRgb(100, 116, 139)) };
        accept.Click += (_, _) => { answer.TrySetResult(true); Close(); }; reject.Click += (_, _) => { answer.TrySetResult(false); Close(); };
        actions.Children.Add(accept); actions.Children.Add(reject); panel.Children.Add(actions); Content = panel;
        Closed += (_, _) => answer.TrySetResult(false);
    }
    public async Task<bool> RequestAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Show();
        using var registration = token.Register(() => Dispatcher.BeginInvoke(() => { answer.TrySetCanceled(token); Close(); }));
        return await answer.Task;
    }
}
