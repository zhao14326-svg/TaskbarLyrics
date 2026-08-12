# 🎵 任务栏歌词 (Taskbar Lyrics)

在**任务栏空白区域**显示当前播放歌曲的歌词的桌面小工具。

## ✨ 功能特性

| 功能 | 说明 |
|------|------|
| 🎤 多播放器适配 | 通过 Windows 系统媒体控制(SMTC)读取播放信息，兼容 **QQ音乐、网易云音乐、Spotify、Windows媒体播放器、Edge/Chrome浏览器** 等所有支持系统媒体控制的播放器 |
| 📝 内嵌歌词识别 | 自动解析 **MP3 的 ID3v2 USLT 帧**、**FLAC/OGG 的 Vorbis LYRICS 标签**内嵌歌词 |
| 📄 LRC歌词文件 | 支持音频文件同目录的 `.lrc` 歌词文件（按歌名自动匹配） |
| 🌐 在线歌词 | 内置 lrclib.net 在线歌词接口，本地无歌词时自动联网获取（可关闭） |
| 🌊 纯音乐波形 | 无歌词的纯音乐/伴奏自动显示**动态波形震动动画** |
| 🎚 歌词同步 | 支持带时间戳歌词的实时滚动同步，显示当前句 + 下一句预览；SMTC 播放器**自动校准**（暂停/拖进度条/跳歌即时同步），另有手动偏移微调 |
| 📐 可配置 | 位置（左/中/右）、宽度、字体大小、颜色、音乐目录均可自定义 |

## 🚀 使用方法

1. 双击桌面的 **任务栏歌词** 快捷方式（或直接运行 `TaskbarLyrics.exe`）
2. 在任意支持的播放器中播放音乐，歌词即会显示在任务栏空白区域
3. 程序运行后常驻**系统托盘**（右下角音符图标）
   - 右键托盘图标 → **设置**：调整位置/大小/颜色/音乐目录
   - 右键托盘图标 → **显示/隐藏悬浮窗**：切换悬浮层显示
   - 右键托盘图标 → **退出**：完全退出程序

## ⚙️ 设置说明

- **悬浮窗位置**：任务栏右侧（靠近时钟）/ 中间 / 左侧
- **音乐目录**：用于查找音频内嵌歌词和 .lrc 文件，每行一个目录
  - 默认自动搜索：`我的音乐`、`下载`、`BaiduNetdiskDownload`、`CloudMusic`、`F:\qq\Tencent Files`、`迅雷下载` 等
- **歌词颜色 / 波形颜色**：`#RRGGBB` 格式

## 📦 项目结构

```
F:\TaskbarLyrics\
├── TaskbarLyrics.csproj     # 项目文件 (.NET 10 / WPF)
├── App.xaml(.cs)            # 应用入口 + 系统托盘
├── MainWindow.xaml(.cs)     # 设置窗口
├── OverlayWindow.xaml(.cs)  # 任务栏悬浮窗（歌词+波形）
├── Services\
│   ├── SmtcMediaService.cs  # 系统媒体信息读取
│   ├── LyricsManager.cs     # 歌词获取编排（缓存/并发/来源优先级）
│   ├── LrcParser.cs         # LRC 解析 + 纯文本歌词时间戳合成
│   ├── AudioTagLyricsReader.cs  # 内嵌歌词解析
│   ├── OnlineLyricsService.cs   # 在线歌词（lrclib + 网易云）
│   └── NeteaseApi.cs        # 网易云搜索/歌词 API 封装
├── Helpers\                 # 窗口API / 设置持久化
└── LyricsTest\              # 解析器单元测试（可选）
```

## 🔧 技术说明

- 基于 **.NET 10 + WPF**，无第三方运行库依赖
- 悬浮窗采用无边框置顶窗口，设置**点击穿透**（`WS_EX_TRANSPARENT`），不会阻挡任务栏操作
- 内嵌歌词解析为纯代码实现（ID3v2 USLT / Vorbis LYRICS），支持 UTF-8/UTF-16/Latin1 编码
- 设置持久化在 `%AppData%\TaskbarLyrics\settings.json`

## 🛠 重新构建

```powershell
cd F:\TaskbarLyrics
dotnet build -c Release
# 运行测试
cd LyricsTest && dotnet run
```
