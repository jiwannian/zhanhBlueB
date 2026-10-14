# zhanhBlueB 验证记录

首版验证日期：2026-10-13；连接修复、文件打开功能及 Windows 记录菜单：2026-10-14。当前交付版本：Windows 1.0.3 / Android 1.0.1（versionCode 2）。构建环境为本机 Windows 11 x64、
.NET SDK 10.0.302 / Runtime 10.0.10、JDK 21、Android SDK 36。

## Windows 1.0.3 传输记录右键菜单

每条记录独立显示“打开”“删除记录”“删除记录与本地文件”。菜单从右键目标取得编号，
执行时重新查找当前记录；空白处没有文件菜单。“打开”复用双击逻辑，仅已完成且有本地路径的
记录可用。路径优先使用最终 `SavedPath`，其次为发送源 `Source`，因此重名接收和图片副本
均指向实际落盘文件。普通发送文件的删除确认框显示完整路径、原文件说明，默认取消。

仅删除记录不触碰文件。删除文件采用独立 STA 线程上的 Windows Shell `IFileOperation`，
只允许回收，拒绝永久删除。回收成功才删除选中记录；文件不存在时移除记录并提示；取消或
失败保留记录。传输中和资源收尾期间不可删除；确认后再次检查同路径的活动任务，回收期间
阻止同路径新发送和重复删除。共用文件的其他记录保留传输结果，但标记本地文件已移除，
禁用打开和文件重试；受影响的“图片另存为”来源同步失效。删除记录释放图片重试缓存，
迟到进度不会重建记录。新增 `ReceiveSettled` 仅用于本地清理通知，蓝牙帧与 Android 不变。

| 本次检查 | 实际结果 | 证据 |
| --- | --- | --- |
| TDD 失败阶段 | 菜单操作与接收清理测试在实现前失败 | `artifacts/windows-1.0.3-tdd-red.log`、`artifacts/windows-1.0.3-settlement-red.log` |
| Windows 生产核心 | 44 项通过；行 290/298 = 97.31%，分支 85.98% | `artifacts/windows-1.0.3-verification.log`、`artifacts/coverage-1c2c250df63c458e8d87d93d5235a39e/7f58db3b-a90c-4e72-81e5-cd85db853310/coverage.cobertura.xml` |
| 蓝牙接入回归 | 8 项通过 | 同一验证日志及该覆盖率目录的 `bluetooth/` |
| 桌面纯逻辑 | 43 项通过；整体行 80/83 = 96.38%，新增 `TransferRecordActions` 96.42%，`TransferRow` 83.33% | 同一验证日志及该覆盖率目录的 `desktop/`；验证脚本对新增类执行 80% 行门槛 |
| WPF 菜单集成 | 右键非选中项、独立菜单目标、打开/删除/取消/失败、忙碌保护、缓存释放、相关记录、迟到进度及空白区域通过 | `artifacts/windows-1.0.3-ui-final.log`、`tools/DesktopUiCheck/RecordMenuChecks.cs` |
| 真实回收与恢复 | 独立临时文件经真实 `IFileOperation` 回收，恢复后字节一致；缺失文件、目录和占用文件保护通过 | 同一 UI 日志、`tools/DesktopUiCheck/RecycleBinChecks.cs`；未使用用户文件 |
| 既有桌面行为 | 双击、文本/内联事件、发送源、PNG 副本、保存失败及标准/最小窗口离屏检查通过 | 同一 UI 日志、`artifacts/windows-1.0.3-ui-final/` |
| 跨语言传输 | 3 场景通过，含拆帧、中文/表情/图片/同名/空文件及双向各 100 MiB SHA-256 校验 | `artifacts/windows-1.0.3-cross-language-retry2.log`、`artifacts/e2e/report.json` |
| 协议异常 | 7 情形 × 2 端共 14 项独立重跑通过 | `artifacts/windows-1.0.3-protocol-retry2.log`、`artifacts/adversarial/report.json` |
| 自包含发布构建 | 标准 win-x64 发布成功，运行时为 .NET Core/WindowsDesktop 10.0.10 | `artifacts/windows-1.0.3-publish-final.log`、发布目录的 `zhanhBlueB.runtimeconfig.json` |
| 交付包检查 | SHA-256、APK/ZIP CRC、源码私钥/缓存排除和运行时完整性通过；解压后的 EXE 安全监听启动/停止返回 0 | `artifacts/release/1.0.3/package-verification.json`、`artifacts/windows-1.0.3-package-check.log` |

