# Windows 验证记录

此目录的原始日志、TRX、覆盖率和截图仅在开发机保留，已排除出公开仓库及源码包。
下文路径用于定位历史本机证据；重新生成验证报告请在项目根目录运行
`pwsh -File tools/verify.ps1`。当前 1.0.2 结果和用户反馈以
[完整验证记录](../../docs/verification.md) 为准。

更新日期：2026-10-14。环境：Windows 11 x64，.NET SDK 10.0.302 / Runtime 10.0.10。

## Windows 1.0.1 连接修复

- 旧版错误 `该连接没有通过蓝牙认证与加密检查` 源于读取 32feet 3.5 accepted-client 的新包装器缓存；两个 getter 初始均为 `false`，不是原生链路状态。已移除错误断言，保留原生安全监听选项、接入前监听配置检查、系统配对及双方应用信任确认。官方 32feet 源码归档位于项目 `docs/third-party-source/32feet-3.5-source.zip`；安全依据见[微软蓝牙套接字选项文档](https://learn.microsoft.com/en-us/windows/win32/bluetooth/bluetooth-and-socket-options)。
- TDD RED：旧生产代码下已配对设备、默认 false 包装器缓存回归失败（1 失败 / 0 通过），保留 `bluetooth-1.0.1/accepted-wrapper-red.log` 和 `accepted-wrapper-red-recorded.trx`；最初报告 `accepted-wrapper-red.trx` 同时保留。
- GREEN：修复后 8 项蓝牙接入用例通过，0 失败。测试直接编译链接生产 `BluetoothHost.cs`；适配器覆盖监听选项强制设置、已配对设备到达 HELLO、未配对拒绝、监听选项失效拒绝、设置原生选项失败及一台手机限制。报告为 `bluetooth-1.0.1/bluetooth-green.log`、`bluetooth-green.trx`。
- 核心测试重新运行：40 通过 / 0 失败，生产核心行覆盖率 97.26%（284/292）、分支覆盖率 85.39%（263/308）；日志 `bluetooth-1.0.1/core-1.0.1.log`，TRX `bluetooth-1.0.1/core/core-1.0.1.trx`，Cobertura `bluetooth-1.0.1/core/41550660-3134-45d2-98c2-aeb87ee14670/coverage.cobertura.xml`。蓝牙接入套件中的适配器不代表真实无线链路。
- 标准 SDK 独立发布到 `artifacts/1.0.1/win-x64`，保留旧版 `publish`。显式版本、程序集版本、文件版本分别为 `1.0.1`、`1.0.1.0`、`1.0.1.0`，日志 `bluetooth-1.0.1/desktop-publish.log`；发布编译无警告与错误，实际依赖的 core/WPF 运行库已经缓存，因此使用 `EnableRuntimePackDownload=false`。
- 本次未启动新 GUI、未停止用户进程、未覆盖已安装程序，未读取真实剪贴板或连接手机。Windows 1.0.1 在当前电脑与 vivo X300 的重连、双方信任和双向互传结果待用户真机复测。

## 初版 1.0.0 验证记录（2026-10-13）

- Release 核心自动化测试：40 项通过，0 项失败；生产核心行覆盖率 97.26%（284/292），分支覆盖率 85.39%（263/308）。最新 Cobertura 报告在 `test-results/4ad1cd95-e85f-41f8-a03e-6158542e419d` 子目录。
- 测试覆盖共同 golden 帧、17 字节拆包读流、UUID 网络字节序、头/长度/UTF-8/JSON 校验、双端信任、未信任拒绝、双向大文件、空文件、同名不覆盖、错误确认不能完成发送、校验失败清理、取消迟到文件块、重复活跃 BEGIN 不破坏原文件、图片应用失败清理、断连失败与剪贴板单次抑制。
- 使用本机蓝牙栈运行 `--bluetooth-smoke`，返回码 0，输出 `SECURE_LISTENER_START_STOP_OK`。32feet.NET 3.5.0.3 的安全 RFCOMM 监听成功启动和停止；无设备连接，无用户剪贴板读取或发送。
- WPF Release 编译 0 警告 / 0 错误。客户端 UI 已实际渲染检查，截图 `client-ui.png`，文字及主要操作无遮挡。
- 正式显示名称、可执行文件、本地设置目录、下载子目录均为 `zhanhBlueB`；源码工程和协议技术标识保持 `BlueBridge`，不影响互通。
- 原生剪贴板序号用于过滤本机应用收到内容后产生的通知；暂停时仍消费事件，新一次用户复制相同内容仍发送，不依赖 PNG 重编码结果。
- 握手等待 HELLO 最多 30 秒，信任确认最多两分钟；超时取消会话并自动关闭非模态信任窗口，未完成的确认不会占用唯一连接。
- `dotnet list BlueBridge.Desktop/BlueBridge.Desktop.csproj package --vulnerable --include-transitive`：当前 NuGet 源没有报告易受攻击包。
- 自包含 Windows x64 发布完成：`publish/zhanhBlueB.exe`，运行库包含 Microsoft.NETCore.App 和 Microsoft.WindowsDesktop.App 10.0.10，共约 163 MB（未压缩）。不存在旧名称 `BlueBridge.exe`。
- 正式产物在子进程设置 `DOTNET_MULTILEVEL_LOOKUP=0` 后运行 `--bluetooth-smoke`，返回码 0，输出 `SECURE_LISTENER_START_STOP_OK`；已检查本目录包含 coreclr、hostfxr、hostpolicy、WPF 原生依赖。
- 官方 .NETCore runtime NuGet 包使用分段下载处理慢连接，ZIP CRC 和 `dotnet nuget verify --all` 均通过，验证了 Microsoft 作者与 NuGet 仓库签名。发布由标准 SDK 执行，已缓存实际需要的运行库后使用 `EnableRuntimePackDownload=false` 禁止 SDK 额外预下载未引用的 ASP.NET 运行库；发布包没有 ASP.NET 框架引用。
- 跨语言管道及恶意协议注入测试由仓库根目录工具执行，使用同一生产核心和独立 HeadlessPeer；完整结果以项目根验证文档为准。

尚未验证真实 Android 10/12/13/16 手机连接、vivo X300、真实蓝牙链路的吞吐/后台行为，以及所有目标应用的 PNG 粘贴兼容性。离线协议管道测试和本机监听启动证明不能代替双设备蓝牙真机验收。

`--render-ui <PNG路径>` 只渲染空界面，不开启蓝牙或剪贴板监听，不模拟连接。正式桌面程序没有自动信任开关。
