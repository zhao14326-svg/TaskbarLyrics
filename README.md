# 🎵 任务栏歌词 (Taskbar Lyrics)

在**任务栏空白区域**显示当前播放歌曲的歌词的桌面小工具。

## ✨ 功能特性

| 功能 | 说明 |
|------|------|
| 🎤 多播放器适配 | 通过 **SMTC(系统媒体控制)+ 窗口标题扫描 + 播放器本地 API** 三重方式读取播放信息，兼容 QQ音乐、网易云音乐、Spotify、Windows媒体播放器、浏览器等所有支持系统媒体控制的播放器 |
| 📝 内嵌歌词识别 | 自动解析 **MP3 的 ID3v2.2/2.3/2.4 USLT 帧**、**FLAC 的 Vorbis LYRICS 标签**内嵌歌词 |
| 📄 LRC歌词文件 | 支持音频文件同目录（含嵌套子目录）的 `.lrc` 歌词文件，按歌名自动匹配 |
| 🌐 在线歌词 | **lrclib.net 主源 + 网易云音乐备源**，本地无歌词时自动联网获取（可关闭） |
| 🎨 专辑封面 | SMTC 缩略图 / 本地文件 / 在线多源**并行获取 + 打分择优**，支持封面主题色提取 |
| 🌊 纯音乐频谱 | 无歌词的纯音乐/伴奏自动显示动态频谱动画（多种样式可调） |
| 🎚 歌词同步 | 带时间戳歌词实时滚动同步，显示当前句 + 下一句预览；SMTC **自动校准**（暂停/拖进度条/跳歌即时同步），另有手动偏移微调；无时间戳的纯文本歌词按音频时长估算节奏 |
| 📐 可配置 | 位置（左/中/右）、宽度、字体、颜色、封面样式、频谱参数、歌词动画、音乐目录等均可自定义 |

## 🚀 使用方法

1. 双击 **任务栏歌词** 快捷方式（或直接运行 `TaskbarLyrics.exe`）
2. 在任意支持的播放器中播放音乐，歌词即会显示在任务栏空白区域
3. 程序运行后常驻**系统托盘**（右下角音符图标）
   - 右键托盘图标 → **设置**：调整位置/大小/颜色/封面/频谱/音乐目录
   - 右键托盘图标 → **显示/隐藏悬浮窗**：切换悬浮层显示
   - 右键托盘图标 → **开机自启**：开机自动运行
   - 右键托盘图标 → **退出**：完全退出程序

## ⚙️ 设置说明（设置窗口按标签页分类）

- **显示**：歌词字体大小、颜色、歌词切换动画、悬浮窗位置、歌词同步偏移
- **封面**：封面显示开关、样式（方形/圆角/圆形）、大小、布局、来源策略（在线优先/本地优先/仅在线/仅本地）、主题色提取
- **频谱**：频谱开关、显示时机（纯音乐/无歌词/有歌词时）、样式、响应速度、高度范围、不透明度、刷新间隔
- **窗口**：自动宽度/高度、置顶、背景/边框、水平锚点、X/Y 偏移
- **高级**：播放自动显示/隐藏、开机自启、自动检测任务栏空余区域、播放器歌词缓存目录、音乐目录
- **音乐目录**：用于查找音频内嵌歌词、`.lrc` 文件和封面，每行一个目录
  - 默认自动搜索：`我的音乐`、`下载`，以及各磁盘根目录的 `Music` / `音乐` / `Downloads` / `BaiduNetdiskDownload` / `CloudMusic` / `qq\Tencent Files` / `迅雷下载` / `夸克网盘` 等（仅收录实际存在的目录）

## 📦 项目结构

```
TaskbarLyrics/
├── TaskbarLyrics.csproj      # 项目文件 (.NET 10 / WPF)
├── App.xaml(.cs)             # 应用入口 + 系统托盘
├── MainWindow.xaml(.cs)      # 设置窗口
├── OverlayWindow.xaml(.cs)   # 任务栏悬浮窗（歌词+封面+频谱）
├── Models\                   # 歌词/歌词行数据模型
├── Services\
│   ├── SmtcMediaService.cs       # 系统媒体信息(SMTC)+ 窗口标题扫描
│   ├── TrackDetector.cs          # 曲目检测（窗口标题 → SMTC → 本地API 回退）
│   ├── LyricsManager.cs          # 歌词获取编排（缓存/并发/两阶段渐进加载/索引）
│   ├── LrcParser.cs              # LRC 解析 + 纯文本歌词时间戳合成
│   ├── AudioTagLyricsReader.cs   # 内嵌歌词/封面/时长读取（MP3 + FLAC）
│   ├── OnlineLyricsService.cs    # 在线歌词（lrclib 主源 + 网易云备源）
│   ├── NeteaseApi.cs             # 网易云搜索/歌词 API 封装
│   ├── PlayerLocalApiService.cs  # 网易云本地 API（歌词/播放状态）
│   ├── PlayerLyricsCache.cs      # QQ/网易云/酷狗等播放器歌词缓存共享
│   ├── LyricCacheService.cs      # 本地歌词持久缓存（SQLite）
│   └── CoverArtService.cs        # 封面多源并行获取
├── Helpers\                 # 窗口API / 设置持久化 / 取色 / 图片解码
├── Assets\                  # 前端资源（HTML/CSS/JS）与字体
├── THIRD-PARTY-NOTICES.md   # 第三方组件与许可证声明
├── LICENSE                  # MIT 许可证
└── LyricsTest\              # 单元测试（18 项）
```

## 🔧 技术说明

- 基于 **.NET 10 + WPF**，悬浮窗 UI 由 **WebView2** 渲染（HTML/CSS/JS 内嵌资源）
- NuGet 依赖：`Microsoft.Web.WebView2`(MIT)、`Microsoft.Data.Sqlite`(MIT)、`SQLitePCLRaw.lib.e_sqlite3`(Apache-2.0)，完整声明见 [THIRD-PARTY-NOTICES.md]
- 悬浮窗采用无边框置顶分层窗口，设置**点击穿透**（`WS_EX_TRANSPARENT`），不会阻挡任务栏操作
- 内嵌歌词解析为纯代码实现（MP3 ID3v2 USLT / FLAC Vorbis LYRICS），支持 UTF-8/UTF-16/Latin1 编码
- 歌词获取采用**两阶段渐进加载**：本地/缓存毫秒级命中 → 在线后台（2.5s 超时），含负缓存、并发去重、最近歌词内存保留
- 音频索引**递归扫描子目录**（支持 `音乐/歌手/专辑/歌.mp3` 嵌套结构），启动后后台自动刷新
- 设置持久化在 `%AppData%\TaskbarLyrics\settings.json`

## 🛠 重新构建

```powershell
cd F:\TaskbarLyrics
dotnet build -c Release
cd LyricsTest
dotnet run -c Release
```

## 📜 许可证

本项目基于 **MIT License** 开源，详见 [LICENSE](LICENSE)。

第三方组件声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)（含 WebView2/SQLite 等依赖与字体许可证）。


