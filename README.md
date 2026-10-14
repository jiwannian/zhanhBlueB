# zhanhBlueB

Android 与 Windows 的蓝牙剪贴板、图片和文件互传工具。日常连接只使用
Bluetooth Classic RFCOMM，不需要网络、账号或服务器。

下载签名 APK 和自带运行时的 Windows 程序：[GitHub Releases](https://github.com/jiwannian/zhanhBlueB/releases/latest)。
制作过程、连接故障和图片打开问题的复盘见 [工程经验](docs/engineering-lessons.md)。

## 支持环境

- Android 10 及以上（API 29+），首要目标设备为 vivo X300。
- Windows 11 x64，配有支持 Bluetooth Classic 的蓝牙适配器。
- 两端都需要安装 zhanhBlueB，并先在系统蓝牙设置中完成配对。

## 使用

当前 Windows 修复版为 **1.0.2**，Android 为 **1.0.0**。1.0.2 增加传输记录双击打开已接收文件；1.0.1 修复已配对手机
被错误判定为“没有通过蓝牙认证与加密检查”的问题。手机 APK 无需重装；
升级电脑端前先从旧版托盘菜单“退出”，完整解压新版并运行。手机停止旧连接后
重新连接电脑，再在两端确认信任。修复依据和验证见 [连接修复说明](docs/connection-fix.md)。
双击打开的操作说明与限制见 [图片和文件打开修复](docs/file-opening-fix.md)。

1. 将 Windows 压缩包完整解压，运行 `zhanhBlueB.exe`；程序自动开启蓝牙监听。
2. 在手机安装 `zhanhBlueB-Android-1.0.0.apk`，打开后点击“刷新设备”。
   Android 12 及以上需要授予“附近设备”权限；Android 13 及以上建议允许通知。
3. 手机选择已配对电脑，点击连接；电脑应显示“等待手机连接”。
4. 首次连接，在两端核对设备名称、蓝牙地址并确认信任。之后同一配对可自动接收。
5. 连接完成后，电脑复制的纯文本、截图等剪贴板图片会自动发送到手机。
   可以随时暂停“自动发送”；断线时不发送，也不在重新连接后补发旧剪贴板。
6. 手机点击“发送剪贴板”；通知快捷入口会打开发送页面，获得焦点后再读取内容。
   也可从其他应用的分享菜单发送文字或单张图片。
7. 文件通过电脑选择、多选、拖入，或手机选择、多选、系统分享发送。
   查看进度，可取消当前传输或重试失败文件。
8. Windows 传输页双击已完成记录可打开文件；电脑剪贴板图片发送成功后会保存一份 PNG 副本，便于稍后打开。

Windows 关闭窗口会保留托盘程序；使用托盘的“退出”结束运行。
手机在连接期间显示前台服务通知，点击停止后不再自动重连。
开机启动默认关闭。

## 剪贴板与文件

- 手机主动发送采用手动方式，保留现有输入法，不需要 Shizuku、Root 或无障碍权限。
- 文字按纯文本传输，保留中文、表情、换行和空白，不同步富文本格式。
- 剪贴板图片转换为 PNG。接收后可粘贴到支持图片剪贴板的应用，也可另存。
  某些安卓应用只支持分享图片、不支持从第三方图片剪贴板粘贴，应使用保存或分享入口。
- 文件保留原始字节，完成 SHA-256 校验后才保存。同名文件自动重命名，不覆盖已有文件。
- 默认保存位置为系统下载目录中的 `zhanhBlueB` 文件夹。接收文件不会自动打开。
- 断连后可以从头重试当前失败文件；已完成文件保留。首版不提供断点续传、文件夹发送。
- 文字上限 1 MiB；剪贴板 PNG 上限 32 MiB、3200 万像素。普通文件分块传输，主要面向中小文件。

## 源码与验证

`windows/` 包含桌面程序、共同协议的 C# 实现和测试；`android/` 包含安卓程序、
独立 Kotlin JVM 核心和测试。两端协议见 [docs/protocol.md](docs/protocol.md)，
共用编码样本为 `protocol/golden.json`。

跨语言测试直接连接生产核心测试进程的标准输入、标准输出，不开放网络端口：

```powershell
pwsh -File tools/verify.ps1
```

该脚本构建两端测试进程并生成本机路径配置，再运行核心单测、覆盖率检查、
跨语言测试、异常输入检查与 Windows 界面渲染。完整签名与打包入口为
`tools/build-release.ps1`，环境和签名配置见 [docs/build.md](docs/build.md)。

实际测试结果、构建命令和未完成的真机验收项目见 [docs/verification.md](docs/verification.md)。
依赖下载发生在构建时，运行时不需要联网；Android APK 不申请 INTERNET 权限。
测试工具的自动信任模式仅用于本地测试，不包含在正式 APK 或 Windows 界面程序中。
内部协议和源码命名空间沿用 BlueBridge；应用名称和下载目录为 zhanhBlueB。

## 依赖与来源

蓝牙传输复用 [32feet.NET](https://github.com/inthehand/32feet)，连接方式参考
[ClipSync](https://github.com/aubynsamuel/clipsync-android)。Android 使用 Kotlin、
Jetpack Compose 和 Gson；Windows 使用 .NET 与 WPF。第三方说明见
[docs/third-party.md](docs/third-party.md)。

本项目自有源码按 [MIT License](LICENSE) 开源。第三方组件、附带源码和通知保留各自许可，
其中旧版 32feet.NET 的历史许可与对应官方源码一并保留；项目 MIT 许可不替代第三方许可。
