namespace TransketchMcpPlugin;

/// <summary>
/// 只读 JPEG 头取图片尺寸，替代图像库做截图尺寸校验
/// </summary>
/// <date>2026年11月16日</date>
internal static class JpegHeader
{
    /// <summary>
    /// 读取 JPEG 宽高
    /// </summary>
    /// <param name="bytes">文件字节</param>
    /// <returns>宽、高，单位像素</returns>
    public static (int Width, int Height) ReadSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
            throw new SiteContextException("不是有效的 JPEG 文件：缺少 SOI 标记");

        var offset = 2;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = bytes[offset + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                offset += 2;
                continue;
            }

            if (offset + 3 >= bytes.Length)
                break;

            var length = (bytes[offset + 2] << 8) | bytes[offset + 3];
            if (length < 2 || offset + 2 + length > bytes.Length)
                break;

            var isStartOfFrame = marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isStartOfFrame)
            {
                if (offset + 8 >= bytes.Length)
                    break;
                var height = (bytes[offset + 5] << 8) | bytes[offset + 6];
                var width = (bytes[offset + 7] << 8) | bytes[offset + 8];
                return (width, height);
            }

            offset += 2 + length;
        }

        throw new SiteContextException("不是有效的 JPEG 文件：未找到 SOF 帧头");
    }

    /// <summary>
    /// 从磁盘读取 JPEG 宽高
    /// </summary>
    /// <param name="path">文件路径</param>
    public static (int Width, int Height) ReadSize(string path)
    {
        if (!File.Exists(path))
            throw new SiteContextException($"找不到图片文件：{path}");
        return ReadSize(File.ReadAllBytes(path));
    }
}
