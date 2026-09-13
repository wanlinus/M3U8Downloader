# M3U8 视频下载器（WinUI 3）

一个用 **C# / .NET 10 / WinUI 3** 编写的 HLS(m3u8) 视频下载器，参考 [Liubsyy/M3U8Quicker](https://github.com/Liubsyy/M3U8Quicker)（Tauri + Rust）的功能设计，并针对实际使用中遇到的**源站动态插播广告导致下载恒定失败**的问题做了专门处理。

---

## 为什么会有这个项目

用其他工具下载某短剧站的视频时，任务总是稳定报错「失败 12 片」，重试多次、换地址都没用。排查后发现根因：

- 该源站**每次请求 m3u8 都可能重新插入广告**：把 3 个位置各替换成同一组 4 个广告分片，合计 **12 片**；
- 广告分片指向另一个日期目录，而**那个目录的 `key.key` 已返回 404**；
- 广告片还被标记为 `#EXT-X-KEY:METHOD=NONE`（明文），与整条 AES-128 加密流不一致；
- 多数下载器**只判断 HTTP 状态码、不做内容校验**，于是这 12 片必然失败；而失败分片又会让整个任务判定为未完成。

本工具在**下载前**就把这类分片识别出来并剔除，因此同样的地址可以一次成功。
（插播是随机的：有时列表里没有广告，此时检测器也不会误报 —— 两种情况最终产物一致。）

---

## 功能

| 功能 | 说明 |
|---|---|
| **站点批量下载** | 粘贴视频网站播放页/详情页地址 → 自动识别站点 → 列出剧集 → 勾选后批量下载整部剧 |
| **广告/无效分片自动识别** | 基于「异目录聚类 + 重复分片 + 加密上下文突变 + 密钥可用性探测」四重判定，下载前剔除 |
| 多线程下载 | `Parallel.ForEachAsync` 并发调度，并发数可调（1–64） |
| 失败重试 | 单分片可重试，指数退避 |
| AES 解密 | AES-128/192/256，**按密钥字节长度分派**（不信任 METHOD 字符串） |
| 断点续传 | 分片落盘，重新运行自动跳过已完成部分（站点模式每集独立临时目录） |
| 内容校验 | 假 200/HTML 嗅探、Content-Length 校验、TS 188 字节包对齐校验 |
| 请求头支持 | 自定义 Referer / Origin / User-Agent（应对防盗链）；站点模式自动携带 |
| 清晰度选择 | 主清单自动选最高分辨率，也可列出全部 |
| 合并输出 | 纯字节拼接为 `.ts`，并校验产物包对齐 |
| 保存路径 | 读取**系统真实下载目录**（支持被用户移动到 D 盘等情况），批量模式自动建剧名子目录 |
| **FFmpeg 集成** | 设置里可手动指定、或**应用内一键下载**（默认 LGPL 构建）；未安装也不影响下载本身 |
| **代理支持** | 站点解析与 FFmpeg 下载均可走代理，带连通性测试；自动绕过局域网地址 |
| 设置与关于 | 设置落盘到 `%APPDATA%`；「关于」含版本、版权、许可证与第三方组件声明 |
| 图形界面 + 命令行 | WinUI 3 桌面界面（双模式），另附 CLI 便于脚本调用 |

> 界面为单实例：重复双击图标不会再起第二个窗口抢带宽。站点模式下并发总连接数 ≈
> 同时下载集数 × 每集分片并发（默认 2×16=32）。

### 支持的站点

目前实现了**苹果 CMS（MacCMS v10）**及其衍生模板的适配器 —— 国内影视站占比最高的一类。
已实测通过多个不同模板的站点（播放页前缀各不相同）：

| 站点 | 播放页形态 | 实测 |
|---|---|---|
| 七猫短剧 | `/vodplay/30450-1-1.html` | 单源，源名 `360播放器` |
| 剧集搜 | `/v/7005-1-3.html` | **21 个播放源 / 336 集**，源名全部解析 |