完整 `tools/verify.ps1` 本次未单次全部通过：跨语言子进程返回 `3221225477`，
随后 Python 输出遇到 GBK 编码错误，见 `artifacts/windows-1.0.3-verification.log`。
之后跨语言三个场景和协议异常十四项分项通过，WPF 检查单独通过；不能将这些结果写成
完整脚本单次通过。ReadyToRun 发布两次出现 `crossgen2.exe` 返回 `-1073741819`，
最终交付改为标准自包含发布；没有确认这些间歇进程退出的底层原因。

正式交付位于 `artifacts/release/1.0.3`，包校验结果见该目录的 `package-verification.json`。
Android APK 复用既有 1.0.1 签名文件，SHA-256 保持下节所列值；没有更改蓝牙协议。
本机 WPF 事件和临时文件回收检查不等于真机收发后的人工鼠标验收；本次未新增安卓设备或
蓝牙双端真机验收结果。

## Android 1.0.1 接收文件打开与历史记录

普通文件完成 SHA-256 校验、MediaStore 保存和发布后，记录实际 `content://` URI、
系统最终文件名、MIME、大小、完成时间及传输编号。接收记录显示“打开”，每次均使用
`ACTION_VIEW` 与 `Intent.createChooser`，通过 `ClipData` 和临时只读授权交给用户选择的应用。
具体 MIME 无匹配应用时尝试 `*/*`；不存在或无权读取的文件显示中文提示。
选择器打开期间禁用打开按钮，取消或失败不改变传输状态。

最近 100 条成功接收文件记录以版本化 JSON 保存在应用私有目录，通过 `AtomicFile`
串行读写；历史和当前记录按编号合并。记录裁剪不删除文件，历史写入失败保留当前记录并提示。
剪贴板图片不进入此历史；升级前的下载文件不扫描导入。Windows 1.0.2 和蓝牙协议未修改。

| 本次检查 | 实际结果 | 证据 |
| --- | --- | --- |
| TDD 失败阶段 | 新增历史/MIME 与记录合并测试在实现前因缺失符号失败 | `artifacts/android-open-red.log`、`artifacts/android-open-ui-red.log` |
| Kotlin 核心与流集成 | 29 项通过；整体行 280/281 = 99.64%，分支 323/387 = 83.46% | `android/core/build/test-results/test/`、`android/core/build/reports/jacoco/test/jacocoTestReport.xml` |
| 新增核心纯逻辑 | 11 项测试覆盖去重、排序、100 条裁剪、重启恢复、损坏/失败写入、并发初始化及 MIME 回退；68/68 行 = 100% | `ReceivedFilesTest`、上述 JaCoCo 报告；新增类分别执行 80% 行门槛 |
| 应用 JVM 测试 | 4 项通过（权限 1 项、记录合并/打开资格 3 项）；新增页面纯逻辑 4/4 行 = 100% | `android/app/build/reports/tests/testDebugUnitTest/`、`android/app/build/reports/jacoco/receivedUiCoverageReport/` |
| 跨语言回归 | 3 场景通过，含中文/表情/多行、PNG、同名和空文件、拆帧、双向各 100 MiB SHA-256 核对 | `artifacts/android-1.0.1-cross-language-final.log`、`artifacts/e2e/report.json` |
| 协议异常回归 | 对原有 `run_case` 加入失败诊断后独立运行，7 情形 × 2 端共 14 项全部通过 | `artifacts/android-1.0.1-protocol-diagnostic.json`、同名 `.log` |
| 签名 release、debug、测试 APK 与 lint | 全部构建成功；lint 0 错误、1 条原有备份规则兼容提示 | `artifacts/android-1.0.1-release-build-final-retry.log`、`artifacts/android-1.0.1-platform-build-final.log` |
| APK 升级签名与元数据 | 包名 `com.zhanh.bluebridge`，版本 1.0.1 / 2，min 29、target/compile 36；v2 签名验证通过，与原 1.0.0 证书一致 | `artifacts/android-1.0.1-apksigner-final.txt`、`artifacts/android-1.0.1-badging-final.txt` |
| Android 平台和 Compose 测试 | 新增 5 项，连同既有 3 项共 8 项已编译；未在设备执行 | `ReceivedFilePlatformTest`、`ReceivedFileUiTest`、`PlatformTest`；`artifacts/android-1.0.1-connected-device-tests.log` |

