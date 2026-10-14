# zhanhBlueB Windows 端

Windows 11 x64，.NET 10 + WPF。Windows 1.0.3 正式发布包 `artifacts/release/1.0.3/zhanhBlueB-Windows-1.0.3-win-x64.zip` 自带运行时，完整解压后运行 `zhanhBlueB.exe`，无需安装 .NET。首次运行 Windows 可能提示未签名发布者；本项目没有商业代码签名证书。

## 使用

1. 在 Windows「设置 → 蓝牙和设备」和手机蓝牙设置中先完成系统配对。
2. 运行电脑 zhanhBlueB，保持「等待手机连接」状态；在手机 zhanhBlueB 中选择电脑连接。
3. 两端核对设备名称、蓝牙地址并确认信任。以后同一设备身份与地址自动通过应用确认。
4. 电脑复制文字和图片后会自动发到手机。取消「自动发送」可暂停；「发送剪贴板」可手动发送。
5. 用「选择文件发送」多选文件，或直接拖文件进窗口。行内显示进度，可取消和显式重试。文件夹暂不支持。
6. 手机发来的文字/图片直接写入电脑剪贴板；文件及接收的 PNG 保存到系统下载目录 `zhanhBlueB` 子目录。同名加 `(1)`、`(2)`，不覆盖已有文件。图片还提供「图片另存为」。
7. 双击已完成的图片或文件记录，可使用 Windows 默认应用打开。电脑剪贴板图片发送成功后也会在下载目录保存一份 PNG 副本，便于从该记录再次打开。
8. 在记录上右键可选择“打开”“删除记录”或“删除记录与本地文件”。后者默认选中“取消”，确认后只将电脑上的本地路径移入回收站；发送记录会明确提示这是发送时选择的原文件，手机副本不受影响。文件占用、权限不足、路径正在传输或回收失败时保留记录，不执行永久删除。

关闭窗口会隐藏到系统托盘。托盘菜单「退出」停止监听并结束进程。「停止连接」立即停止监听。开机启动默认关闭；勾选后只注册当前用户的启动项。清除信任只影响后续应用确认；系统配对需在 Windows 设置中单独解除。

蓝牙传输速度取决于硬件、驱动和距离，适合中小文件。断连取消当前传输，重试从头发送；不会自动重播旧剪贴板或将旧队列投递给新连接。截图传输失败后可点击「重试」，最多保留三张内存副本，退出即消失；断连时尚未发送的截图会丢弃。没有云服务、网络传输或剪贴板内容日志。

## 开发与验证

```powershell
dotnet test BlueBridge.Core.Tests/BlueBridge.Core.Tests.csproj -c Release --collect:"XPlat Code Coverage" --results-directory validation/test-results
dotnet test BlueBridge.Bluetooth.Tests/BlueBridge.Bluetooth.Tests.csproj -c Release --logger "trx;LogFileName=bluetooth-green.trx" --results-directory validation/bluetooth-1.0.1
dotnet build BlueBridge.HeadlessPeer/BlueBridge.HeadlessPeer.csproj -c Release
dotnet publish BlueBridge.Desktop/BlueBridge.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish
dotnet BlueBridge.Desktop/bin/Release/net10.0-windows/zhanhBlueB.dll --bluetooth-smoke
```

`BlueBridge.Core` 是不依赖 UI 或蓝牙硬件的生产传输实现。`BlueBridge.HeadlessPeer` 仅为跨语言管道测试工具：标准输出全部为协议二进制，诊断只写标准错误，测试专用自动信任不会编入桌面应用。测试工具不进入正式发布目录。

32feet.NET 3.5.0.3 是 .NET Framework 兼容资产。选择其 Win32 接口是因为它能在监听前显式设置 `Authenticate=true` 与 `Encrypt=true`；现代 4.x listener 不提供同等安全选项。原生监听套接字强制认证与加密，选项设置失败就不启动监听；接入时复核监听配置、检查系统配对状态，随后要求双方应用确认信任。任一检查失败都关闭连接。此兼容资产的 NuGet NU1701 警告已在项目中单项抑制，初版曾通过本机监听启动测试。

Windows 1.0.1 修复初版误拒绝连接的问题：32feet 3.5 新建 accepted-client 的 `Authenticate`/`Encrypt` getter 只读取默认值为 `false` 的包装器缓存，不能用来判断真实链路安全状态。本次删除这项错误断言，保留上述安全控制。[微软蓝牙套接字文档](https://learn.microsoft.com/en-us/windows/win32/bluetooth/bluetooth-and-socket-options)说明监听前的认证/加密选项约束 `accept`；认证与加密选项也不能使用 `getsockopt` 查询。协议与 Android 1.0.0 保持兼容。

`BlueBridge.Bluetooth.Tests` 编译链接生产 `BluetoothHost.cs`，以确定性蓝牙适配器覆盖安全监听、已配对接入、未配对拒绝、配置丢失拒绝、原生选项设置失败及单连接限制；不启用真实蓝牙，不读取或发送用户数据。这些测试验证接入逻辑，实际手机重连与互传仍需真机验收。

详见 [验证说明](validation/README.md)。