识别方式刻意**不写死 URL 前缀、不依赖 CSS 类名**：

- 从页面提取 `var player_aaaa={...}`（用**花括号配对法**解析 JSON 而非正则，因为 JSON 内含转义斜杠 `\/`）拿到本集 m3u8 直链；
- 剧中转场配置 `playerconfig.js` 的路径各站不同，因此**从页面 `<script src>` 自动发现**，而不是写死 `/static/js/playerconfig.js`；
- 剧集列表通过收集页内所有指向**同一剧集 ID** 的播放页链接、按集号排序得到，因此换模板也能用；同时按剧 ID 过滤掉侧边栏「猜你喜欢」里其他剧的链接；
- 多播放源默认只勾选当前页所在源，避免同一集被下载两遍。

> 官方 JSON API（`api.php/provide/vod/`）常被站长关闭（直接返回 `closed`），所以走 HTML 解析。
>
> 由于前缀不再写死，日期型路径（如 `/news/2026-09-13.html`）也可能"长得像"剧集页，
> 因此加了硬校验：苹果 CMS 播放页**必然带 `player_aaaa`**，没有就直接明确报错，不会产出垃圾结果。

---

## 项目结构

```
M3U8Downloader/
├── LICENSE                           # Apache License 2.0
├── NOTICE                            # 版权与第三方组件摘要
├── THIRD_PARTY_NOTICES.md            # 第三方组件完整声明（FFmpeg 等）
├── M3U8Downloader.slnx
├── src/
│   ├── M3U8Downloader.Core/          # 核心库（无 UI 依赖，可复用）
│   │   ├── HlsModels.cs              # 播放列表/分片/密钥模型
│   │   ├── M3U8Parser.cs             # m3u8 解析（主清单 + 媒体清单）
│   │   ├── SegmentInspector.cs       # 广告/无效分片识别 ★核心
│   │   ├── DownloadModels.cs         # 下载参数与进度模型
│   │   ├── HlsDownloader.cs          # 下载引擎 ★核心
│   │   ├── KnownFolders.cs           # 系统「下载」目录（支持被用户移盘）
│   │   ├── AppInfo.cs                # 版本/版权/许可证/第三方声明
│   │   ├── Settings/                 # 设置模型与落盘（%APPDATA%）
│   │   ├── Net/ProxyHelper.cs        # 代理构造、地址规范化、连通性测试
│   │   ├── Ffmpeg/                   # FFmpeg 探测与自动下载安装
│   │   └── Sites/                    # 站点识别与批量下载
│   │       ├── SiteModels.cs         # 剧集/播放源模型
│   │       ├── SiteResolver.cs       # 适配器接口 + 站点识别入口 + 编码嗅探
│   │       ├── MacCmsAdapter.cs      # 苹果 CMS 适配器
│   │       └── SeriesDownloader.cs   # 批量下载协调器 + 选集 + 清晰度挑选
│   ├── M3U8Downloader.App/           # WinUI 3 图形界面
│   │   ├── MainWindow.xaml(.cs)      # 双模式界面 + 设置/关于入口
│   │   ├── MainViewModel.cs          # 单文件下载
│   │   ├── SeriesBatchViewModel.cs   # 站点批量下载
│   │   ├── EpisodeItemViewModel.cs   # 剧集项（可绑定勾选状态）
│   │   ├── SettingsDialog.xaml(.cs)  # 设置（FFmpeg / 代理 / 下载默认值）
│   │   └── AboutDialog.xaml(.cs)     # 关于（版本 / 版权 / 许可证 / 第三方声明）
│   └── M3U8Downloader.Cli/           # 命令行版
│       └── Program.cs
├── scripts/
│   ├── build.ps1                     # 一键构建
│   ├── publish.ps1                   # 一键发布（自包含 + 运行时完整性自检）
│   └── test-ads.ps1                  # 用真实地址验证广告识别
└── publish/                          # 发布产物（含 app-win-x64\ffmpeg\）
```