APK 签名证书 SHA-256：`095d4b1ba35eb1876d20e039247143bde03fd49c77afa8bdd7cfcbc3027e5545`。
APK SHA-256：`413cc4a83c526f2df6184e6174e8b5edf3e492fe4537a6afaf2842e3b7b109f1`。
本次独立交付位于 `artifacts/release/android-1.0.1`，包含最新签名 APK、完整源码、中文说明和校验清单。

完整 `tools/verify.ps1` 的单次运行未全部通过：跨语言阶段的子进程曾返回
`3221225477`，随后 Python 的 GBK 错误输出失败；签名构建的 JVM 测试进程也曾返回
`-1073741819`。降低 Gradle 并发后构建和测试通过，跨语言三个场景独立重跑通过。
协议异常脚本曾在不同用例报告 `peer stdout closed`，最终以相同生产测试函数及附加失败诊断
独立运行全部 14 项通过。保留初始失败日志，不声称已确定这些间歇退出的原因，也不把
分项通过写成完整脚本单次通过。

当前 `adb devices -l` 无已连接设备，`connectedDebugAndroidTest` 因无设备未执行测试。
API 29、31、33、36 与 vivo X300 的系统选择器、外部应用实际读取、重复点击、取消后再次打开、
删除/失效授权、重启恢复及真实蓝牙回归均待设备验收。图片、PDF、文本、未知扩展名、中文文件名、
同名和空文件的真实外部应用兼容性不得从编译或纯逻辑测试推断。

## 已执行的自动化检查

下表保留 Windows 1.0.2 / Android 1.0.0 的既有基线；当前 Windows 1.0.3 和 Android 1.0.1 结果以上表为准。

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

Windows 1.0.2 交付使用 win-x64 自包含 ReadyToRun 发布，运行时为 .NET 10.0.10，
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
或 Windows 发布目录。APK 不申请 INTERNET 或全盘存储权限。Android 1.0.1 的文件打开只新增
`ACTION_VIEW` 包可见性查询，没有新增运行时或存储权限。

Android 已显式禁用备份并排除 cloud/device-transfer，AGP 8.9 lint 仍产生一条
兼容提示；本次保留该提示，没有把它计作错误。生产 APK、debug APK 和包含
既有 3 项及本次新增 5 项 instrumentation 检查的测试 APK均已成功编译。

## 用户反馈与待完成的真机验收

2026-10-14 用户反馈连接和功能正常，所附截图显示电脑已连接 `V2509A`，
手机发送的约 299.4 KB JPG 文件已完成接收。该反馈支持 1.0.1 连接修复已经可用，
但不等于下列完整验收。Windows 1.0.2 的图片打开修复已通过单元及 WPF 事件集成检查；
实际鼠标双击、Windows 默认图片应用启动和真机接收后打开仍需在新版本验证。
本次未通过 adb 或模拟器运行 Android instrumentation 测试。以下成功结果不能仅从核心
测试、本机监听或编译成功推断：

- vivo X300 与当前电脑的实际加密 RFCOMM 双向收发、首次双端确认及自动重连。
- Android API 29、31、33、36 的真实权限弹窗、系统分享和界面流程；8 项
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
7. 手机覆盖安装 Android 1.0.1 后，由电脑选择或拖入 JPG/PNG、PDF、文本、未知扩展名、
   中文/表情文件名、同名和空文件。逐条点击“打开”，确认每次均出现系统选择器，外部应用读取
   正确副本；取消后可再次打开，重启 APK 或断开蓝牙仍保留历史。删除一个文件后仅提示，
   原成功状态不变。验证剪贴板图片没有新增自动保存或历史。
8. Windows 1.0.3 右键非选中记录，确认操作目标正确；仅删除记录后核对电脑/手机文件均保留。
   用自行创建的临时文件测试“删除记录与本地文件”，核对完整路径、发送原文件说明和默认取消，
   确认后在回收站恢复；检查共用文件记录失效、活动任务保护和既有取消/重试流程。

接收的图片、文件可能占用下载目录空间；收到内容后不会自动打开文件。
断连的当前文件从头重试，已完成文件保留。
