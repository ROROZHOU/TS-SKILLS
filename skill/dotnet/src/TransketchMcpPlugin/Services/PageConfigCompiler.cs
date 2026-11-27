using System.Globalization;
using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

/// <summary>
/// 把已审核的样式绑定编译进 site.config；不做语义猜测，只搬运显式证据
/// </summary>
/// <date>2026年11月16日</date>
internal static class PageConfigCompiler
{
    private static readonly Dictionary<string, string> DashNames = new(StringComparer.Ordinal)
    {
        ["sysDash"] = "dashed",
        ["sysDot"] = "dotted",
        ["dash"] = "dashed",
        ["dot"] = "dotted"
    };

    /// <summary>
    /// 执行编译，返回编译后的配置与逐条报告
    /// </summary>
    /// <param name="styles">resolved-styles.json 根节点</param>
    /// <param name="bindings">style-bindings.json 根节点</param>
    /// <param name="config">原始 site.config.json 根节点</param>
    public static (JsonObject Output, List<McpBindingReport> Report) Compile(
        JsonObject styles,
        JsonObject bindings,
        JsonObject config)
    {
        var stylesHash = SiteJson.Text(styles["template_sha256"]);
        if (!string.Equals(stylesHash, SiteJson.Text(bindings["template_sha256"]), StringComparison.Ordinal))
            throw new SiteContextException("Template hash mismatch");

        var output = (JsonObject)config.DeepClone();
        if (output["ppt"]?["pages"] is JsonObject configuredPages)
        {
            foreach (var (pageId,node) in configuredPages)
            {
                if (node is not JsonObject page || (string?)page["type"] is "纯模板" or "非场地分析" or "红线底图" || (string?)page["action"]=="preserve") continue;
                var mode=(string?)bindings["basemapStyles"]?[pageId] ?? (string?)page["basemapStyle"] ??
                    ((bool?)output["basemap"]?["gray"] == false ? "color" : "grayscale");
                if (mode is not ("color" or "grayscale")) throw new SiteContextException($"P{pageId} 底图风格须为 color 或 grayscale。");
                page["basemapStyle"]=mode;
            }
        }
        var lookup = BuildLookup(styles);
        var report = new List<McpBindingReport>();

        foreach (var binding in (bindings["bindings"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)binding["target"] is null or "layer")
                binding["layer"]=MapLayer(SiteJson.Text(binding["layer"]));
            var scale = SiteJson.Number(binding["css_to_ppt_scale"]);
            if (scale <= 0)
                throw new SiteContextException("css_to_ppt_scale 必须大于 0");

            var references = binding["style_refs"] as JsonObject;
            var fillObject = Find(lookup, references?["fill"]);
            var strokeObject = Find(lookup, references?["stroke"]);
            var textObject = Find(lookup, references?["text"]);
            var backgroundObject = Find(lookup, references?["background"]);

            var missing = new List<string>();
            var fill = (JsonNode?)fillObject?["fill"]?.DeepClone();
            var stroke = (JsonNode?)strokeObject?["stroke"]?.DeepClone();
            if (fillObject is not null && fill is null)
                missing.Add("fill inheritance");
            if (strokeObject is not null && stroke is null)
                missing.Add("stroke inheritance");

            var style = new JsonObject
            {
                ["on"] = true,
                ["style"] = SiteJson.Text(binding["geometry"]) is { Length: > 0 } geometry ? geometry : "polygon",
                ["fill"] = fill ?? new JsonObject { ["opacity"] = 0 },
                ["stroke"] = stroke ?? new JsonObject { ["opacity"] = 0, ["width"] = 0 },
                ["label"] = new JsonObject { ["show"] = textObject is not null },
                ["_template"] = new JsonObject
                {
                    ["sha256"] = stylesHash,
                    ["style_refs"] = SiteJson.Copy(references),
                    ["css_to_ppt_scale"] = scale
                }
            };

            if (stroke is JsonObject strokeObjectValue)
                ApplyStroke(strokeObjectValue, scale);

            // 原始实现里 source_stroke 与 stroke 是同一个对象，单位换算会同时写进溯源，这里按最终状态留快照
            var templateInfo = (JsonObject)style["_template"]!;
            templateInfo["source_fill"] = SiteJson.Copy(fill);
            templateInfo["source_stroke"] = SiteJson.Copy(stroke);

            if (textObject is not null)
                ApplyText(style, textObject, backgroundObject, scale, binding, missing);

            templateInfo["unresolved"] = SiteJson.StringArray(missing);

            var pageKey = SiteJson.Text(binding["page"]);
            var pages = EnsureObject(EnsureObject(output, "ppt"), "pages");
            var page = EnsureObject(pages, pageKey);
            if (page["enabled"] is JsonArray enabled)
                for (int i=0;i<enabled.Count;i++) enabled[i]=MapLayer(SiteJson.Text(enabled[i]));
            ApplyBinding(page, binding, style);

            report.Add(new McpBindingReport
            {
                Page = (int)SiteJson.Number(binding["page"]),
                Target = SiteJson.Text(binding["target"]) is { Length: > 0 } target ? target : "layer",
                Layer = SiteJson.Text(binding["layer"]) is { Length: > 0 } layer ? layer : null,
                Unresolved = missing
            });
        }

        var ppt = EnsureObject(output, "ppt");
        ppt["template_sha256"] = stylesHash;
        ppt["style_policy"] = "current-reference-ppt";
        var compilation = new JsonArray();
        foreach (var item in report)
            compilation.AddNode(ReportToJson(item));
        ppt["style_compilation"] = compilation;

        return (output, report);
    }

