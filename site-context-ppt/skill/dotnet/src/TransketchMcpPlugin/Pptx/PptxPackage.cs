using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// pptx 包的有序读写；未改动的 part 保留原始字节，只有显式改写的 part 才重新序列化
/// </summary>
/// <date>2026年11月16日</date>
internal sealed class PptxPackage
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XDocument> _dirtyXml = new(StringComparer.Ordinal);

    /// <summary>
    /// 包内 part 路径，按原包顺序
    /// </summary>
    public IReadOnlyList<string> PartNames => _order;

    /// <summary>
    /// 从磁盘读取 pptx
    /// </summary>
    /// <param name="path">pptx 绝对路径</param>
    public static PptxPackage Load(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new SiteContextException($"找不到 pptx 文件：{path}");

        var package = new PptxPackage();
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            package._order.Add(entry.FullName);
            package._entries[entry.FullName] = buffer.ToArray();
        }

        return package;
    }

    /// <summary>
    /// 判断 part 是否存在
    /// </summary>
    /// <param name="partName">包内路径</param>
    public bool Contains(string partName) => _entries.ContainsKey(partName);

    /// <summary>
    /// 读取 part 原始字节
    /// </summary>
    /// <param name="partName">包内路径</param>
    public byte[] GetBytes(string partName)
    {
        if (!_entries.TryGetValue(partName, out var bytes))
            throw new SiteContextException($"pptx 包内缺少 part：{partName}");
        return bytes;
    }

    /// <summary>
    /// 读取 part 文本
    /// </summary>
    /// <param name="partName">包内路径</param>
    public string GetText(string partName) => Encoding.UTF8.GetString(GetBytes(partName));

    /// <summary>
    /// 读取 part 的 XML；重复读取返回同一实例，可直接修改后 SetXml
    /// </summary>
    /// <param name="partName">包内路径</param>
    public XDocument GetXml(string partName) =>
        _dirtyXml.TryGetValue(partName, out var cached)
            ? cached
            : PptxNames.Parse(GetText(partName));

    /// <summary>
    /// 标记 part 为已改写，保存时重新序列化
    /// </summary>
    /// <param name="partName">包内路径</param>
    /// <param name="document">改写后的 XML</param>
    public void SetXml(string partName, XDocument document)
    {
        if (!_entries.ContainsKey(partName))
            throw new SiteContextException($"pptx 包内缺少 part，无法改写：{partName}");
        _dirtyXml[partName] = document;
    }

    /// <summary>
    /// 用原始字节写入 part，字节内容保持不变
    /// </summary>
    /// <param name="partName">包内路径</param>
    /// <param name="bytes">新字节</param>
    public void SetBytes(string partName, byte[] bytes)
    {
        if (!_entries.ContainsKey(partName))
            throw new SiteContextException($"pptx 包内缺少 part，无法写入：{partName}");
        _dirtyXml.Remove(partName);
        _entries[partName] = bytes;
    }

    /// <summary>
    /// 新增 part，追加在包尾；已存在时抛异常，避免静默覆盖模板资源
    /// </summary>
    /// <param name="partName">包内路径</param>
    /// <param name="bytes">内容</param>
    public void AddPart(string partName, byte[] bytes)
    {
        if (_entries.ContainsKey(partName))
            throw new SiteContextException($"pptx 包内已存在同名 part：{partName}");
        _order.Add(partName);
        _entries[partName] = bytes;
    }

    /// <summary>
    /// 从另一个包复制 part，保留原始字节
    /// </summary>
    /// <param name="source">源包</param>
    /// <param name="partName">包内路径</param>
    public void CopyPartFrom(PptxPackage source, string partName)
    {
        if (Contains(partName))
            SetBytes(partName, source.GetBytes(partName));
        else
            AddPart(partName, source.GetBytes(partName));
    }

    /// <summary>
    /// 生成不与现有 part 冲突的媒体路径
    /// </summary>
    /// <param name="extension">扩展名，不带点</param>
    /// <param name="stem">文件名主干</param>
    public string NewMediaPart(string stem, string extension)
    {
        for (var index = 0; ; index++)
        {
            var name = index == 0
                ? $"ppt/media/{stem}.{extension}"
                : $"ppt/media/{stem}-{index}.{extension}";
            if (!Contains(name))
                return name;
        }
    }

    /// <summary>
    /// 按原顺序写出新包；已改写的 XML part 在这里序列化
    /// </summary>
    /// <param name="path">目标路径</param>
    public void Save(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var partName in _order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(partName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            if (_dirtyXml.TryGetValue(partName, out var document))
            {
                var bytes = PptxNames.Serialize(document);
                entryStream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                var bytes = _entries[partName];
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
