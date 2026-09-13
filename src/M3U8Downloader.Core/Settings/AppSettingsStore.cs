using System.Text;
using System.Text.Json;

namespace M3U8Downloader.Core.Settings;

/// <summary>
/// 设置的落盘仓库。
///
/// 位置：<c>%APPDATA%\M3U8Downloader\settings.json</c>（每用户，随漫游配置走）。
/// 注意：**不使用任何与本机相关的硬编码路径** —— 分发给别人时会落到各自的用户目录。
/// </summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>设置文件所在目录</summary>
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M3U8Downloader");

    /// <summary>设置文件完整路径</summary>
    public static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// 读取设置。任何异常都回退到默认值 —— 设置坏了也不该让程序起不来。
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path)) return AppSettings.Default;

            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return AppSettings.Default;

            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.Default;
            settings.Normalize();
            return settings;
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    /// <summary>
    /// 保存设置。先写临时文件再替换，避免中途失败留下半个损坏的 JSON。
    /// 返回是否写成功（失败不抛异常，由调用方决定怎么提示）。
    /// </summary>
    public static bool Save(AppSettings settings)
    {
        try
        {
            settings.Normalize();

            Directory.CreateDirectory(SettingsDirectory);
            var path = SettingsFilePath;
            var temp = path + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));

            // File.Move(overwrite) 在 .NET Core 3.0+ 上等价于原子替换
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