    private static Dictionary<(int Page, string Id), JsonObject> BuildLookup(JsonObject styles)
    {
        var lookup = new Dictionary<(int Page, string Id), JsonObject>();
        foreach (var page in (styles["pages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var pageNumber = (int)SiteJson.Number(page["page"]);
            foreach (var item in (page["objects"] as JsonArray ?? []).OfType<JsonObject>())
                lookup[(pageNumber, SiteJson.Text(item["id"]))] = item;
        }

        return lookup;
    }

    private static JsonObject? Find(Dictionary<(int Page, string Id), JsonObject> lookup, JsonNode? reference)
    {
        if (reference is null)
            return null;
        var page = (int)SiteJson.Number(reference["page"]);
        var id = SiteJson.Text(reference["id"]);
        return lookup.TryGetValue((page, id), out var item)
            ? item
            : throw new SiteContextException($"找不到样式引用：page={page}, id={id}");
    }

    private static void ApplyStroke(JsonObject stroke, double scale)
    {
        var width = SiteJson.Number(stroke["width_pt"]) / (0.75 * scale);
        stroke["width"] = width;

        var dash = SiteJson.Text(stroke["dash"]);
        stroke["dash"] = DashNames.TryGetValue(dash, out var mapped) ? mapped : dash is "" ? "solid" : dash;

        if (stroke["custom_dash"] is JsonArray { Count: > 0 } customDash)
        {
            var flattened = new JsonArray();
            foreach (var pair in customDash.OfType<JsonArray>())
            {
                foreach (var value in pair)
                    flattened.AddNode(JsonValue.Create(SiteJson.Number(value) * width / 100000));
            }

            stroke["dash"] = flattened;
        }
    }

    private static void ApplyText(
        JsonObject style,
        JsonObject textObject,
        JsonObject? backgroundObject,
        double scale,
        JsonObject binding,
        List<string> missing)
    {
        var runs = (textObject["runs"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(run => SiteJson.Text(run["text"]).Trim().Length > 0)
            .ToList();
        var index = (int)SiteJson.Number(binding["text_run_index"]);
        var run = index >= 0 && index < runs.Count ? runs[index] : new JsonObject();

        var size = SiteJson.NumberOrNull(run["size_pt"]);
        var color = run["color"];
        if (size is null)
            missing.Add("font size inheritance");
        if (color is null)
            missing.Add("text color inheritance");

        var label = (JsonObject)style["label"]!;
        label["size"] = size is null || size == 0 ? null : size / (0.75 * scale);
        label["color"] = color is null ? null : SiteJson.Field(color, "color")?.DeepClone();
        label["opacity"] = color is null
            ? null
            : color is JsonObject colorObject && colorObject.ContainsKey("opacity")
                ? SiteJson.NumberOrNull(colorObject["opacity"])
                : 1;
        label["fontFamily"] = ReadFontFamily(run);
        label["bold"] = SiteJson.Text(run["bold"]) == "1";
        label["italic"] = SiteJson.Text(run["italic"]) == "1";

        var paragraphs = textObject["paragraphs"] as JsonArray;
        label["align"] = paragraphs is { Count: > 0 } && paragraphs[0] is JsonObject first
            ? SiteJson.Field(first, "align")?.DeepClone()
            : null;

        var background = backgroundObject is null ? textObject["fill"] : backgroundObject["fill"];
        var backgroundColor = SiteJson.Field(background, "color");
        if (backgroundColor is not null)
        {
            var hex = SiteJson.Text(backgroundColor);
            var opacity = SiteJson.Field(background, "opacity") is null
                ? 1
                : SiteJson.NumberOrNull(SiteJson.Field(background, "opacity")) ?? 1;
            if (hex.Length >= 7 && hex[0] == '#')
            {
                label["box"] = "rgba(" +
                               int.Parse(hex[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture) + "," +
                               int.Parse(hex[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture) + "," +
                               int.Parse(hex[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture) + "," +
                               opacity.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        var template = style["_template"]!;
        template["text_runs"] = SiteJson.Copy(textObject["runs"]);
        template["text_layout"] = SiteJson.Copy(textObject["text_layout"]);
        template["paragraphs"] = SiteJson.Copy(textObject["paragraphs"]);
        template["background"] = SiteJson.Copy(background);
    }

    private static string? ReadFontFamily(JsonObject run)
    {
        var fonts = (run["font"] as JsonArray ?? []).OfType<JsonArray>().ToList();
        var eastAsian = fonts.FirstOrDefault(pair => SiteJson.Text(pair.Count > 0 ? pair[0] : null) == "ea");
        var latin = fonts.FirstOrDefault(pair => SiteJson.Text(pair.Count > 0 ? pair[0] : null) == "latin");
        var chosen = eastAsian ?? latin;
        return chosen is null ? null : SiteJson.Text(chosen.Count > 1 ? chosen[1] : null);
    }

    private static string MapLayer(string value) => value.Trim() switch
    {
        "水" or "水系" or "水域" or "水体" => "water", "绿地" => "green", "道路" => "road",
        "地铁站" => "metro", "公交车站" or "公交站" => "bus", "商业" => "shop",
        "行政" => "gov", "体育" => "sport", "公园" => "park", "所在区县" or "区县边界" => "district",
        "高速公路" => "express", "铁路站" or "火车站" or "高铁站" => "rail", "机场" => "air",
        "分析圈" => "ring", "实景视点" => "view", "基地" => "project",
        "green" or "water" or "road" or "metro" or "bus" or "shop" or "gov" or "sport" or "park"
        or "district" or "express" or "rail" or "air" or "ring" or "view" or "project" or "planning" or "resource" => value.Trim(),
        _ => throw new SiteContextException("无法对应地图导出图层，请审核绑定：" + value)
    };

    private static void ApplyBinding(JsonObject page, JsonObject binding, JsonObject style)
    {
        var target = SiteJson.Text(binding["target"]) is { Length: > 0 } value ? value : "layer";
        if (target == "parcel")
        {
            page["parcelOutlineStyle"] = Merge(page["parcelOutlineStyle"], style);
            return;
        }

        var key = SiteJson.Text(binding["layer"]);
        if (target == "resource_style")
        {
            var styles = EnsureObject(page, "layerStyles");
            styles[key] = Merge(styles[key], style);
            return;
        }

        if (target != "layer")
            throw new SiteContextException("Unknown target " + target);

        var layers = EnsureObject(page, "layers");
        var existing = layers[key] as JsonObject ?? new JsonObject();
        if (binding.ContainsKey("variant"))
        {
            var variants = EnsureObject(existing, "variants");
            var variant = SiteJson.Text(binding["variant"]);
            variants[variant] = Merge(variants[variant], style);
            existing["widthMode"] = "template";
            layers[key] = existing;
            return;
        }

        style["on"] = existing["on"]?.DeepClone() ?? JsonValue.Create((page["enabled"] as JsonArray)?.Any(item => SiteJson.Text(item) == key) == true);
        layers[key] = Merge(existing, style);
    }

    private static JsonObject Merge(JsonNode? old, JsonObject update)
    {
        var result = old?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var (name, value) in update) result[name] = value?.DeepClone();
        return result;
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonObject ReportToJson(McpBindingReport report)
    {
        var unresolved = new JsonArray();
        foreach (var item in report.Unresolved)
            unresolved.AddNode(JsonValue.Create(item));
        return new JsonObject
        {
            ["page"] = report.Page,
            ["target"] = report.Target,
            ["layer"] = report.Layer,
            ["unresolved"] = unresolved
        };
    }
}
