using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// pptx part 路径与关系的公共处理
/// </summary>
/// <date>2026年11月16日</date>
internal static class PptxParts
{
    /// <summary>
    /// 把相对目标路径归一化为包内路径
    /// </summary>
    /// <param name="baseDirectory">基准目录，如 ppt/slides</param>
    /// <param name="target">关系里的 Target</param>
    public static string Normalize(string baseDirectory, string target)
    {
        var combined = target.StartsWith('/')
            ? target[1..]
            : string.IsNullOrEmpty(baseDirectory) ? target : baseDirectory.TrimEnd('/') + "/" + target;

        var segments = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join("/", segments);
    }

    /// <summary>
    /// part 对应关系文件的路径，如 ppt/slides/_rels/slide1.xml.rels
    /// </summary>
    /// <param name="partName">包内路径</param>
    public static string RelationshipPartPath(string partName)
    {
        var slash = partName.LastIndexOf('/');
        var directory = slash < 0 ? "" : partName[..slash];
        var file = slash < 0 ? partName : partName[(slash + 1)..];
        return string.IsNullOrEmpty(directory)
            ? $"_rels/{file}.rels"
            : $"{directory}/_rels/{file}.rels";
    }

    /// <summary>
    /// 读取关系表；关系文件不存在时返回空表
    /// </summary>
    /// <param name="package">pptx 包</param>
    /// <param name="partName">关系所属 part</param>
    public static Dictionary<string, string> ReadRelationships(PptxPackage package, string partName)
    {
        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        var relPath = RelationshipPartPath(partName);
        if (!package.Contains(relPath))
            return relationships;

        var root = package.GetXml(relPath).Root;
        if (root is null)
            return relationships;

        foreach (var element in root.Elements())
        {
            var id = element.Attribute("Id")?.Value;
            var target = element.Attribute("Target")?.Value;
            if (id is not null && target is not null)
                relationships[id] = target;
        }

        return relationships;
    }

    /// <summary>
    /// 按演示文稿顺序读取每页的页号与 part 路径
    /// </summary>
    /// <param name="package">pptx 包</param>
    public static List<(int Page, string Part)> SlideParts(PptxPackage package)
    {
        var presentation = package.GetXml("ppt/presentation.xml").Root
                           ?? throw new SiteContextException("ppt/presentation.xml 缺少根节点");
        var list = presentation.Element(PptxNames.P + "sldIdLst");
        if (list is null)
            return [];

        var relationships = ReadRelationships(package, "ppt/presentation.xml");
        var result = new List<(int, string)>();
        var page = 0;
        foreach (var slideId in list.Elements(PptxNames.P + "sldId"))
        {
            page++;
            var relationshipId = slideId.Attribute(PptxNames.R + "id")?.Value;
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var target))
                throw new SiteContextException($"presentation.xml 中第 {page} 页缺少对应关系：{relationshipId}");
            result.Add((page, Normalize("ppt", target)));
        }

        return result;
    }
}
