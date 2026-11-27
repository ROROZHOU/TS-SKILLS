using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// 参考模板的对象树提取；只记录原始证据，不做语义推断
/// </summary>
/// <date>2026年11月16日</date>
internal static class TemplateService
{
    /// <summary>
    /// 需要随提取成果一起落盘的 OOXML 子集前缀
    /// </summary>
    private static readonly string[] OoxmlPrefixes =
        ["ppt/theme/", "ppt/slideMasters/", "ppt/slideLayouts/", "ppt/media/", "ppt/slides/"];

    /// <summary>
    /// 生成 inventory.json
    /// </summary>
    /// <param name="package">模板包</param>
    /// <param name="templateSha256">模板实际哈希</param>
    public static JsonObject BuildInventory(PptxPackage package, string templateSha256)
    {
        var slides = new JsonArray();
        foreach (var (page, part) in PptxParts.SlideParts(package))
        {
            var root = package.GetXml(part).Root
                       ?? throw new SiteContextException($"幻灯片缺少根节点：{part}");
            var spaceTree = root.Element(PptxNames.P + "cSld")?.Element(PptxNames.P + "spTree")
                            ?? throw new SiteContextException($"幻灯片缺少 p:cSld/p:spTree：{part}");
            var relationships = PptxParts.ReadRelationships(package, part);
            var objects = new JsonArray();
            Walk(spaceTree, [], relationships, objects);
            slides.AddNode(new JsonObject
            {
                ["page"] = page,
                ["part"] = part,
                ["objects"] = objects,
                ["relationships"] = SiteJson.ObjectOf(relationships)
            });
        }

        var presentation = package.GetXml("ppt/presentation.xml").Root
                           ?? throw new SiteContextException("ppt/presentation.xml 缺少根节点");
        var size = presentation.Element(PptxNames.P + "sldSz");
        var sizeObject = new JsonObject();
        if (size is not null)
        {
            foreach (var attribute in size.Attributes())
                sizeObject[attribute.Name.LocalName] = attribute.Value;
        }

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["template_sha256"] = templateSha256,
            ["slide_size_emu"] = sizeObject,
            ["slides"] = slides,
            ["extraction_scope"] =
                "raw object tree; transforms retain group coordinate systems; theme and layout inheritance require original parts; image content requires visual review"
        };
    }

    /// <summary>
    /// 把 pptx 里的 OOXML 子集原样落盘，供复核复杂继承
    /// </summary>
    /// <param name="package">模板包</param>
    /// <param name="outputDir">提取目录</param>
    public static void WriteOoxml(PptxPackage package, string outputDir)
    {
        foreach (var partName in package.PartNames)
        {
            if (!OoxmlPrefixes.Any(prefix => partName.StartsWith(prefix, StringComparison.Ordinal)))
                continue;
            var target = Path.Combine(outputDir, "ooxml", partName.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllBytes(target, package.GetBytes(partName));
        }
    }

    /// <summary>
    /// 读取 sldSz 尺寸，键为属性名，值为 EMU
    /// </summary>
    /// <param name="inventory">inventory.json 根节点</param>
    public static Dictionary<string, long> ReadSlideSize(JsonObject inventory)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (inventory["slide_size_emu"] is not JsonObject size)
            return result;
        foreach (var pair in size)
        {
            if (long.TryParse(pair.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                result[pair.Key] = value;
        }

        return result;
    }

    /// <summary>
    /// 从 inventory 读取逐页对象个数
    /// </summary>
    /// <param name="inventory">inventory.json 根节点</param>
    public static Dictionary<int, int> ReadObjectCounts(JsonObject inventory)
    {
        var result = new Dictionary<int, int>();
        if (inventory["slides"] is not JsonArray slides)
            return result;
        foreach (var slide in slides.OfType<JsonObject>())
        {
            var page = slide["page"]?.GetValue<int>() ?? 0;
            var count = (slide["objects"] as JsonArray)?.Count ?? 0;
            if (page > 0)
                result[page] = count;
        }

        return result;
    }

    /// <summary>
    /// 递归遍历 p:spTree，记录形状、图片、组合与图表框架
    /// </summary>
    /// <param name="parent">当前节点</param>
    /// <param name="ancestors">祖先对象的 cNvPr id 列表</param>
    /// <param name="relationships">当前页的关系表</param>
    /// <param name="output">输出数组</param>
    private static void Walk(
        XElement parent,
        List<string> ancestors,
        Dictionary<string, string> relationships,
        JsonArray output)
    {
        var index = -1;
        foreach (var child in parent.Elements())
        {
            index++;
            var tag = child.Name.LocalName;
            if (tag is not ("sp" or "pic" or "grpSp" or "graphicFrame" or "cxnSp"))
                continue;

            var nonVisual = child.Descendants(PptxNames.P + "cNvPr").FirstOrDefault();
            var key = nonVisual?.Attribute("id")?.Value ?? index.ToString(CultureInfo.InvariantCulture);
            var images = new JsonArray();
            foreach (var blipFill in child.Elements(PptxNames.P + "blipFill"))
            {
                foreach (var blip in blipFill.Elements(PptxNames.A + "blip"))
                {
                    var relationship = blip.Attribute(PptxNames.R + "embed")?.Value;
                    if (relationship is null)
                        continue;
                    images.AddNode(new JsonObject
                    {
                        ["relationship"] = relationship,
                        ["target"] = relationships.GetValueOrDefault(relationship)
                    });
                }
            }

            var transforms = new List<XElement>();
            AddIfPresent(transforms, child.Element(PptxNames.P + "spPr")?.Element(PptxNames.A + "xfrm"));
            AddIfPresent(transforms, child.Element(PptxNames.P + "grpSpPr")?.Element(PptxNames.A + "xfrm"));
            AddIfPresent(transforms, child.Element(PptxNames.P + "xfrm"));

            var styles = new List<XElement>();
            AddIfPresent(styles, child.Element(PptxNames.P + "spPr"));
            AddIfPresent(styles, child.Element(PptxNames.P + "style"));
            AddIfPresent(styles, child.Element(PptxNames.P + "txBody"));

            output.AddNode(new JsonObject
            {
                ["id"] = key,
                ["name"] = nonVisual?.Attribute("name")?.Value ?? "",
                ["type"] = tag,
                ["group_path"] = SiteJson.StringArray(ancestors),
                ["z_order"] = index,
                ["text"] = tag == "grpSp"
                    ? new JsonArray()
                    : SiteJson.StringArray(child.Descendants(PptxNames.A + "t").Select(element => element.Value)),
                ["transform_xml"] = SiteJson.StringArray(
                    transforms.Select(element => element.ToString(SaveOptions.DisableFormatting))),
                ["style_xml"] = SiteJson.StringArray(
                    styles.Select(element => element.ToString(SaveOptions.DisableFormatting))),
                ["images"] = images
            });

            if (tag == "grpSp")
                Walk(child, [.. ancestors, key], relationships, output);
        }
    }

    private static void AddIfPresent(List<XElement> target, XElement? element)
    {
        if (element is not null)
            target.Add(element);
    }
}
