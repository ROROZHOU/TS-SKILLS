using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// 模板对象样式的可寻址解析；复杂继承标记为未解析，保留原始 XML 作为最终依据
/// </summary>
/// <date>2026年11月16日</date>
internal static class StyleExtractor
{
    private static readonly string[] StrokeJoinKinds = ["round", "bevel", "miter"];

    /// <summary>
    /// 生成 resolved-styles.json
    /// </summary>
    /// <param name="package">模板包</param>
    /// <param name="templateSha256">模板实际哈希</param>
    public static JsonObject Extract(PptxPackage package, string templateSha256)
    {
        if (!package.Contains("ppt/theme/theme1.xml"))
            throw new SiteContextException("模板缺少 ppt/theme/theme1.xml，无法解析主题色");

        var theme = package.GetXml("ppt/theme/theme1.xml").Root
                    ?? throw new SiteContextException("ppt/theme/theme1.xml 缺少根节点");
        var scheme = ThemeColorResolver.ReadScheme(theme);

        var pages = new JsonArray();
        foreach (var (page, part) in PptxParts.SlideParts(package))
        {
            var root = package.GetXml(part).Root
                       ?? throw new SiteContextException($"幻灯片缺少根节点：{part}");
            var objects = new JsonArray();
            foreach (var shape in root.Descendants().Where(element =>
                         element.Name == PptxNames.P + "sp"
                         || element.Name == PptxNames.P + "pic"
                         || element.Name == PptxNames.P + "cxnSp"))
            {
                objects.AddNode(ExtractObject(shape, scheme));
            }

            pages.AddNode(new JsonObject
            {
                ["page"] = page,
                ["part"] = part,
                ["objects"] = objects
            });
        }

        return new JsonObject
        {
            ["template_sha256"] = templateSha256,
            ["theme"] = SiteJson.ObjectOf(scheme),
            ["units"] = new JsonObject
            {
                ["font"] = "pt",
                ["stroke"] = "pt",
                ["html"] = "CSS px at capture viewport; PPT conversion uses crop scale × 0.75"
            },
            ["pages"] = pages
        };
    }

    /// <summary>
    /// 统计逐页 run 个数与未解析个数
    /// </summary>
    /// <param name="styles">resolved-styles.json 根节点</param>
    public static Dictionary<int, (int Runs, int Unresolved)> ReadCounts(JsonObject styles)
    {
        var result = new Dictionary<int, (int, int)>();
        if (styles["pages"] is not JsonArray pages)
            return result;
        foreach (var page in pages.OfType<JsonObject>())
        {
            var pageNumber = page["page"]?.GetValue<int>() ?? 0;
            var runs = 0;
            var unresolved = 0;
            if (page["objects"] is JsonArray objects)
            {
                foreach (var shape in objects.OfType<JsonObject>())
                {
                    var shapeRuns = shape["runs"] as JsonArray;
                    runs += shapeRuns?.Count ?? 0;
                    if (HasUnresolved(shape["fill"]) || HasUnresolved(shape["stroke"])
                        || (shapeRuns is not null && shapeRuns.Any(HasUnresolvedColor)))
                        unresolved++;
                }
            }

            if (pageNumber > 0)
                result[pageNumber] = (runs, unresolved);
        }

        return result;
    }

