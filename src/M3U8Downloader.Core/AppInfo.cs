using System.Reflection;
using System.Runtime.InteropServices;

namespace M3U8Downloader.Core;

/// <summary>
/// 应用元信息，供「关于」界面显示。
///
/// ⚠️ 分发前请把下面的作者/版权/许可证字段改成你自己的：
/// 目前项目里没有 LICENSE 文件，<see cref="LicenseName"/> 只是占位。
/// </summary>
public static class AppInfo
{
    /// <summary>产品名（同时用于数据目录名）</summary>
    public const string ProductName = "M3U8 视频下载器";

    /// <summary>英文/程序集名</summary>
    public const string ProgramName = "M3U8Downloader";

    /// <summary>作者或组织</summary>
    public const string Author = "wanlinus";

    /// <summary>版权声明</summary>
    public const string Copyright = "Copyright © 2026 wanlinus";

    /// <summary>项目主页 / 仓库地址</summary>
    public const string ProjectUrl = "https://github.com/wanlinus/M3U8Downloader";

    /// <summary>许可证名称</summary>
    public const string LicenseName = "Apache License 2.0";

    /// <summary>
    /// 第三方组件声明。
    /// FFmpeg 是独立第三方组件，应用会调用它、也可能在应用内下载它，
    /// 因此必须在使用者可见的位置给出声明 —— 这也是分发时的合规要求。
    /// </summary>
    public const string ThirdPartyNotices =
        """
        本程序以 Apache License 2.0 发布。下列第三方组件适用其自身许可证，
        不因本程序的许可证而改变。

        ── FFmpeg（可选、按需获取）──
        用于：TS→MP4 转封装（-c copy 流复制）、读取媒体信息、下载结果解码校验。
        FFmpeg 未被打包进安装包；可自行安装并指定路径，或由本程序从第三方
        构建提供方下载。调用方式为独立进程调用。

        默认构建：BtbN FFmpeg-Builds 的 LGPL 构建
        默认地址：github.com/BtbN/FFmpeg-Builds/releases/download/latest/
                  ffmpeg-master-latest-win64-lgpl.zip

        之所以默认选 LGPL：本程序不需要 libx264 等 GPL-only 组件，
        用 LGPL 可让分发链路上不引入额外的 GPL 义务。如需 GPL 特性，
        可在「设置 → FFmpeg 下载源」中自行改成 GPL 构建，
        相应合规责任由使用者自行承担。

        许可证文本与源码：https://ffmpeg.org/legal.html

        ── 运行平台与框架 ──
        .NET / C#（MIT License，© Microsoft Corporation）
        Windows App SDK / WinUI（Microsoft 软件许可条款）
        Windows SDK（Microsoft 软件许可条款）

        ── 界面依赖 ──
        H.NotifyIcon / H.NotifyIcon.WinUI（MIT License，© HavenDV）
        用于：系统托盘图标、气泡通知、托盘右键菜单。
        https://github.com/HavenDV/H.NotifyIcon

        完整声明见仓库根目录的 THIRD_PARTY_NOTICES.md 与 NOTICE。
        """;

    /// <summary>程序集版本（形如 1.0.0.0，显示时去掉末尾的 .0）</summary>
    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version
                    ?? Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(1, 0, 0);
            return v.Revision > 0 ? v.ToString() : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>运行环境描述，方便用户反馈问题时贴出来</summary>
    public static string RuntimeDescription =>
        $".NET {Environment.Version}  ·  {RuntimeInformation.OSDescription}  ·  {RuntimeInformation.ProcessArchitecture}";

    /// <summary>一键复制的版本信息（用于反馈问题）</summary>
    public static string BuildDiagnosticText(string? ffmpegStatus = null)
    {
        var lines = new List<string>
        {
            $"{ProductName} {Version}",
            RuntimeDescription,
            $"设置文件：{Settings.AppSettingsStore.SettingsFilePath}",
            $"FFmpeg 目录：{Ffmpeg.FfmpegLocator.ManagedDirectory}",
        };

        if (!string.IsNullOrWhiteSpace(ffmpegStatus))
            lines.Add($"FFmpeg：{ffmpegStatus}");

        return string.Join(Environment.NewLine, lines);
    }
}