---

## 构建与运行

### 环境要求

- Windows 10 1809+ / Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)（仅开发需要；运行发布版不需要）

### 构建

```powershell
cd M3U8Downloader
.\scripts\build.ps1
```

### 运行图形界面

```powershell
dotnet run --project src\M3U8Downloader.App -c Debug -p:Platform=x64 -r win-x64
```

界面右上角可切换两个模式：**单文件下载** 与 **站点批量下载**。
也可加 `--mode=batch` 直接以批量模式启动。

### 发布（自包含，免安装）

```powershell
.\scripts\publish.ps1
```

产物已内嵌 Windows App SDK 运行时与 .NET 运行时，**目标机器无需安装任何运行时**，
直接双击 `publish\app-win-x64\M3U8Downloader.exe` 即可。整体约 210 MB。
脚本末尾会自检 `hostpolicy.dll` / `coreclr.dll` / `Microsoft.WindowsAppRuntime.dll` 等是否齐全。

---

## 命令行用法

### 单文件模式

```powershell
# 基本下载
m3u8dl "https://example.com/index.m3u8" -o D:\videos -n 第01集

# 带防盗链请求头
m3u8dl "https://example.com/index.m3u8" --referer "https://example.com/play/1-1.html" --origin "https://example.com"

# 只分析不下载（查看广告识别结果）
m3u8dl "https://example.com/index.m3u8" --dry-run

# 列出所有清晰度
m3u8dl "https://example.com/index.m3u8" --variants
```

### 站点批量模式

```powershell
# 识别站点并列出剧集（不下载）
m3u8dl --series "https://www.example.com/vodplay/30450-1-1.html" --list

# 下载指定集
m3u8dl --series "https://www.example.com/vodplay/30450-1-1.html" --episodes 1-8 -o D:\剧集

# 全部下载
m3u8dl --series "https://www.example.com/vodplay/30450-1-1.html" -o D:\剧集
```

### 选项

| 选项 | 说明 |
|---|---|
| `-o, --out <目录>` | 输出目录（默认 `用户\Downloads`） |
| `-n, --name <文件名>` | 输出文件名（不含扩展名） |
| `--referer <地址>` | 附加 Referer 请求头 |
| `--origin <地址>` | 附加 Origin 请求头 |
| `--ua <字符串>` | 自定义 User-Agent |
| `-c, --concurrency <n>` | 并发下载数，默认 16 |
| `--retries <n>` | 单分片重试次数，默认 3 |
| `--no-skip-ads` | **不**自动跳过疑似广告分片（默认跳过） |
| `--dry-run` | 只解析与分析，不下载 |
| `--variants` | 只列出所有清晰度 |
| `--log <文件>` | 把完整日志写入文件 |
| `--series <地址>` | 启用站点批量模式 |
| `--list` | 站点模式：只列出剧集不下载 |
| `--episodes <范围>` | 站点模式：选集，如 `1-8`、`1,3,5`、`2` |
| `--all-sources` | 站点模式：勾选全部播放源（默认只勾当前源） |
| `--ep-concurrency <n>` | 站点模式：同时下载集数，默认 2 |
| `--height <n>` | 优先选择指定高度的清晰度 |

### FFmpeg / 代理

| 选项 | 说明 |
|---|---|
| `--ffmpeg-status` | 查看 FFmpeg 检测结果（自定义路径 → 程序目录 → 用户目录 → 系统 PATH） |
| `--ffmpeg-download` | 应用内下载并安装 FFmpeg（默认 LGPL 构建，约 185 MB） |
| `--ffmpeg-url <url>` | 指定下载源（默认 BtbN 官方构建，国内可换镜像） |
| `--ffmpeg-path <exe>` | 手动指定 `ffmpeg.exe` 路径 |
| `--proxy <url>` | 使用代理，如 `http://127.0.0.1:7897` |
| `--proxy-test` | 测试代理连通性（与界面「测试」按钮**同一实现**） |

