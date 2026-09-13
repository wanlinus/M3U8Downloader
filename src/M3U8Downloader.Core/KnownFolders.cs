using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace M3U8Downloader.Core;

/// <summary>
/// 系统已知文件夹。
///
/// 为什么不能直接拼 <c>%USERPROFILE%\Downloads</c>：
/// 用户可以在「此电脑 → 下载 → 属性 → 位置」里把「下载」文件夹整体搬到别的盘，
/// 而 <c>%USERPROFILE%\Downloads</c> 往往**依然存在**，于是程序会静默地把文件存到一个
/// 用户根本不会去看的旧目录里 —— 不报错，但结果不符合预期。
/// 正确做法是向系统查询 FOLDERID_Downloads。
/// </summary>
public static class KnownFolders
{
    private static readonly Lazy<string> LazyDownloads = new(ResolveDownloads, isThreadSafe: true);

    /// <summary>系统「下载」文件夹的绝对路径（已反映用户重定位后的实际位置）</summary>
    public static string Downloads => LazyDownloads.Value;

    private static string ResolveDownloads()
    {
        // 首选：Shell API，能正确反映被搬走的情况
        if (OperatingSystem.IsWindows())
        {
            var fromShell = TryGetKnownFolderPath(FolderId.Downloads);
            if (!string.IsNullOrWhiteSpace(fromShell)) return fromShell;
        }

        // 兜底：%USERPROFILE%\Downloads
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            return Path.Combine(profile, "Downloads");

        return Directory.GetCurrentDirectory();
    }

    [SupportedOSPlatform("windows")]
    private static string? TryGetKnownFolderPath(Guid folderId)
    {
        var ptr = IntPtr.Zero;
        try
        {
            // dwFlags = KF_FLAG_DEFAULT(0)，hToken = null → 当前用户
            var hr = SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero) return null;

            var path = Marshal.PtrToStringUni(ptr);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    private static class FolderId
    {
        /// <summary>FOLDERID_Downloads = {374DE290-123F-4565-9164-39C4925E467B}</summary>
        public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    }
}
