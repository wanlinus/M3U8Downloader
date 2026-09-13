namespace M3U8Downloader.Core;

/// <summary>
/// 不依赖外部工具的"产物时长"估算。
///
/// 为什么要它：时长核对的思路是"清单声明时长 vs 产物真实时长"，
/// 但如果机器上没装 ffmpeg/ffprobe（或它们认不出这条流），这条校验就会**静默跳过** ——
/// 那就等于没校验。这里自己读容器里现成的信息：
///
/// - **MPEG-TS**：累加 PCR（节目时钟参考）的相邻差值。PCR 是 TS 自带的时间基准，
///   只要流是按时间线拼起来的，首尾差值就是真实时长；累加差值还能天然容忍计数回绕与跳变。
///   PCR 在自适应字段里，只出现在带随机访问标志的包上（通常每 40~100ms 一个）。
/// - **MP4 / fMP4**：读 moov → mvhd 里的 duration / timescale。
///
/// 拿不到就返回 null，调用方据此报"读不出时长"而不是假装通过。
/// </summary>
public static class MediaDurationProbe
{
    /// <summary>估算时长（秒）；读不出来返回 null</summary>
    public static double? TryProbeSeconds(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".ts" or ".m2ts" => TryProbeTsByPcr(path),
                ".mp4" or ".m4s" or ".m4v" => TryProbeMp4ByMvhd(path),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- TS：累加 PCR 差值

    /// <summary>PCR 计数频率：27 MHz ÷ 300 = 90 kHz</summary>
    private const double PcrTicksPerSecond = 90000.0;

    /// <summary>PCR 是 33 位计数器，到这个数就回绕</summary>
    private const double PcrModulus = 8589934592.0;   // 2^33

    private static double? TryProbeTsByPcr(string path)
    {
        const int packetSize = 188;
        const int pcrFlag = 0x10;         // 自适应字段里 PCR_flag 的位

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var buffer = new byte[packetSize * 4096];

        long lastRaw = -1;
        double totalSeconds = 0;
        var samples = 0;

        while (true)
        {
            var read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            for (var i = 0; i + packetSize <= read; i += packetSize)
            {
                if (buffer[i] != 0x47) continue;

                // 只有带自适应字段的包才可能含 PCR
                var afc = (buffer[i + 3] >> 4) & 0x03;
                if (afc != 2 && afc != 3) continue;

                var afLen = buffer[i + 4];
                if (afLen < 7) continue;                          // 放不下 6 字节 PCR
                if ((buffer[i + 5] & pcrFlag) == 0) continue;

                var b = i + 6;
                var basePart = ((long)buffer[b] << 25)
                               | ((long)buffer[b + 1] << 17)
                               | ((long)buffer[b + 2] << 9)
                               | ((long)buffer[b + 3] << 1)
                               | (uint)(buffer[b + 4] >> 7);

                var ext = ((buffer[b + 4] & 0x01) << 8) | buffer[b + 5];
                var pcr = (double)basePart + ext / 300.0;          // 换算到 90 kHz 刻度

                if (lastRaw >= 0)
                {
                    var delta = pcr - lastRaw;
                    if (delta < 0) delta += PcrModulus;            // 回绕
                    if (delta > 0 && delta < PcrTicksPerSecond * 60 * 30)  // 单次跳变超过 30 分钟视为异常，不计
                        totalSeconds += delta / PcrTicksPerSecond;
                }

                lastRaw = (long)pcr;
                samples++;
            }
        }

        return samples >= 2 && totalSeconds > 0 ? totalSeconds : null;
    }

    // ---------------------------------------------------------------- MP4：moov/mvhd

    private static double? TryProbeMp4ByMvhd(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

        // 顶层找 moov（faststart 会在文件开头，但也有可能在结尾）
        var moovOffset = FindTopLevelBox(fs, "moov");
        if (moovOffset < 0) return null;

        fs.Position = moovOffset;
        var mvhdOffset = FindChildBox(fs, moovOffset, "mvhd");
        if (mvhdOffset < 0) return null;

        fs.Position = mvhdOffset;
        var header = new byte[32];
        var got = fs.Read(header, 0, header.Length);
        if (got < header.Length) return null;

        // mvhd 内容布局（从 box 内容起点算）：
        //   [0] version | [1..3] flags
        //   version 0: [4..7] creation | [8..11] modification | [12..15] timescale | [16..19] duration
        //   version 1: [4..11] creation | [12..19] modification | [20..23] timescale | [24..31] duration
        var version = header[0];
        if (version == 1)
        {
            var timescale = ReadUInt32(header, 20);
            var duration = ReadUInt64(header, 24);
            if (timescale == 0) return null;
            return duration / (double)timescale;
        }
        else
        {
            var timescale = ReadUInt32(header, 12);
            var duration = ReadUInt32(header, 16);
            if (timescale == 0) return null;
            return duration / (double)timescale;
        }
    }

    private static long FindTopLevelBox(FileStream fs, string type)
    {
        var size = fs.Length;
        long pos = 0;
        var header = new byte[16];

        while (pos + 8 <= size)
        {
            fs.Position = pos;
            if (fs.Read(header, 0, 8) < 8) return -1;

            long boxSize = ReadUInt32(header, 0);
            var boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var bodyOffset = 8L;

            if (boxSize == 1)
            {
                // largesize：真正的大小在紧随其后的 8 字节里。
                // 注意别再回头看 header[0..3] —— 它已经被这 8 字节覆盖了。
                if (fs.Read(header, 8, 8) < 8) return -1;
                boxSize = (long)ReadUInt64(header, 8);
                bodyOffset = 16;
            }
            else if (boxSize == 0)
            {
                boxSize = size - pos;
            }

            if (boxSize < bodyOffset || pos + boxSize > size) return -1;
            if (boxType == type) return pos + bodyOffset;

            pos += boxSize;
        }

        return -1;
    }

    /// <summary>
    /// 在 <paramref name="parentBody"/> 指向的父 box **内容区**里找子 box，返回子 box 内容起点。
    ///
    /// 坑：父 box 的边界不能从"内容起点"往前推着算 ——
    /// 内容起点的前 8 字节属于父 box 的头，而内容起点上的这 8 字节是**第一个子 box 的头**。
    /// 之前就是在这里把第一个子 box 的 size 当成了父 box 的大小
    /// （mvhd 的 108 字节被当成整个 moov 的长度），于是永远找不到子 box。
    /// </summary>
    private static long FindChildBox(FileStream fs, long parentBody, string type)
    {
        var header = new byte[16];

        // 回到父 box 的头，读出它的真实大小
        long boxStart = -1;
        foreach (var candidate in new[] { parentBody - 16, parentBody - 8 })
        {
            if (candidate < 0) continue;
            fs.Position = candidate;
            if (fs.Read(header, 0, 8) < 8) continue;

            long declared = ReadUInt32(header, 0);
            var headerSize = 8L;
            if (declared == 1)
            {
                if (fs.Read(header, 8, 8) < 8) continue;
                declared = (long)ReadUInt64(header, 8);
                headerSize = 16;
            }
            else if (declared == 0)
            {
                declared = fs.Length - candidate;
            }

            if (candidate + headerSize == parentBody && declared >= headerSize)
            {
                boxStart = candidate;
                break;
            }
        }

        if (boxStart < 0) return -1;

        fs.Position = boxStart;
        if (fs.Read(header, 0, 8) < 8) return -1;
        long parentSize = ReadUInt32(header, 0);
        var parentHeaderSize = 8L;
        if (parentSize == 1)
        {
            if (fs.Read(header, 8, 8) < 8) return -1;
            parentSize = (long)ReadUInt64(header, 8);
            parentHeaderSize = 16;
        }
        else if (parentSize == 0)
        {
            parentSize = fs.Length - boxStart;
        }

        var pos = boxStart + parentHeaderSize;
        var end = boxStart + parentSize;

        while (pos + 8 <= end)
        {
            fs.Position = pos;
            if (fs.Read(header, 0, 8) < 8) return -1;

            long boxSize = ReadUInt32(header, 0);
            var boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var bodyOffset = 8L;

            if (boxSize == 1)
            {
                if (fs.Read(header, 8, 8) < 8) return -1;
                boxSize = (long)ReadUInt64(header, 8);
                bodyOffset = 16;
            }
            else if (boxSize == 0)
            {
                boxSize = end - pos;
            }

            if (boxSize < bodyOffset || pos + boxSize > end) return -1;
            if (boxType == type) return pos + bodyOffset;

            pos += boxSize;
        }

        return -1;
    }

    private static uint ReadUInt32(byte[] b, int offset) =>
        ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];

    private static ulong ReadUInt64(byte[] b, int offset) =>
        ((ulong)ReadUInt32(b, offset) << 32) | ReadUInt32(b, offset + 4);
}