```powershell
# 走代理下载 FFmpeg，并验证装好没有
m3u8dl --ffmpeg-download --proxy http://127.0.0.1:7897
m3u8dl --ffmpeg-status

# 只测代理通不通
m3u8dl --proxy-test --proxy 127.0.0.1:7897
```

---

## FFmpeg 与代理

### FFmpeg 放在哪

安装位置遵循**"放在程序自己身边"**的原则，不往系统其它地方写东西：

| 优先级 | 位置 | 说明 |
|---|---|---|
| 1 | 设置里手动指定 | 复用你已有的 ffmpeg |
| 2 | **`<程序目录>\ffmpeg\`** | **默认安装位置**。整个文件夹拷到别的机器照样能用 |
| 3 | `%LOCALAPPDATA%\M3U8Downloader\ffmpeg\` | 仅当程序目录不可写时（如装在 `C:\Program Files\`）才用 |
| 4 | 系统 `PATH` | 找得到就用 |

> 严格门槛：`ffmpeg` 与 `ffprobe` **都**可用才算"已就绪"。只找到一半时界面会如实说明缺哪个。

### 为什么默认下载 LGPL 构建

本项目是 Apache-2.0，且只把 FFmpeg 当**独立进程**调用（转封装用 `-c copy` 流复制，不重新编码），
用不到 libx264 等 GPL-only 组件。选 LGPL 构建可以让分发链路上不引入额外的 GPL 义务，体积也更小
（LGPL ≈ 133 MB，GPL ≈ 164 MB）。需要 GPL 特性时可在设置里改下载源，详见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

### 代理

- 站点解析与 FFmpeg 下载都走同一份代理设置；
- 地址可只填 `127.0.0.1:7897`，会自动补 `http://`；
- **自动绕过** `localhost` / `10.x` / `192.168.x` / `172.16-31.x` —— 国内站点不必绕代理；
- 设置里有「测试」按钮，CLI 有 `--proxy-test`，两者共用 Core 里的同一实现。
  （测试目标 `api.github.com/zen` **必须带 User-Agent**，否则 GitHub 直接回 403，
  会出现"代理明明是通的却测不出可用"的假故障。）

---

## 广告/无效分片识别原理

`SegmentInspector` 用四重判据，命中任一即标记为可疑（可用 `--no-skip-ads` 关闭）：

1. **异目录聚类**：正常分片必然集中于同一目录。把分片按 URL 目录分组，占比低于 50% 的少数派目录即判为插播。
2. **重复分片**：同一分片地址在列表中重复出现 ≥3 次（通常在开头/中部/结尾各一次），是广告循环插入的特征。
3. **加密上下文突变**：整条流是 AES-128，个别分片却声明 `METHOD=NONE`。
4. **密钥可用性探测**：可疑分片引用的 `key.key` 实际请求一次，取不到即升级为「必然失败」。

判定结果会在界面日志与 CLI 输出中逐条列出原因，例如：

```
⚠ 检测到 12 个疑似插播广告/无效分片：
   · 发现 12 个分片来自异目录 /20260830/Kt5SzxVn/hls（主流目录 …/K2KZSZlA/1395kb/hls 含 1398 片）
   · 整条流为 1398 片加密，但有 12 片被声明为明文（METHOD=NONE）
   [319] 2Uog4J8A.ts (5s) 来自其他目录…；且同一分片重复出现 3 次；该片为明文段，与整条加密流不一致
   → 已启用自动跳过，这些分片不会导致任务失败。
```

---

## 实现中踩过的坑（已修复，供参考）

