# zhanhBlueB 构建说明

源码目录保持 `bluebridge`；产品名为 `zhanhBlueB`。内部包名
`com.zhanh.bluebridge`、C# 命名空间和协议 UUID 保持固定。

## 环境

- Windows 11 x64、.NET SDK 10；本次构建 SDK 10.0.302，运行时 10.0.10。
- JDK 21；请将 `JAVA_HOME` 或脚本参数指向本机 JDK 21 路径。
- Android SDK Platform 36、Build Tools 36.0.0；请设置 `ANDROID_HOME` 指向本机 SDK。
- Python 3、PowerShell 7；Gradle Wrapper 8.11.1 已包含在源码中。

首次构建需要下载 NuGet/Maven/Gradle 依赖；缓存齐备后运行程序不需要联网。
设置 `ANDROID_HOME` 为 SDK 路径，或在 `android/local.properties` 中配置
`sdk.dir=<Android SDK 路径>`。该文件属于本机配置，不进入源码包。

## 自动化验证

在项目根目录运行：

```powershell
pwsh -File tools/verify.ps1 -JavaHome '<JDK 21 路径>'
```

此入口运行 C# 与 Kotlin 生产核心测试，检查两端核心行覆盖率至少 80%，
构建独立测试进程并生成 `artifacts/peer-commands.json`，运行双向管道传输、
异常协议输入检查以及实际 WPF 界面离屏渲染。它不连接真实手机。

Android instrumentation 另外需要真机或模拟器。运行前连接 adb 设备：

```powershell
cd android
.\gradlew.bat :app:connectedDebugAndroidTest
```

## 签名与正式打包

Android release 配置从环境变量读取：

- `BLUEBRIDGE_KEYSTORE`：私有 JKS 的绝对路径。
- `BLUEBRIDGE_STORE_PASSWORD`、`BLUEBRIDGE_KEY_PASSWORD`：对应口令。
- 当前固定别名为 `bluebridge`。

也可在本机 `.secrets/android-signing.json` 提供 `keystore`、`storePassword`、
`keyPassword` 三项，由 Android 构建脚本读取。私钥和该配置已被排除出交付包，
不要将其上传到版本库。交付 APK 已签名；以后覆盖升级须继续使用同一私钥。
本次私钥只保留在本机 `.secrets/android-release.jks`，应自行妥善备份。

配置签名后在根目录运行：

```powershell
pwsh -File tools/build-release.ps1 -JavaHome '<JDK 21 路径>'
```

结果位于 `artifacts/release/1.0.3`：Android 1.0.1 签名 APK、Windows 1.0.3 自带运行时 ZIP、
源码 ZIP、中文说明及 `SHA256SUMS.txt`。Windows 发布使用新目录，避免旧文件混入。
Windows EXE 尚无商业代码签名证书。

Windows 1.0.3 使用标准自包含发布，不启用 ReadyToRun。本机构建的 crossgen2 曾两次异常退出；
标准发布成功，交付仍包含 .NET Core 和 WPF 运行时，无需另外安装 .NET。

`tools/build-release.ps1` 的 `-Version` 与 `-AndroidVersion` 分别指定 Windows/
源码版号和 APK 版号，默认 1.0.3 / 1.0.1。本次 Windows 增加传输记录右键菜单，Android 保持 1.0.1。
单独更新手机可运行 `android/build-release.ps1`，APK 生成于
`android/app/build/outputs/apk/release/app-release.apk`，无需重新构建 Windows。
本次独立交付位于 `artifacts/release/android-1.0.1`。

打包入口还会运行 `tools/check_release.py`：检查 SHA-256、ZIP CRC、源码与运行时
完整性、私钥排除、正式程序无测试进程，再将 Windows 包解压至临时目录，直接
运行包内 EXE 启动/停止安全蓝牙监听。此检查需要本机支持蓝牙；在不具备蓝牙的
构建机上可单独运行 `python tools/check_release.py --skip-listener` 完成静态包校验。

## 依赖选择

Windows 使用 `32feet.NET 3.5.0.3` 的原生 Win32 RFCOMM 实现，可显式要求
`Authenticate=true` 与 `Encrypt=true`，在接收时复核监听安全配置与系统配对，
随后执行应用信任确认；不使用 accepted-client 的缓存属性推断真实链路状态。
这是旧 .NET Framework 兼容资产；本机 .NET 10 下安全监听启动、停止测试通过。
真实 Android 双端连接仍须验收，详见 `verification.md`。

本次 SDK 会尝试下载未引用的 AspNet Runtime Pack，因此在实际 Core 和 WPF
官方运行时包已经完整缓存后，发布命令额外传入
`-p:EnableRuntimePackDownload=false`。该选项只阻止额外下载；最终交付仍由标准
SDK 生成，且包内运行时配置只有 Core 和 WindowsDesktop。新环境应先正常
还原实际运行时依赖，再按需使用此选项。

Android 使用 AGP 8.9.0、Kotlin 2.1.0、Compose BOM 2025.02.00。
compile/target SDK 均为 36，minimum 为 29。AGP 对 compile SDK 36 的推荐版本提示
被单独抑制；本次 APK、测试 APK 和 lint 实际构建成功。lint 的备份规则兼容提示
保留在验证记录中。
