using System.Xml.Linq;
using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

/// <summary>
/// 验收：核对封面依赖未被改动、采集溯源可追、原生对象与页数符合 profile 约定
/// </summary>
/// <date>2026年11月16日</date>
internal static class ValidationService
{
    /// <summary>
    /// 执行验收
    /// </summary>
    /// <param name="runDir">运行目录</param>
    /// <param name="profilePath">site-context-profile.json 路径</param>
    public static McpComposeResult Validate(string runDir, string profilePath, string? baseline = null, string? pages = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = SiteContextProfile.Load(profilePath);
        var run = Path.GetFullPath(runDir);
        var work = Path.Combine(run, "过程");
        var maps = Path.Combine(work, "地图");
        var inputs = Path.Combine(work, "输入");
        var outDir = Path.Combine(run, "成果");
        var acceptance = profile.Acceptance;

        var destination = Path.Combine(outDir, profile.OutputFileName);
        var package = PptxPackage.Load(destination, cancellationToken);
        var templatePath=Path.Combine(inputs, profile.TemplateFileName);
        var original = PptxPackage.Load(templatePath, cancellationToken);
        if(profile.Template["cadLayoutHash"] is JsonValue reviewedHash && !string.Equals(reviewedHash.GetValue<string>(),SiteJson.HashFile(templatePath),StringComparison.OrdinalIgnoreCase))
            throw new SiteContextException("红线页绑定来自其他 PPT，请按当前模板重新提取");
        var config = MapStyles.Config(work);
        var configPages = (config["ppt"] as JsonObject)?["pages"] as JsonObject
                          ?? throw new SiteContextException("site.config.json 缺少 ppt.pages");
        var lineagePath = Path.Combine(work, "图层对象来源.json");
        if (!File.Exists(lineagePath))
            throw new SiteContextException($"找不到 {lineagePath}；请先执行 action=compose");
        var lineage = SiteJson.LoadFile(lineagePath) as JsonArray ?? [];

        var errors = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed)
                errors.Add(message);
        }

        var selected = pages?.Split(',').Select(x => int.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        if (selected is not null && baseline is null) throw new SiteContextException("指定修订页需要基准 PPTX");
        var reference = baseline is null ? original : PptxPackage.Load(Path.GetFullPath(baseline), cancellationToken);
        var referenceParts = PptxParts.SlideParts(reference);
        var targetParts = PptxParts.SlideParts(package).ToDictionary(x => x.Page, x => x.Part);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var preserved = ProfileJson.IntList(profile.Template, "preservePages").ToHashSet();
        var managed = profile.ComposePages.Concat(profile.CadBaselinePages).ToHashSet();
        foreach (var item in profile.Template["redlinePages"] as JsonArray ?? [])
            managed.Add(item!.GetValue<int>());
        if (profile.Template["cadPageLayout"] is JsonObject layouts)
            foreach (var key in layouts.Select(entry => entry.Key))
                managed.Add(int.Parse(key, System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (page, part) in referenceParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected is not null ? !selected.Contains(page) : preserved.Contains(page))
            {
                Check(targetParts.GetValueOrDefault(page) == part, $"P{page} 页序变化");
                Cover(part);
            }
            else if (baseline is null && !managed.Contains(page) && targetParts.TryGetValue(page, out var targetPart) &&
                     reference.GetBytes(part).AsSpan().SequenceEqual(package.GetBytes(targetPart)))
            {
                Check(false, $"P{page} 仍为模板原页。请按本项目重建，或在确认应原样保留后列入 preservePages。");
            }
        }
        void Cover(string part)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(part))
                return;
            Check(
                package.Contains(part)
                && reference.Contains(part)
                && package.GetBytes(part).AsSpan().SequenceEqual(reference.GetBytes(part)),
                "P1 changed: " + part);
            var relationshipsPart = PptxParts.RelationshipPartPath(part);
            if (!reference.Contains(relationshipsPart))
                return;
            Check(
                package.Contains(relationshipsPart)
                && package.GetBytes(relationshipsPart).AsSpan()
                    .SequenceEqual(reference.GetBytes(relationshipsPart)),
                "P1 relationship changed: " + relationshipsPart);
            foreach (var element in reference.GetXml(relationshipsPart).Root?.Elements() ?? [])
            {
                if (element.Attribute("TargetMode")?.Value == "External")
                    continue;
                var target = element.Attribute("Target")?.Value;
                if (target is null)
                    continue;
                var resolved = PptxParts.Normalize(PartDirectory(part), target);
                if (reference.Contains(resolved))
                    Cover(resolved);
            }
        }

        var viewport = profile.Viewport;
        var screenshot = ProfileJson.Object(acceptance, "screenshot");
        var screenshotWidth = ProfileJson.Integer(screenshot, "width");
        var screenshotHeight = ProfileJson.Integer(screenshot, "height");
        var requiredPrefix = ProfileJson.Text(acceptance, "requireFeatureSourcePrefix");
        var minNativeTexts = ProfileJson.Integer(acceptance, "minNativeTextBoxes");
        var outlinePath = Path.Combine(inputs, "redline-outline.json");
        var allowedCadName = File.Exists(outlinePath) ? "CAD:用地红线:" + SiteJson.Text(SiteJson.LoadObject(outlinePath)["metadata"]?["handle"]) : null;
        var allowedRadii = ProfileJson.IntList(acceptance, "allowedRadii");
        var cropTolerance = ProfileJson.Number(acceptance, "cropAspectTolerance");
        var perPageRules = ProfileJson.OptionalObject(acceptance, "perPage");
        var figure = profile.FigureRect;
        var checkedPages = new List<McpValidationPage>();

        foreach (var page in ProfileJson.IntList(acceptance, "pages").Where(n => selected is null || selected.Contains(n)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capturePath = Path.Combine(maps, $"P{page}.json");
            if (!File.Exists(capturePath))
            {
                Check(false, $"P{page} 缺少采集数据");
                continue;
            }

            var data = SiteJson.LoadObject(capturePath);
            MapStyles.Apply(config,page,data);
            ThematicOverlay.Append(work,page,data);
            var slidePart = targetParts.GetValueOrDefault(page) ?? "";
            if (!package.Contains(slidePart))
            {
                Check(false, $"P{page} 在 pptx 里不存在");
                continue;
            }

            var slide = package.GetXml(slidePart);
            var names = slide.Descendants(PptxNames.P + "cNvPr")
                .Select(element => element.Attribute("name")?.Value ?? "")
                .ToList();
            var pageProfile = data["pageProfile"] as JsonObject;
            Check((int)Math.Round(SiteJson.Number(data["page"])) == page, "Wrong export page");
            var captureViewport = data["viewport"] as JsonObject;
            Check(
                captureViewport is not null
                && (int)Math.Round(SiteJson.Number(captureViewport["width"])) == viewport.Width
                && (int)Math.Round(SiteJson.Number(captureViewport["height"])) == viewport.Height,
                "Wrong viewport");
            var screenshotPath = Path.Combine(maps, $"P{page}-screen.jpg");
            Check(
                File.Exists(screenshotPath)
                && JpegHeader.ReadSize(screenshotPath) == (screenshotWidth, screenshotHeight),
                "Screenshot mismatch");
            var cropOverride = ProfileJson.OptionalArray(profile.PerPage(page), "cropOverride");
            var profileForCompare = pageProfile;
            var configForCompare = configPages[page.ToString()] as JsonObject;
            if (cropOverride is not null && pageProfile is not null && configForCompare is not null)
            {
                profileForCompare = (JsonObject)pageProfile.DeepClone();
                configForCompare = (JsonObject)configForCompare.DeepClone();
                profileForCompare["crop"] = cropOverride.DeepClone();
                configForCompare["crop"] = cropOverride.DeepClone();
            }
            Check(
                JsonEquals(profileForCompare, configForCompare),
                $"P{page} config/export mismatch");

            foreach (var feature in (data["features"] as JsonArray ?? []).OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Check(
                    SiteJson.Text(feature["source"]).StartsWith(requiredPrefix, StringComparison.Ordinal) || SiteJson.Text(feature["source"]) == "专题/registered",
                    $"P{page} non-HTML feature");
                var layer = SiteJson.Text(feature["layer"]);
                var style = pageProfile?["layers"] is JsonObject layers
                    ? layers[layer]
                    : null;
                if (feature["style_key"] is JsonValue key)
                    style = pageProfile?["layerStyles"]?[SiteJson.Text(key)];
                else if (feature["level"] is JsonValue level)
                    style = style?["variants"]?[SiteJson.Text(level)] ?? style;
                if (SiteJson.Text(feature["source"]) == "专题/registered") style = feature["style"]; // binding checked in Append
                var derived = SiteJson.Text(feature["source"]) is "HTML/GEO.waterways" or "HTML/L.waterNames"
                    or "HTML/PXc.expLabels" or "HTML/PX.roadNames" or "HTML/config verified label";
                Check(
                    style is not null && (JsonEquals(feature["style"], style) ||
                        (derived && style["_template"] is not null && JsonEquals(feature["style"]?["_template"], style["_template"]))),
                    $"P{page} style mismatch {SiteJson.Text(feature["id"])}");
            }

            var radius = (int)Math.Round(SiteJson.Number(pageProfile?["radius"]));
            foreach (var name in names.Where(name => name.StartsWith("CAD:", StringComparison.Ordinal)))
                Check(
                    name == allowedCadName && allowedRadii.Contains(radius),
                    $"P{page} forbidden CAD overlay");

            foreach (var item in lineage.OfType<JsonObject>()
                         .Where(item => (int)Math.Round(SiteJson.Number(item["page"])) == page
                                        && item["feature_id"] is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var featureId = SiteJson.Text(item["feature_id"]);
                Check(
                    (data["features"] as JsonArray ?? []).OfType<JsonObject>()
                        .Any(feature => SiteJson.Text(feature["id"]) == featureId),
                    "Untraceable native object");
                if ((string?)item["source"]=="专题/registered")
                    Check((data["features"] as JsonArray ?? []).Any(f => SiteJson.Text(f?["id"])==featureId &&
                        JsonNode.DeepEquals(f?["file_sha256"],item["file_sha256"])), $"P{page} 专题文件已变化，请重新组版");
                var prefix = SiteJson.Text(item["ppt_object_prefix"]);
                Check(names.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)), "Missing native object");
            }

            if (perPageRules?[page.ToString()] is JsonObject rules
                && rules["allowedLayers"] is JsonArray allowedLayerNodes)
            {
                var allowedLayers = allowedLayerNodes.Select(SiteJson.Text).ToHashSet(StringComparer.Ordinal);
                var actualLayers = (data["features"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(feature => SiteJson.Text(feature["layer"]))
                    .Where(layer => layer.Length > 0)
                    .ToHashSet(StringComparer.Ordinal);
                Check(actualLayers.IsSubsetOf(allowedLayers), $"P{page} contains extra layers");
            }

            Check(
                slide.Descendants(PptxNames.P + "txBody").Count() > minNativeTexts,
                "Native texts missing");

            var crop = (pageProfile?["crop"] as JsonArray)
                ?.Select(node => SiteJson.Number(node)).ToList();
            if (crop is { Count: 4 } && crop[2] > crop[0] && crop[3] > crop[1])
            {
                var scaleX = figure.Width / (crop[2] - crop[0]);
                var scaleY = figure.Height / (crop[3] - crop[1]);
                Check(Math.Abs(scaleX - scaleY) <= cropTolerance, $"P{page} crop aspect mismatch");
            }

            checkedPages.Add(new McpValidationPage
            {
                Page = page,
                Features = (data["features"] as JsonArray)?.Count ?? 0,
                NativePaths = slide.Descendants(PptxNames.A + "custGeom").Count()
            });
        }

        foreach (var pair in perPageRules ?? [])
        {
            if (pair.Value is not JsonObject rule)
                continue;
            var rulePage = pair.Key;
            var message = ProfileJson.OptionalText(rule, "message");
            var configPage = configPages[rulePage] as JsonObject;
            if (ProfileJson.OptionalInteger(rule, "radius") is { } expectedRadius)
            {
                Check(
                    configPage is not null
                    && (int)Math.Round(SiteJson.Number(configPage["radius"])) == expectedRadius,
                    message.Length > 0 ? message : $"P{rulePage} radius mismatch");
            }

            if (rule["crop"] is JsonArray expectedCrop)
            {
                var actual = (configPage?["crop"] as JsonArray)
                    ?.Select(node => SiteJson.Number(node)).ToList();
                var expected = expectedCrop.Select(node => SiteJson.Number(node)).ToList();
                Check(
                    actual is not null
                    && actual.Count == expected.Count
                    && actual.Zip(expected).All(item => item.First == item.Second),
                    message.Length > 0 ? message : $"P{rulePage} crop mismatch");
            }
        }

        var slideCount = package.GetXml("ppt/presentation.xml").Root?
            .Element(PptxNames.P + "sldIdLst")?
            .Elements(PptxNames.P + "sldId")
            .Count() ?? 0;
        Check(slideCount == profile.ExpectedSlideCount, "Slide count");

        var contents = SiteJson.LoadObject(Path.Combine(work, "页面内容.json"));
        var missing = new List<string>();
        foreach (var (key,layout) in profile.Template["cadPageLayout"] as JsonObject ?? new JsonObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int number=int.Parse(key,System.Globalization.CultureInfo.InvariantCulture);
            if(selected!=null && !selected.Contains(number))continue;
            var page=contents[key];
            foreach(var gap in page?["remaining_gaps"] as JsonArray ?? []) missing.Add($"P{number} {SiteJson.Text(gap)}");
            var names=package.GetXml(targetParts[number]).Descendants(PptxNames.P+"cNvPr")
                .Select(e=>(string?)e.Attribute("name")).ToHashSet();
            foreach(var field in layout?["reviewFields"] as JsonArray ?? [])
                if(page?[SiteJson.Text(field)] is not (JsonObject {Count:>0} or JsonArray {Count:>0}))
                    missing.Add($"P{number} 缺少已核对的 {field}");
            if(page?["indicators"]!=null || contents["cadParcels"]!=null)
            {
                var raw=Path.Combine(inputs,"cad-geometry.json");
                var cad=File.Exists(outlinePath)?SiteJson.LoadObject(outlinePath):null;
                if(cad?["cadLinework"] is not JsonArray && File.Exists(raw))cad=SiteJson.LoadObject(raw);
                var primary=SiteJson.Text(cad?["metadata"]?["handle"]).ToUpperInvariant();
                var groups=ParcelIndicators.Read(page?["indicators"],primary,(string?)page?["parcelName"]);
                var expected=CadParcelSelection.Resolve(contents["cadParcels"],page as JsonObject,layout!.AsObject(),primary,groups);
                Check(names.Where(n=>n?.StartsWith("CAD:用地红线:")==true).ToHashSet().SetEquals(expected.Keys.Select(h=>"CAD:用地红线:"+h)),$"P{number} 红线面与本页地块清单不符");
                var p=PptxNames.P;
                var a=PptxNames.A;
                var sourcePart=PptxParts.SlideParts(original).Single(s=>s.Page==number).Part;
                var originals=original.GetXml(sourcePart).Descendants(p+"sp").ToList();
                var composed=package.GetXml(targetParts[number]).Descendants(p+"sp").ToList();
                foreach(var (handle,binding) in expected)
                {
                    var sample=originals.SingleOrDefault(e=>(int?)e.Element(p+"nvSpPr")?.Element(p+"cNvPr")?.Attribute("id")==(int?)binding["parcelStyleId"]);
                    var drawn=composed.Where(e=>(string?)e.Element(p+"nvSpPr")?.Element(p+"cNvPr")?.Attribute("name")=="CAD:用地红线:"+handle).ToList();
                    XElement Paint(XElement shape)=>new("paint",shape.Element(p+"spPr")!.Elements().Where(e=>e.Name!=a+"xfrm" && e.Name!=a+"prstGeom" && e.Name!=a+"custGeom"),shape.Element(p+"style"));
                    Check(sample!=null && drawn.Count==1 && XNode.DeepEquals(Paint(sample),Paint(drawn[0])),$"P{number} 地块 {handle} 面样式与绑定不符");
                }
                foreach(var group in groups)
                    Check(ParcelIndicators.Valid(package.GetXml(targetParts[number]),group),$"P{number} 地块 {group.Handle} 指标、覆盖顺序或位置不符");
            }
            foreach(var dataset in page?["cadTopics"] as JsonArray ?? [])
            {
                var name=SiteJson.Text(dataset);
                if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"\A[\p{L}\p{N}_-]{1,80}\z"))throw new SiteContextException("无效 CAD 专题名");
                var file=SiteJson.LoadObject(Path.Combine(work,"CAD专题",name+".json"));
                foreach(var feature in file["features"]!.AsArray())
                    Check(names.Contains("图件:"+name+":"+SiteJson.Text(feature?["properties"]?["class"])),$"P{number} 专题未出图：{name}");
            }
        }
        foreach (var page in checkedPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var gap in contents[page.Page.ToString()]?["remaining_gaps"] as JsonArray ?? [])
                missing.Add($"P{page.Page} {SiteJson.Text(gap)}");
            var shot = SiteJson.LoadObject(Path.Combine(maps, $"P{page.Page}.json"));
            foreach (var warning in shot["captureWarnings"] as JsonArray ?? [])
                missing.Add($"P{page.Page} {SiteJson.Text(warning)}");
            MapStyles.Apply(config,page.Page,shot);
            ThematicOverlay.Append(work,page.Page,shot);
            var layers = (shot["features"] as JsonArray ?? []).Select(f => SiteJson.Text(f?["layer"])).ToHashSet();
            foreach (var layer in shot["pageProfile"]?["enabled"] as JsonArray ?? [])
                if (!layers.Contains(SiteJson.Text(layer)) && !(SiteJson.Text(layer)=="planning" &&
                    configPages[page.Page.ToString()]?["topics"] is JsonObject { Count: >0 } && layers.Contains("resource"))) missing.Add($"P{page.Page} 启用层缺数据：{SiteJson.Text(layer)}");
        }
        var reportFile = Path.Combine(work, "组版检查.json");
        if (File.Exists(reportFile))
            foreach (var gap in SiteJson.LoadObject(reportFile)["remaining_gaps"] as JsonArray ?? [])
                if (!missing.Contains(SiteJson.Text(gap))) missing.Add(SiteJson.Text(gap));
        var completenessPass = missing.Count == 0;
        var result = new McpRunValidation
        {
            GenerationAndProvenancePass = errors.Count == 0,
            Errors = errors,
            CoverDependenciesChecked = seen.Count,
            Pages = checkedPages,
            DataCompletenessPass = completenessPass,
            Missing = missing,
            PptxSha256 = SiteJson.HashFile(destination)
        };

        var reportPath = Path.Combine(work, ProfileJson.Text(acceptance, "reportFileName"));
        var report = new JsonObject
        {
            ["generation_and_provenance_pass"] = result.GenerationAndProvenancePass,
            ["errors"] = SiteJson.StringArray(result.Errors),
            ["cover_dependencies_checked"] = result.CoverDependenciesChecked,
            ["pages"] = PageArray(result.Pages),
            ["data_completeness_pass"] = result.DataCompletenessPass,
            ["missing"] = SiteJson.StringArray(result.Missing),
            ["pptx_sha256"] = result.PptxSha256
        };
        cancellationToken.ThrowIfCancellationRequested();
        SiteJson.SaveFile(reportPath, report);
        result.ReportPath = reportPath;
        return new McpComposeResult { Action = "validate", Validation = result };
    }

    private static JsonArray PageArray(IReadOnlyList<McpValidationPage> pages)
    {
        var array = new JsonArray();
        foreach (var page in pages)
        {
            array.AddNode(new JsonObject
            {
                ["page"] = page.Page,
                ["features"] = page.Features,
                ["native_paths"] = page.NativePaths
            });
        }

        return array;
    }

    private static string PartDirectory(string part)
    {
        var slash = part.LastIndexOf('/');
        return slash < 0 ? "" : part[..slash];
    }

    private static bool Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static bool JsonEquals(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
                return false;
            foreach (var pair in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(pair.Key, out var other))
                    return false;
                if (!JsonEquals(pair.Value, other))
                    return false;
            }

            return true;
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
                return false;
            for (var index = 0; index < leftArray.Count; index++)
            {
                if (!JsonEquals(leftArray[index], rightArray[index]))
                    return false;
            }

            return true;
        }

        var leftNumber = SiteJson.NumberOrNull(left);
        var rightNumber = SiteJson.NumberOrNull(right);
        if (leftNumber is not null && rightNumber is not null)
            return leftNumber.Value.Equals(rightNumber.Value);
        return string.Equals(SiteJson.Text(left), SiteJson.Text(right), StringComparison.Ordinal);
    }
}
