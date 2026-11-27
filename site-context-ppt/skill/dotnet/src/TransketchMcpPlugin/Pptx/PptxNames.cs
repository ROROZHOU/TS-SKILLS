using System.Xml;
using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// OOXML 命名空间与 XML 读取写入的统一入口；导出 XML 不带 BOM、不做缩进重排
/// </summary>
/// <date>2026年11月16日</date>
internal static class PptxNames
{
    /// <summary>
    /// presentationml 命名空间
    /// </summary>
    public const string PresentationNamespace =
        "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <summary>
    /// drawingml 命名空间
    /// </summary>
    public const string DrawingNamespace =
        "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>
    /// 关系属性命名空间
    /// </summary>
    public const string RelationshipAttributeNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>
    /// 包级关系命名空间
    /// </summary>
    public const string PackageRelationshipNamespace =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// 内容类型命名空间
    /// </summary>
    public const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    public static readonly XNamespace P = PresentationNamespace;
    public static readonly XNamespace A = DrawingNamespace;
    public static readonly XNamespace R = RelationshipAttributeNamespace;
    public static readonly XNamespace Rel = PackageRelationshipNamespace;
    public static readonly XNamespace ContentTypes = ContentTypesNamespace;

    /// <summary>
    /// 读取 XML，禁止 DTD 并保留原始空白，避免改写页出现文本重排
    /// </summary>
    /// <param name="text">XML 文本</param>
    public static XDocument Parse(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
            CheckCharacters = false
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    /// <summary>
    /// 序列化 XML；不写 BOM、不缩进，保证未改内容字节稳定
    /// </summary>
    /// <param name="document">XML 文档</param>
    public static byte[] Serialize(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = false,
            Encoding = new System.Text.UTF8Encoding(false),
            CheckCharacters = false
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
        }
        return stream.ToArray();
    }
}
