# YF-SuperPlayer PWA

这是 YF-SuperPlayer 的 PWA 测试版本。此仓库当前只包含 PWA、移动端宿主和桌面/移动端可共用的核心逻辑，不包含 Windows WinForms 桌面版源码。

## 运行

安装 .NET 8 SDK 后，在仓库根目录运行：

```powershell
dotnet restore .\WinAudioPlayer.MobileHost\WinAudioPlayer.MobileHost.csproj
dotnet run --project .\WinAudioPlayer.MobileHost\WinAudioPlayer.MobileHost.csproj --urls http://127.0.0.1:5198
```

浏览器打开：

<http://127.0.0.1:5198/>

按 `Ctrl+C` 可停止服务。

## 目录

- `WinAudioPlayer.Core`：可供 Windows 与移动端共用的播放及视频偏好逻辑。
- `WinAudioPlayer.MobileWeb`：Blazor WebAssembly PWA 前端。
- `WinAudioPlayer.MobileHost`：本地宿主以及在线搜索、下载接口。

## 提示

PWA 内部保存的歌曲位于浏览器站点存储中。清理浏览器站点数据会删除它们；重要歌曲请另存到设备。
