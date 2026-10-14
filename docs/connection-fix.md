# Windows 1.0.1 连接修复

修复日期：2026-10-14。

Android 1.0.0 保持兼容，无需重装 APK。系统蓝牙配对及本地信任配置不需要清除。

## 问题与原因

用户在 vivo X300 与 Windows 系统完成配对后，Windows 1.0.0 显示
“连接已拒绝：该连接没有通过蓝牙认证与加密检查”，手机显示 socket closed
或“两端信任确认超时或被拒绝”。这是 Windows 接收路径的误判。

32feet.NET 3.5 的 `SocketBluetoothClient.MsftSocketOptionHelper` 在创建时将
`authenticate`、`encrypt` 缓存字段初始化为 false。Windows 平台的两个 getter
只返回这些字段，不查询实际蓝牙链路。`EndAcceptBluetoothClient` 为接收的
socket 创建新的 client/helper，所以监听端要求认证和加密后，接收 wrapper
的属性仍可返回 false。旧代码因检查这个缓存值而拒绝连接，在发送 HELLO
和显示双方信任提示前就关闭了手机 socket。

## 修复与安全要求

新代码使用本程序实际配置的监听端安全策略判断接收条件，不再把接收 wrapper
的缓存属性当成链路状态。仍保留：

- 监听前通过原生 `setsockopt` 强制蓝牙认证及加密；设置失败就停止监听。
- 接收端检查设备已在 Windows 系统配对。
- 首次两端应用信任确认及后续设备身份/蓝牙地址绑定。
- HELLO/信任确认的超时和断连清理。

这没有取消认证或加密。微软文档明确，监听 socket 要求认证/加密时，不满足
条件的连接由操作系统拒绝；同时不支持用 `getsockopt` 读取这些安全选项。
因此没有增加会失败或产生错误安全判断的原生状态查询。

依据：项目附带的官方 32feet 3.5 源码归档中
`Net.Bluetooth.Msft/SocketBluetoothClient.cs` 和 `WindowsBluetoothListener.cs`；
[Microsoft Bluetooth and Socket Options](https://learn.microsoft.com/en-us/windows/win32/bluetooth/bluetooth-and-socket-options)。

## 升级

1. 从 Windows 旧程序托盘菜单选择“退出”；仅关闭窗口会隐藏到托盘。
2. 完整解压 `zhanhBlueB-Windows-1.0.1-win-x64.zip`，运行其中的 `zhanhBlueB.exe`。
3. 手机点击“停止连接”，再选择已配对电脑连接。
4. 两端出现信任提示时核对设备并确认；已有信任信息可自动通过。

无需解除系统配对。新包仍自带 .NET/WPF 运行时。

新增回归测试直接编译生产 `BluetoothHost`，用遵循上述库行为的测试替身验证
接收路径：已配对手机的 wrapper 缓存为 false 时仍可进入协议会话；未配对设备、
不要求认证/加密的监听器和安全设置失败仍被拒绝。测试先在旧代码下复现失败，
再验证修复。实际手机重连、首次信任及完整传输结果仍须在更新后核实。