1. **AES 填充策略**：HLS 的 AES-128 分片通常带 **PKCS7 填充**。若错误地用「无填充 + 截断到 16 字节整数倍」处理，每片会丢掉 4–15 字节，拼接后 **188 字节 TS 包整体错位** —— 文件大小看着正常、实测包同步率只有 2%，根本无法播放。本工具按 PKCS7 → 无填充+188 对齐 的顺序尝试并校验结果。

2. **密钥按长度分派**：部分源站把 AES-192/256 错标为 `AES-128`，所以要按**密钥实际字节长度**（16/24/32）决定算法强度，而不是照 METHOD 字符串走。

3. **IV 推导**：`EXT-X-KEY` 未显式给出 IV 时，按规范用**媒体序号**（`MEDIA-SEQUENCE + 索引`，16 字节大端）推导，而不是列表索引。

4. **重定向后 URL 作基准**：解析相对路径必须用 `HttpClient` 重定向后的**最终地址**，否则经 CDN 跳转会解析到错误域名。

5. **假 200 与截断响应**：不少源站用 `200 + HTML 错误页` 冒充成功，或提前截断响应。需要嗅探 HTML、校验 Content-Length。

6. **路径中的双斜杠不能归一化**：某些源的主清单是 `/20260906/xx//1395kb/hls/index.m3u8`，把 `//` 折叠成 `/` 会直接 404。

7. **WinUI 3 部署**：非打包（unpackaged）应用若不自包含，会因缺少 DDLM 包而弹「Windows App Runtime 组件缺失」。设 `WindowsAppSDKSelfContained=true` + `SelfContained=true` 可彻底免除依赖。

8. **勾选状态不会回写**：WinUI 的 `CheckBox` 双向绑定要求属性实现 `INotifyPropertyChanged`。Core 模型保持朴素，界面侧用 `EpisodeItemViewModel` 薄包装。

9. **并发是嵌套的**：站点模式下总连接数 ≈ 集数并发 × 每集分片并发（默认 2×16=32），不要盲目调大。

10. **别硬编码下载目录**：`%USERPROFILE%\Downloads` 与**系统真实下载目录**可能不是同一个位置（用户可以把「下载」文件夹移到别的盘）。而且 `%USERPROFILE%\Downloads` 往往**仍然存在**，于是程序会**静默存到错误的地方**——不报错，但你在下载文件夹里找不到文件。正确做法是向 Shell 查询 `FOLDERID_Downloads`（见 `Core/KnownFolders.cs`）。

11. **WinUI 下读命令行**：`Environment.GetCommandLineArgs()` 在 WinUI 的启动路径里不保证可靠（实测会返回空，导致启动参数失效），应改用 Win32 `GetCommandLineW`。

12. **PowerShell 脚本编码**：Windows PowerShell 5.1 读取**无 BOM 的 UTF-8** 脚本时会按系统 ANSI 代码页（中文系统为 GBK）解码。含中文的脚本若不加 BOM，中文字符的尾字节会把后面的引号吞掉 → 语法错误。含中文的 `.ps1` 请存为 **UTF-8 with BOM**，或只用 ASCII。

---

## 免责声明

本工具仅供**个人学习与合法的离线观看**使用。请遵守目标站点的服务条款与当地法律法规，不要用于传播受版权保护的内容。下载所得内容请于 24 小时内删除。

---

## 许可证

本项目以 **Apache License 2.0** 发布，详见 [LICENSE](LICENSE)。

```
Copyright 2026 wanlinus

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
```

### 第三方组件

- **FFmpeg**（可选、按需获取，**未打包进安装包**）：适用其自身的 LGPL/GPL 条款，
  与本项目的许可证相互独立。完整声明见 **[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)**。
- .NET / Windows App SDK / Windows SDK：适用各自许可证，见 [NOTICE](NOTICE)。
- 功能设计参考了 [Liubsyy/M3U8Quicker](https://github.com/Liubsyy/M3U8Quicker)（Apache-2.0）；
  本项目为独立的 C# / WinUI 3 实现，未复制其源代码。
