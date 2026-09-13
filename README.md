# M3U8 视频下载器（WinUI 3）

给国内影视站用的批量下载器：粘贴一个播放页地址，自动识别站点、列出剧集、多线程下载，
下完转 MP4 并做一轮产物校验。图形界面（WinUI 3）+ 命令行两个入口，共用同一个下载内核。

## 下载即用

到 **[Releases](https://github.com/wanlinus/M3U8Downloader/releases)** 下载
`M3U8Downloader-<版本>-win-x64.zip`，解压后双击 `M3U8Downloader.exe` 即可 ——
包内已自带 .NET 与 Windows App SDK 运行时，**目标机器不需要预装任何东西**。

- 系统要求：Windows 10 1809+ / Windows 11，64 位
- 程序未做代码签名，首次运行 SmartScreen 可能提示「已保护你的电脑」，点「更多信息」→「仍要运行」
- **包内不含 FFmpeg**（独立第三方组件，许可与体积都不适合打包）。不配置也能正常下载，
  只是产物保留 `.ts` 且跳过解码校验；需要转 MP4 时在「设置 → FFmpeg」点一次「自动下载 FFmpeg」

## 功能

| 功能 | 说明 |
|---|---|
| **站点批量下载** | 粘贴播放页/详情页地址 → 识别站点 → 列出剧集（含多播放源）→ 勾选后批量下载 |
| **下载任务队列** | 入队即返回，可继续贴下一个地址；任务串行执行，支持暂停 / 继续 / 重试失败集 |
| **断点续传** | 任务列表与分片都落盘，关掉程序再打开自动接着下；暂存目录带清单指纹，播放列表变了就整批重下 |
| **输出 MP4** | 合并后用 ffmpeg 流复制转 `.mp4`（`+faststart`）；没装 FFmpeg 就保留 `.ts` |
| **产物校验** | 落盘字节核对、188 字节逐包对齐、时长核对、容器探测、全量解码检查 —— 逐项写进报告 |
| **下载报告** | 每部剧生成 `剧名-下载报告.md`（汇总 / 分集明细 / 校验结论 / 失败原因 / 运行日志） |
| **广告分片识别** | 异目录聚类 + 重复分片 + 加密上下文突变 + 密钥可用性探测，下载前剔除插播广告 |
| **托盘常驻** | 点关闭只是缩到托盘，下载继续跑；真正退出走托盘右键菜单 |
| AES 解密 | AES-128/192/256，按密钥字节长度分派（不信任 METHOD 字符串） |
| FFmpeg 集成 | 设置里可手动指定，也可应用内一键下载（默认 LGPL 构建） |
| 两个入口 | WinUI 3 桌面界面 + 命令行 `m3u8dl` |

## 支持的站点

站点适配是**模块化**的：一个站点 = 一个 `ISiteAdapter` 实现类，放进
`src/M3U8Downloader.Core/Sites/` 就会被自动登记（反射扫描，按 `Priority` 排序），
**不需要改任何注册代码**。

| 适配器 | 覆盖的形态 | 实测 |
|---|---|---|
| **苹果 CMS** | MacCMS v10 及其衍生模板，国内占比最高 | 多个模板；含原生形态 URL 的欧乐影院 |
| **努努影院** | nnyy.in 自研站点（`ep_slug` + `/_gp/` 接口） | 9 个播放源 / 18 集；单集 951 分片下完并全部校验通过 |
| **通用解析（兜底）** | 页面里直接写着 m3u8 地址的站点 | 专用适配器都不认时自动出手 |

三点值得说明：

- **不写死 URL 前缀、不依赖 CSS 类名**。苹果 CMS 的播放页形态有两种都认：
  伪静态 `/vodplay/30450-1-1.html` 与原生 `/index.php/vod/play/id/83912/sid/1/nid/1.html`；
  剧集列表靠"页面里指向同一剧 ID 的链接"来收集，换模板也能用。
  硬校验是"播放页必须带 `player_aaaa`"，否则明确报错，不会产出垃圾结果。
- **多源**。苹果 CMS 的源在 URL 的 sid 里；努努影院每一集可用的源都不一样
  （《交锋》第 1 集 9 个、第 17 集只剩 1 个），所以识别时逐集取源再汇总成源列表。
- **通用兜底认不了"地址由接口动态返回"的站点**（努努就是这类），那必须写专用适配器；
  碰到这类页面它会明确报错，而不是返回空列表。

要加新站点：实现 `ISiteAdapter` 的三个成员（`CanHandle` / `ParseAsync` /
`ResolvePlaylistUrlAsync`），按需重写 `Priority` 与 `NeedsProxy`，
再到自检的**阶段 J** 补一段本地 fixture 断言。照 `NnyyAdapter` 写即可。

## 构建与运行

需要 Windows 10 1809+ 与 [.NET 10 SDK](https://dotnet.microsoft.com/download)（仅开发需要）。

```powershell
.\scripts\build.ps1                                    # 一键构建
dotnet run --project src\M3U8Downloader.App -c Debug -p:Platform=x64 -r win-x64
```

### 自检（无界面，不依赖外网）

```powershell
dotnet run --project tests\M3U8Downloader.SelfTest
```

本地起一个极简 HLS 服务器跑完整链路，按阶段校验：

| 阶段 | 校验内容 |
|---|---|
| A / A2 | 引擎上报频率、每集进度、总速度、分片级进度 |
| B | 任务队列：入队即见分集清单、状态流转、绑定属性经 UI 线程封送 |
| C | 断点续传：存盘 → 关程序 → 恢复，已完成的集不重下、分片被复用 |
| D | 暂停 → 继续：必须停在「已暂停」（不是「失败」），停下后 6 秒不许再动 |
| E | 重试失败集：**任务数不变**、分集行数不变、已完成的集不被重下 |
| F / F2 | 续传只下原本选中的集（21 集里只勾第 12 集，多源时也一样） |
| G | 转 MP4 后的时长核对（用真实 ffmpeg） |
| H | 密文首字节是 `<` 的加密分片必须能下下来 |
| I | 单文件下载服务：产物、暂存目录清理、报告、时长核对 |
| J | 站点适配层：自动登记、努努（含电影页）、通用兜底、空页面报错 |

退出码 0 表示全部通过。阶段 E 是「点重试冒出重复集」那个 bug 的回归测试。

### 发布与自动打包

```powershell
.\scripts\publish.ps1                  # 自包含，目标机器免安装
.\scripts\publish.ps1 -Version 1.2.3   # 顺便把版本号写进 exe
```

推一个 `v*` 的 tag 就会自动打包并发到 Releases：

```bash
git tag v1.0.0 && git push origin v1.0.0
```

`.github/workflows/release.yml` 会先跑自检（过不了就不打包），再调上面同一个
`publish.ps1` 发布、压成两个 zip、传到 Releases。
也可以到 **Actions → 打包发布 → Run workflow** 手动触发。

## 命令行用法

```powershell
# 单文件
m3u8dl "https://example.com/index.m3u8" -o D:\videos -n 第01集
m3u8dl "https://example.com/index.m3u8" --dry-run        # 只分析不下载
m3u8dl "https://example.com/index.m3u8" --variants       # 列出所有清晰度

# 站点批量
m3u8dl --series "https://www.example.com/vodplay/30450-1-1.html" --list
m3u8dl --series "https://www.example.com/vodplay/30450-1-1.html" --episodes 1-8 -o D:\剧集
```

| 选项 | 说明 |
|---|---|
| `-o, --out <目录>` | 输出目录（默认系统「下载」目录） |
| `-n, --name <文件名>` | 输出文件名（不含扩展名） |
| `--referer` / `--origin` / `--ua` | 附加请求头（应对防盗链） |
| `-c, --concurrency <n>` | 并发下载数，默认 16 |
| `--retries <n>` | 单分片重试次数，默认 3 |
| `--no-skip-ads` | **不**自动跳过疑似广告分片（默认跳过） |
| `--dry-run` / `--variants` | 只分析 / 只列清晰度 |
| `--log <文件>` | 完整日志写入文件 |
| `--series <地址>` | 启用站点批量模式 |
| `--list` / `--episodes <范围>` | 站点模式：只列剧集 / 选集（`1-8`、`1,3,5`） |
| `--all-sources` / `--ep-concurrency <n>` / `--height <n>` | 站点模式：全源 / 同时下载集数 / 优先清晰度 |
| `--ffmpeg-status` / `--ffmpeg-download` / `--ffmpeg-path` | FFmpeg 检测 / 应用内下载 / 手动指定 |
| `--proxy <url>` / `--proxy-test` | 代理地址 / 测试连通性 |

## 代理策略

**默认全部直连**，只在两种情况下才改用代理：

1. 直连失败（超时 / 连接被重置）；
2. 直连**连上了却被 403/451 拒绝** —— 墙外 CDN 常用"按 IP 直接 403"这一招
   （实测欧乐影院的 CDN 就是：国内直连 403、代理 200）。

细节：

- **回退按主机记忆**：记住之后同一主机的后续请求直接走代理。
  不记的话每个分片、每次页面请求都要先白等一遍超时 —— 实测识别欧乐影院
  从 36 秒降到 9 秒，靠的就是这一条。
- 404/410 这类「资源真没了」**不会**触发代理（换了也没用）；
- 连接超时 5 秒，不用干等；
- 在「设置 → 代理」填了地址就生效，**不需要其它开关**；
- 对直连正常的国内站点**零代理开销**，一分流量都不多花。

## 下载任务与报告

- **暂停**：任务落到「已暂停」，可以「继续下载」；重新解析站点后只补没下完的集；
- **重试失败集**：在**原任务上就地重试**，已完成的集不会重下，任务列表里始终只有一条；
- **续传**：任务列表存 `%APPDATA%\M3U8Downloader\tasks.json`，重开程序自动接着下；
  记录说下好了、文件却已被删的集会被识别出来重下；
- **报告**：整部剧结束后在视频目录生成 `剧名-下载报告.md`，含汇总、分集明细
  （编码 / 分辨率 / 帧率 / 时长核对 / 解码检查）、失败原因与完整日志。

产物校验分几层，**没通过的一律不算成功**：

| 层级 | 查什么 |
|---|---|
| 清单 / 密钥 | 响应不是 HTML/JSON 错误页（挡"假 200"） |
| 分片 | Content-Length 核对、TS 同步字节、解密后校验 |
| 合并 | 应拼入分片之和 vs 产物大小（不多不少）、188 字节包对齐 |
| 产物 | 时长核对（清单声明 vs 实际）、容器探测、全量解码检查 |

关掉「全量解码检查」可省时间（400 MB 约 35 秒），报告里那一列会显示为未执行。

## 托盘与后台运行

**关掉窗口 ≠ 关掉程序**：

| 操作 | 行为 |
|---|---|
| 点窗口「✕」/ Alt+F4 | 窗口隐藏到托盘，**下载继续跑**，首次会弹一个气泡提示 |
| 单击托盘图标 / 右键「打开主界面」 | 窗口回来并置前 |
| 托盘右键「退出」 | **唯一真正退出的入口**：先落盘进度，再摘掉托盘图标，最后关窗 |
| 再次点程序图标 | 不新开窗口，把已有实例叫到前台 |

托盘图标用的是 [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)（MIT）。
设置里可以关掉「点关闭时最小化到托盘」，恢复成点关闭即退出。

## 更多

- 项目结构、分层与流水线、暂存目录与校验强度、广告识别原理 → [docs/internal-design.md](docs/internal-design.md)
- 实现中踩过的坑（已修复，留档） → [docs/pitfalls.md](docs/pitfalls.md)

## 免责声明

本工具仅供**个人学习与合法的离线观看**使用。请遵守目标站点的服务条款与当地法律法规，
不要用于传播受版权保护的内容。下载所得内容请于 24 小时内删除。

## 许可证

```
Copyright 2026 wanlinus

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
```

### 第三方组件

- **FFmpeg**（可选、按需获取，**未打包**）：适用其自身的 LGPL/GPL 条款。
  完整声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
- .NET / Windows App SDK / Windows SDK：适用各自许可证，见 [NOTICE](NOTICE)。
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)（MIT）：托盘图标、气泡通知与右键菜单。
- 功能设计参考了 [Liubsyy/M3U8Quicker](https://github.com/Liubsyy/M3U8Quicker)（Apache-2.0）；
  本项目为独立的 C# / WinUI 3 实现，未复制其源代码。
