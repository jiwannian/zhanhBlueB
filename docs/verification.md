# zhanhBlueB 验证记录

首版验证日期：2026-10-13；连接修复：2026-10-14；文件打开修复：2026-10-14。当前交付版本：Windows 1.0.2 / Android 1.0.0。构建环境为本机 Windows 11 x64、
.NET SDK 10.0.302 / Runtime 10.0.10、JDK 21、Android SDK 36。

## 已执行的自动化检查

| 检查 | 实际结果 | 本地报告 |
| --- | --- | --- |
| C# 生产核心单元与流集成测试（1.0.2 重跑） | 40 项通过，0 失败；行 284/292 = 97.26%，分支 263/308 = 85.39% | `artifacts/coverage-d4623c81736b40799028edd75cc7b2d2/f8436e32-d72b-432f-a1f3-c67e90d7731e/coverage.cobertura.xml` |
| Windows 蓝牙接入修复回归（1.0.2 重跑） | 8 项通过，0 失败；保留 1.0.1 的安全监听和接入修复 | `artifacts/desktop-1.0.2/bluetooth/bluetooth-green.trx`；旧版失败回归见 `windows/validation/README.md` |
| Windows 文件打开与 PNG 保存单元测试 | 23 项通过，0 失败；文件路径、默认程序参数、失败状态保护、PNG 校验、唯一命名、取消清理 | `artifacts/desktop-1.0.2/final/file-open-green.trx` |
| Windows WPF 双击事件集成 | 真实 WPF 项目与每项容器的文本/内联文字双击、成功发送图片本地副本、按钮/右键排除、缺失文件提示通过 | `artifacts/desktop-1.0.2/ui-green-result.txt` |
| Kotlin 生产核心单元与流集成测试 | 18 项通过，0 失败；行 212/213 = 99.53%，分支 276/331 = 83.38% | `android/core/build/reports/jacoco/test/jacocoTestReport.xml` |
| Android 权限策略 JVM 测试 | 1 项通过，含 API 29、31、33、36 分支断言 | `android/app/build/reports/tests/testDebugUnitTest/index.html` |
| 共用帧样本 | 4 份跨语言 golden 样本通过 | `protocol/golden.json` |
| C# ↔ Kotlin 端到端传输 | 3 场景通过 | `artifacts/e2e/report.json` |
| 异常协议注入 | 7 种情形 × 2 个端点，共 14 项通过 | `artifacts/adversarial/report.json` |
| 实际 WPF 界面离屏渲染 | 默认界面、传输记录、最小窗口尺寸通过 | `artifacts/desktop-1.0.2/ui-green/windows-*.png` |
| 本机安全蓝牙监听 | 启动、停止成功，输出 `SECURE_LISTENER_START_STOP_OK` | Windows 程序 `--bluetooth-smoke` |
| Android release 构建与签名 | APK v2 验证通过，标签 zhanhBlueB，min 29 / target、compile 36 | `android/app/build/reports/apksigner-verification.txt` |
| Android lint | 0 错误，1 条 DataExtractionRules 兼容提示 | `android/app/build/reports/lint-results-debug.html` |
| NuGet 已知漏洞检查 | 首版查询未报告易受攻击依赖；本次未增加产品依赖 | Windows 依赖检查输出 |
| 正式交付包检查 | SHA-256、ZIP CRC、私钥排除、运行时完整性通过；解压后的 Windows EXE 安全监听启动/停止返回 0 | `artifacts/release/1.0.2/package-verification.json` |

覆盖率对象为两端生产传输核心；不包含平台 UI、真实蓝牙驱动和仅用于测试的
HeadlessPeer。源码中的验证脚本检查两端核心行覆盖率最低 80%。
Windows 文件打开与图片保存独立单测行覆盖率为 88%；WPF 双击事件以隔离桌面渲染宿主检查，
不启动产品托盘、蓝牙监听、剪贴板侦听或调用真实默认应用。
源码包不附本机缓存或大体积测试数据，报告可通过 `tools/verify.ps1` 重新生成。
本机旧 Python 3.10 在本次复测中出现进程访问冲突；改用本机已有的 Codex 工具运行时 Python
后，跨语言三个场景及异常协议十四项全部复测通过。该运行时差异不影响正式 APK/Windows 程序，
二者都不依赖 Python。

Windows 交付使用 win-x64 自包含 ReadyToRun 发布，运行时为 .NET 10.0.10，
运行时配置仅包含 .NETCore 和 WindowsDesktop。微软 Core 运行时 NuGet 包
完成 CRC 和 NuGet 作者/仓库签名验证。1.0.2 发布目录共 476 文件（含子目录），压缩包排除 PDB。

