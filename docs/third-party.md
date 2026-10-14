# 第三方依赖

项目根目录的 MIT License 仅适用于项目自有源码。下列依赖、附带的第三方源码和
许可/通知按其原始许可分发，不由项目 MIT License 重新授权。

| 组件 | 用途 | 许可与来源 |
| --- | --- | --- |
| 32feet.NET 3.5.0.3 | Windows Bluetooth Classic RFCOMM | 历史 In The Hand Community License；官方 3.5 标签亦附 MIT 文本，https://github.com/inthehand/32feet |
| .NET、WPF | Windows 自包含运行时及界面 | MIT，https://github.com/dotnet/runtime ，https://github.com/dotnet/wpf |
| Kotlin、kotlinx.coroutines | Android/JVM 语言、标准库与协程 | Apache-2.0，https://github.com/JetBrains/kotlin ，https://github.com/Kotlin/kotlinx.coroutines |
| AndroidX、Jetpack Compose | Android 界面、生命周期和文件分享 | Apache-2.0，https://android.googlesource.com/platform/frameworks/support/ |
| Gson | Kotlin 协议控制消息 JSON | Apache-2.0，https://github.com/google/gson |

开发与测试依赖（JUnit、JaCoCo、xUnit、Coverlet 等）不包含在用户程序中。
安装包附带许可证和 .NET 第三方通知；依赖自身保留的版权与通知仍适用。
ClipSync 仅为设计参考，不采用它的文本消息协议，也不包含其应用二进制。

`licenses/` 保存从官方仓库取得的原始许可与 .NET 第三方通知，来源记录在
`licenses/sources.json`。32feet 历史许可正文另保留在 `32feet-Legacy.aml`，
为避免将新版仓库许可直接套用到旧 NuGet 资产，安装包同时附官方 3.5 标签源码
`third-party-source/32feet-3.5-source.zip`。该库按原样调用，没有修改其源码。