    private static JsonObject ExtractObject(XElement shape, Dictionary<string, string> scheme)
    {
        var nonVisual = shape.Descendants(PptxNames.P + "cNvPr").FirstOrDefault();
        var shapeProperties = shape.Element(PptxNames.P + "spPr");
        var line = shapeProperties?.Element(PptxNames.A + "ln");
        var stroke = line is null ? null : ThemeColorResolver.ResolveFill(line, scheme);

        if (line is not null)
        {
            stroke ??= new JsonObject();
            stroke["width_pt"] = double.Parse(line.Attribute("w")?.Value ?? "0", NumberStyles.Float,
                CultureInfo.InvariantCulture) / 12700.0;

            var dash = line.Element(PptxNames.A + "prstDash");
            stroke["dash"] = dash?.Attribute("val")?.Value ?? "solid";

            var customDash = new JsonArray();
            foreach (var dashStop in line.Elements(PptxNames.A + "custDash").SelectMany(element => element.Elements(PptxNames.A + "ds")))
            {
                customDash.AddNode(new JsonArray(
                    JsonValue.Create(dashStop.Attribute("d")?.Value),
                    JsonValue.Create(dashStop.Attribute("sp")?.Value)));
            }

            stroke["custom_dash"] = customDash;
            stroke["cap"] = line.Attribute("cap")?.Value;
            stroke["join"] = line.Elements()
                .Select(element => element.Name.LocalName)
                .FirstOrDefault(name => StrokeJoinKinds.Contains(name, StringComparer.Ordinal));
            stroke["head"] = ReadEnd(line.Element(PptxNames.A + "headEnd"));
            stroke["tail"] = ReadEnd(line.Element(PptxNames.A + "tailEnd"));
        }

        var runs = new JsonArray();
        var textBody = shape.Element(PptxNames.P + "txBody");
        var levelOneDefault = textBody?.Element(PptxNames.A + "lstStyle")
            ?.Element(PptxNames.A + "lvl1pPr")?.Element(PptxNames.A + "defRPr");

        if (textBody is not null)
        {
            foreach (var paragraph in textBody.Elements(PptxNames.A + "p"))
            {
                var paragraphDefault = paragraph.Element(PptxNames.A + "pPr")?.Element(PptxNames.A + "defRPr");
                foreach (var run in paragraph.Elements(PptxNames.A + "r"))
                {
                    var candidates = new[] { run.Element(PptxNames.A + "rPr"), paragraphDefault, levelOneDefault }
                        .Where(element => element is not null)
                        .Cast<XElement>()
                        .ToList();
                    runs.AddNode(BuildRun(run, candidates, scheme));
                }
            }
        }

        var transform = shapeProperties?.Element(PptxNames.A + "xfrm");
        var result = new JsonObject
        {
            ["id"] = nonVisual?.Attribute("id")?.Value,
            ["name"] = nonVisual?.Attribute("name")?.Value,
            ["text"] = string.Concat(shape.Descendants(PptxNames.A + "t").Select(element => element.Value)),
            ["fill"] = ThemeColorResolver.ResolveFill(shapeProperties, scheme),
            ["stroke"] = stroke,
            ["runs"] = runs,
            ["transform"] = transform?.ToString(SaveOptions.DisableFormatting),
            ["group_coordinates"] = shape.Ancestors().Any(element => element.Name.LocalName == "grpSp"),
            ["inheritance"] = "explicit values plus theme color resolution; missing values require layout/master evidence"
        };

        var body = textBody?.Element(PptxNames.A + "bodyPr");
        result["text_layout"] = body is null ? null : SiteJson.AttributeObject(body.Attributes());

        var paragraphs = new JsonArray();
        if (textBody is not null)
        {
            foreach (var paragraphProperties in textBody.Elements(PptxNames.A + "p").SelectMany(paragraph => paragraph.Elements(PptxNames.A + "pPr")))
            {
                paragraphs.AddNode(new JsonObject
                {
                    ["align"] = paragraphProperties.Attribute("algn")?.Value,
                    ["attributes"] = SiteJson.AttributeObject(paragraphProperties.Attributes()),
                    ["spacing_xml"] = SiteJson.StringArray(paragraphProperties.Elements()
                        .Where(element => element.Name.LocalName is "lnSpc" or "spcBef" or "spcAft")
                        .Select(element => element.ToString(SaveOptions.DisableFormatting)))
                });
            }
        }

        result["paragraphs"] = paragraphs;
        return result;
    }

    private static JsonObject BuildRun(XElement run, List<XElement> candidates, Dictionary<string, string> scheme)
    {
        string? Attribute(string name) => candidates
            .Select(candidate => candidate.Attribute(name)?.Value)
            .FirstOrDefault(value => value is not null);

        var size = Attribute("sz");
        var color = candidates
            .Select(candidate => ThemeColorResolver.ResolveFill(candidate, scheme))
            .FirstOrDefault(node => node is not null);

        var fonts = new JsonArray();
        foreach (var candidate in candidates)
        {
            foreach (var font in candidate.Elements().Where(element => element.Name.LocalName is "latin" or "ea"))
            {
                fonts.AddNode(new JsonArray(
                    JsonValue.Create(font.Name.LocalName),
                    JsonValue.Create(font.Attribute("typeface")?.Value)));
            }
        }

        return new JsonObject
        {
            ["text"] = run.Element(PptxNames.A + "t")?.Value ?? "",
            ["size_pt"] = size is null
                ? null
                : double.Parse(size, NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0,
            ["color"] = color,
            ["bold"] = Attribute("b"),
            ["italic"] = Attribute("i"),
            ["underline"] = Attribute("u"),
            ["spacing"] = Attribute("spc"),
            ["font"] = fonts
        };
    }

    private static JsonNode? ReadEnd(XElement? end) =>
        end is null ? null : SiteJson.AttributeObject(end.Attributes());

    private static bool HasUnresolved(JsonNode? node) =>
        node is JsonObject shape && shape["unresolved"] is not null;

    private static bool HasUnresolvedColor(JsonNode? node) =>
        node is JsonObject run && HasUnresolved(run["color"]);
}
