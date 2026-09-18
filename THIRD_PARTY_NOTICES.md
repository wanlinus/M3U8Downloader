# 第三方组件声明（Third-Party Notices）

本程序以 **Apache License 2.0** 发布。下列第三方组件由各自的权利人提供，
适用其自身许可证，**不因本程序的许可证而改变**。

---

## 1. FFmpeg（可选、按需获取）

| 项目 | 说明 |
|---|---|
| 组件 | FFmpeg（`ffmpeg` / `ffprobe`） |
| 官网 | https://ffmpeg.org/ |
| 许可证 | **LGPL 2.1+** 或 **GPL 2+/3+**，取决于具体构建 |
| 默认构建来源 | https://github.com/BtbN/FFmpeg-Builds |
| 默认构建变体 | `ffmpeg-master-latest-win64-lgpl`（**LGPL**） |

### 本程序如何使用 FFmpeg

FFmpeg **没有被打包进本程序的安装包**。它只会在以下情况被使用：

1. 用户自行安装并指定路径；
2. 用户在本程序的「设置」中**主动点击**「自动下载 FFmpeg」，此时程序会从上述第三方
   构建提供方下载压缩包，解压到程序目录下的 `ffmpeg\` 文件夹；
3. 系统 `PATH` 中已存在 ffmpeg。

调用方式为**以独立进程方式执行**（命令行调用），仅用于：

- 将下载完成的 TS 转为 MP4（`-c copy` 流复制）；
- 读取媒体信息（ffprobe）；
- 对下载结果做解码校验。

### 为什么默认选择 LGPL 构建

本程序是 Apache-2.0 项目，且只把 FFmpeg 当作**独立进程**调用，不需要
libx264/libx265 等 GPL-only 组件（转封装使用流复制，不重新编码）。
因此默认选择 **LGPL** 构建，使分发链路上不引入额外的 GPL 义务，体积也更小。

若你需要 GPL-only 特性（例如用 FFmpeg 重新编码为 H.264），可在
「设置 → FFmpeg 下载源」中改为：

```
https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip
```

此时该 GPL 构建适用 GPL 条款，**由使用者自行承担相应合规责任**。

### 合规提示

- 本程序以 Apache-2.0 分发；FFmpeg 以其自身许可证分发，两者相互独立。
- 若你在自己的产品中**内置**了 FFmpeg 二进制，需自行满足对应许可证要求
  （LGPL 需提供动态链接与替换方式/源码获取途径；GPL 需提供完整对应源码）。
- 本程序默认不内置、不捆绑 FFmpeg，因此不承担上述内置义务。

---

## 2. 运行平台与框架

| 组件 | 许可证 | 说明 |
|---|---|---|
| .NET / C# 运行时与类库 | MIT License | © Microsoft Corporation |
| Windows App SDK（WinUI） | Microsoft 软件许可条款 | © Microsoft Corporation |
| Windows SDK | Microsoft 软件许可条款 | © Microsoft Corporation |
| [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) / H.NotifyIcon.WinUI | MIT License | 系统托盘图标、气泡通知与托盘右键菜单（© HavenDV） |

发布版（`publish\app-win-x64`）通过自包含部署内嵌了 .NET 运行时与
Windows App SDK 运行时，其分发受上述各自许可证约束。

---

## 3. 参考与致谢

本项目的功能设计参考了 **[Liubsyy/M3U8Quicker](https://github.com/Liubsyy/M3U8Quicker)**
（Tauri + Rust，Apache-2.0）。本项目为独立的 C# / WinUI 实现，
**未复制其源代码**；仅参考了其公开的功能设计与部署经验
（如 ffmpeg 的探测顺序、非打包 WinUI 应用的自包含部署等）。

---

## 4. 免责声明

本工具仅供**个人学习与合法的离线观看**使用。请遵守目标站点的服务条款与当地法律法规，
不要用于传播受版权保护的内容。下载所得内容请于 24 小时内删除。