跨语言测试用两个独立进程通过操作系统管道双向连接同一生产协议核心，
未使用 TCP、Wi-Fi、蓝牙射频或服务器。内容场景覆盖中文、表情、多行、空白及
空字符串、PNG、多文件、空文件、同名文件重命名和已有文件不覆盖；拆帧场景将
数据按 17 字节分段交付；大文件场景双向各传输 100 MiB 并核对 SHA-256。
管道传输的耗时不能作为蓝牙吞吐指标。

异常检查包括未信任时发送文字、超大文件块、非法路径、SHA-256 不符、
取消后迟到文件块、已完成编号重复发送，以及正在接收时收到重复 BEGIN。
核心测试还覆盖截断头、UTF-8/JSON 类型错误、错误 ACK、发送读取失败、
断连清理、剪贴板本地写入与暂停去重、初始握手期限和取消未结束的信任回调。

Windows 要求系统已配对、RFCOMM 认证和加密，不满足条件即关闭连接。
认证/加密由监听 socket 上的原生设置强制；接收 client 的缓存 getter 不用于判断
链路状态。1.0.0 在此处的误判已由 1.0.1 修复，不把首版监听启动成功当作连接成功。
收到 HELLO 前最多等 30 秒，应用信任确认最多等 2 分钟；超时会释放连接位置。
传输记录中的已完成文件支持双击打开，使用接收后的最终路径（包含同名文件自动重命名结果）；
电脑剪贴板图片成功发送后也保存一份经 PNG 和 SHA-256 验证、同名安全重命名的本地副本；
若该额外副本因磁盘或权限问题无法保存，发送成功状态仍保留并提示原因。
文件已被移动、删除或没有默认应用时只显示提示，不执行不安全的 shell 命令。
安全复核确认 Android 分享入口只接受 `content://`；FileProvider 只暴露本应用
图片目录，服务和通知发送 Activity 不导出。测试自动信任进程不进入正式 APK
或 Windows 发布目录。APK 不申请 INTERNET 或全盘存储权限。

Android 已显式禁用备份并排除 cloud/device-transfer，AGP 8.9 lint 仍产生一条
兼容提示；本次保留该提示，没有把它计作错误。生产 APK、debug APK 和包含
3 项 instrumentation 检查的测试 APK均已成功编译。

## 用户反馈与待完成的真机验收

2026-10-14 用户反馈连接和功能正常，所附截图显示电脑已连接 `V2509A`，
手机发送的约 299.4 KB JPG 文件已完成接收。该反馈支持 1.0.1 连接修复已经可用，
但不等于下列完整验收。Windows 1.0.2 的图片打开修复已通过单元及 WPF 事件集成检查；
实际鼠标双击、Windows 默认图片应用启动和真机接收后打开仍需在新版本验证。
本次未通过 adb 或模拟器运行 Android instrumentation 测试。以下成功结果不能仅从核心
测试、本机监听或编译成功推断：

- vivo X300 与当前电脑的实际加密 RFCOMM 双向收发、首次双端确认及自动重连。
- Android API 29、31、33、36 的真实权限弹窗、系统分享和界面流程；3 项
  instrumentation 检查已编译但未运行。
- 关闭手机 Wi-Fi/移动数据及电脑网络后的完整双端操作。
- 手机后台接收与厂商省电策略、Windows 托盘运行下的真实剪贴板同步。
- 截图在各目标应用中的直接粘贴兼容性、真实剪贴板无回传循环。
- 真实射频环境下的多文件和 100 MiB 双向传输、蓝牙关闭、权限拒绝、
  存储空间不足、取消及失败重试。

## 真机验收步骤

1. 安装两端产品，在系统中配对。电脑运行 `zhanhBlueB.exe`，手机刷新设备并
   连接；两端核对地址和名称后确认信任。
2. 关闭手机 Wi-Fi、移动数据并断开电脑网络，保持蓝牙打开。双向发送中文、
   表情、多行文字和截图，确认可粘贴且不会反复回传。
3. 双向发送多文件、两个同名文件、空文件和 100 MiB 文件；检查进度、
   `Downloads/zhanhBlueB` 下的文件名、大小和 SHA-256。在 Windows 双击已完成 JPG/PNG
   及普通文件记录，检查系统默认程序打开的是正确文件，特别是重名后的文件。
4. 传输中取消，检查临时文件清理，再重试；关闭蓝牙后恢复，检查自动重连。
   点击手机“停止连接”后确认不会继续重试。
5. 将 Windows 隐藏到托盘、手机切换至后台，验证持续接收；测试通知发送入口。
6. 在四个 API 版本检查权限允许/拒绝，测试文件提供方失效和真实磁盘不足，
   将设备系统版本、应用名称、结果和限制记录在本文件后续版本。

接收的图片、文件可能占用下载目录空间；收到内容后不会自动打开文件。
断连的当前文件从头重试，已完成文件保留。
