using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Point = TransketchMcpPlugin.ComposeGeometry.Point;

namespace TransketchMcpPlugin;

/// <summary>
/// 把采集成果组版为原生可编辑的 pptx；只允许网页导出要素，CAD 仅作为用地红线
/// </summary>
/// <date>2026年11月16日</date>
internal static class ComposeService
{
    /// <summary>
    /// 执行组版
    /// </summary>
    /// <param name="runDir">运行目录</param>
    /// <param name="profilePath">site-context-profile.json 路径</param>
    /// <param name="baselinePptxPath">修订基准 pptx；为空时基于模板并套用 CAD 基准页</param>
    /// <param name="pages">逗号分隔的页号；非空时必须同时给 baselinePptxPath</param>
    public static McpComposeResult Compose(
        string runDir,
        string profilePath,
        string? baselinePptxPath,
        string? pages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = SiteContextProfile.Load(profilePath);
        var selected = ParsePages(pages) ?? profile.ComposePages;
        if (selected.Count == 0)
            throw new SiteContextException("没有要重排的页；检查 profile 的 compose.pages");
        if (pages is not null && baselinePptxPath is null)
            throw new SiteContextException("指定 pages 时必须同时给 baselinePptxPath；只改部分页会让未改页的来源无法复核");

        if (selected.Except(profile.ComposePages).Any()) throw new SiteContextException("修订页须属于当前组版 profile");
        var composer = new Composer(profile, Path.GetFullPath(runDir), baselinePptxPath, cancellationToken);
        return new McpComposeResult { Action = "compose", Compose = composer.Run(selected) };
    }

    /// <summary>
    /// 解析逗号分隔的页号
    /// </summary>
    /// <param name="pages">页号文本</param>
    private static List<int>? ParsePages(string? pages)
    {
        if (string.IsNullOrWhiteSpace(pages))
            return null;
        var result = new List<int>();
        foreach (var token in pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) || page <= 0)
                throw new SiteContextException($"pages 里的页号不合法：{token}");
            if (!result.Contains(page))
                result.Add(page);
        }

        return result;
    }

    /// <summary>
    /// 单次组版的全部状态
    /// </summary>
    private sealed class Composer
    {
        private const int EmuPerPx = 9525;

        private readonly CancellationToken _cancellationToken;
        private readonly SiteContextProfile _profile;
        private readonly string _work;
        private readonly string _maps;
        private readonly string _inputs;
        private readonly string _outDir;
        private readonly PptxPackage _package;
        private readonly PptxPackage _symbolSource;
        private readonly JsonObject _outline;
        private readonly JsonObject _contents;
        private readonly byte[] _coverBytes;
        private readonly (double X, double Y, double Width, double Height) _figure;
        private readonly double _tolerance;
        private readonly List<string> _warnings = [];
        private readonly JsonArray _lineage = [];
        private readonly List<McpComposePageReport> _pageReports = [];
        private readonly HashSet<string> _mediaExtensions = new(StringComparer.Ordinal);
        private readonly List<(double X, double Y, double Width, double Height)> _placed = [];

        private int _page;
        private JsonObject _data = new();
        private JsonObject _pageProfile = new();
        private PptxSlideWriter _writer = null!;
        private XDocument _relationships = null!;
        private string _slidePart = "";
        private string _relationshipsPart = "";
        private double[] _crop = [];
        private double _scaleX;
        private double _scaleY;
        private int _mediaCounter;

        public Composer(SiteContextProfile profile, string run, string? baseline, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _profile = profile;
            _work = Path.Combine(run, "过程");
            _maps = Path.Combine(_work, "地图");
            _inputs = Path.Combine(_work, "输入");
            _outDir = Path.Combine(run, "成果");
            _figure = profile.FigureRect;
            _tolerance = profile.CropAspectTolerance;

            var templatePath = Path.Combine(_inputs, profile.TemplateFileName);
            if (!File.Exists(templatePath))
                throw new SiteContextException($"找不到模板：{templatePath}");
            var cadBaselinePath = Path.Combine(_inputs, profile.CadBaselineFileName);
            var config = MapStyles.Config(_work);
            var digest = SiteJson.HashFile(templatePath);
            if (!string.Equals(digest, SiteJson.Text(config["ppt"]?["template_sha256"]), StringComparison.OrdinalIgnoreCase))
                throw new SiteContextException("模板与项目配置的 SHA-256 不匹配");
            if(profile.Template["cadLayoutHash"] is JsonValue layoutHash && !string.Equals(layoutHash.GetValue<string>(),digest,StringComparison.OrdinalIgnoreCase))
                throw new SiteContextException("红线页绑定来自其他 PPT，请按当前模板重新提取");
            CheckStyleHashes(config["ppt"]?["pages"], digest);
            _package = PptxPackage.Load(baseline is null ? templatePath : Path.GetFullPath(baseline), _cancellationToken);
            _coverBytes = _package.GetBytes("ppt/slides/slide1.xml");
            _symbolSource = PptxPackage.Load(templatePath, _cancellationToken);

            _contents = SiteJson.LoadObject(Path.Combine(_work, "页面内容.json"));

            var outlinePath = Path.Combine(_inputs, "redline-outline.json");
            _outline = File.Exists(outlinePath) ? SiteJson.LoadObject(outlinePath) : new JsonObject();
            var nativePages = new HashSet<int>();
            if (baseline is null && profile.Template["cadPageLayout"] is JsonObject layouts)
            {
                var rawPath = Path.Combine(_inputs, "cad-geometry.json");
                var raw = File.Exists(rawPath) ? SiteJson.LoadObject(rawPath) : null;
                if (raw is not null && _outline["metadata"]?["sha256"] is JsonValue registeredHash &&
                    (SiteJson.Text(registeredHash) != SiteJson.Text(raw["metadata"]?["sha256"]) ||
                     SiteJson.Text(_outline["metadata"]?["handle"]) != SiteJson.Text(raw["metadata"]?["handle"])))
                    throw new SiteContextException("CAD 底图和配准红线来自不同图纸或实体，请核对项目输入");
                var cad = _outline["cadLinework"] is JsonArray ? _outline : raw ?? _outline;
                if (cad["cadLinework"] is JsonArray)
                    foreach (var (key, layout) in layouts)
                    {
                        int page = int.Parse(key, CultureInfo.InvariantCulture);
                        var part = PptxParts.SlideParts(_package).Single(s => s.Page == page).Part;
                        var xml = _package.GetXml(part);
                        var writer = new PptxSlideWriter(xml, ProfileJson.Text(profile.Layout, "fontFamily"));
                        var lines = CadPage.Draw(writer, layout!.AsObject(), cad, _contents[key] as JsonObject, _work, _contents["cadParcels"], _cancellationToken);
                        if (lines == 0) _warnings.Add($"P{page} 当前图幅没有可绘制的 CAD 底图线");
                        _package.SetXml(part, xml);
                        nativePages.Add(page);
                        _lineage.Add((JsonNode)new JsonObject { ["page"] = page, ["source"] = "CAD/DWG", ["metadata"] = cad["metadata"]!.DeepClone(), ["cadLineCount"] = lines });
                    }
            }
            var baselinePages = profile.CadBaselinePages.Except(nativePages).ToList();
            if (baseline is null && baselinePages.Count > 0) ApplyCadBaseline(cadBaselinePath, baselinePages);
            if (baseline is null && profile.Template["redlinePages"] is JsonArray redlinePages)
            {
                foreach (var number in redlinePages)
                {
                    var n = (int)number!;
                    if (nativePages.Contains(n) || baselinePages.Contains(n)) continue;
                    var part = PptxParts.SlideParts(_package).Single(item => item.Page == n).Part;
                    var xml = _package.GetXml(part);
                    var writer = new PptxSlideWriter(xml, ProfileJson.Text(profile.Layout, "fontFamily"));
                    writer.Tree.Elements().Where(e => e.Name != PptxNames.P + "nvGrpSpPr" && e.Name != PptxNames.P + "grpSpPr").Remove();
                    writer.Text("redline-pending", "待补本项目红线图纸解析及组版", 120, 380, 1680, 180, 32, "373737");
                    _package.SetXml(part, xml);
                    _warnings.Add($"P{n} 待补本项目红线图纸解析及组版");
                }
            }
        }

        private static void CheckStyleHashes(JsonNode? node, string digest)
        {
            if (node is JsonObject obj)
            {
                if (obj["_template"] is JsonObject style && !string.Equals(SiteJson.Text(style["sha256"]), digest, StringComparison.OrdinalIgnoreCase))
                    throw new SiteContextException("样式引用的模板 SHA-256 不匹配，请重新编译配置");
                foreach (var item in obj) CheckStyleHashes(item.Value, digest);
            }
            else if (node is JsonArray list)
                foreach (var item in list) CheckStyleHashes(item, digest);
        }

        public McpComposeReport Run(IReadOnlyList<int> selected)
        {
            var ordered = selected.OrderBy(page => page).ToList();
            foreach (var page in ordered)
                ComposePage(page);

            EnsureContentTypes();
            Directory.CreateDirectory(_outDir);
            var outputPath = Path.Combine(_outDir, _profile.OutputFileName);
            var pending = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                _package.Save(pending, _cancellationToken);
                _cancellationToken.ThrowIfCancellationRequested();
                File.Move(pending, outputPath, true);
            }
            finally { File.Delete(pending); }

            var coverIdentical = _package.GetBytes("ppt/slides/slide1.xml").AsSpan().SequenceEqual(_coverBytes);
            var remainingGaps = BuildRemainingGaps(ordered);
            remainingGaps.AddRange(_warnings);
            var reportPath = Path.Combine(_work, "组版检查.json");
            var lineagePath = Path.Combine(_work, "图层对象来源.json");

            var check = new JsonObject
            {
                ["cover_xml_identical"] = coverIdentical,
                ["pages"] = PageReportArray(),
                ["remaining_gaps"] = SiteJson.StringArray(remainingGaps),
                ["selected_pages"] = IntArray(ordered)
            };
            SiteJson.SaveFile(reportPath, check);
            SiteJson.SaveFile(lineagePath, _lineage);

            return new McpComposeReport
            {
                PptxPath = outputPath,
                PptxSha256 = SiteJson.HashFile(outputPath),
                SelectedPages = ordered,
                CoverXmlIdentical = coverIdentical,
                Pages = _pageReports,
                RemainingGaps = remainingGaps,
                Warnings = _warnings,
                LineagePath = lineagePath,
                ReportPath = reportPath
            };
        }

        private void ApplyCadBaseline(string cadBaselinePath, IReadOnlyList<int> pages)
        {
            var cadBaseline = PptxPackage.Load(cadBaselinePath, _cancellationToken);
            foreach (var page in pages)
            {
                foreach (var part in new[]
                         {
                             $"ppt/slides/slide{page}.xml",
                             $"ppt/slides/_rels/slide{page}.xml.rels",
                             $"ppt/notesSlides/notesSlide{page}.xml"
                         })
                {
                    if (cadBaseline.Contains(part))
                        _package.CopyPartFrom(cadBaseline, part);
                }

                var relationshipsPart = $"ppt/slides/_rels/slide{page}.xml.rels";
                if (!cadBaseline.Contains(relationshipsPart))
                    continue;
                foreach (var element in cadBaseline.GetXml(relationshipsPart).Root?.Elements() ?? [])
                {
                    if (!(element.Attribute("Type")?.Value ?? "").EndsWith("/image", StringComparison.Ordinal))
                        continue;
                    var target = element.Attribute("Target")?.Value;
                    if (target is not null)
                        _package.CopyPartFrom(cadBaseline, PptxParts.Normalize("ppt/slides", target));
                }
            }
        }

        private void ComposePage(int page)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _page = page;
            _placed.Clear();
            _data = SiteJson.LoadObject(Path.Combine(_maps, $"P{page}.json"));
            MapStyles.Apply(MapStyles.Config(_work),page,_data);
            _pageProfile=_data["pageProfile"]!.AsObject();
            if (_data["error"] is not null || (int?)_data["page"]!=page) throw new SiteContextException($"P{page} 采集无效");
            ThematicOverlay.Append(_work,page,_data);
            CheckViewport();
            ReadCrop();

            _slidePart = PptxParts.SlideParts(_package).Single(item => item.Page == page).Part;
            _relationshipsPart = PptxParts.RelationshipPartPath(_slidePart);
            if (!_package.Contains(_slidePart))
                throw new SiteContextException($"模板缺少第 {page} 页：{_slidePart}");
            var slide = _package.GetXml(_slidePart);
            _writer = new PptxSlideWriter(slide, ProfileJson.Text(_profile.Layout, "fontFamily"));
            _relationships = _package.Contains(_relationshipsPart)
                ? _package.GetXml(_relationshipsPart)
                : new XDocument(new XElement(PptxNames.Rel + "Relationships"));

            RemoveOtherObjects();
            var content = _contents[page.ToString()] as JsonObject
                          ?? throw new SiteContextException($"页面内容.json 缺少第 {page} 页");
            foreach (var key in new[] { "figure", "sub", "body", "caption", "attribution" })
                if (content[key] is not JsonValue v || !v.TryGetValue<string>(out _))
                    throw new SiteContextException($"P{page} 页面内容字段 {key} 应为字符串");
            WriteTextBlocks(page, content);
            AddBasemap(page);

            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            var labels = AddFeatures(page, used);
            AddCadOverlay();
            AddProjectMarker(page, labels);
            AddNameLabels(page, labels);
            PlaceLabels(labels);
            WriteLegend();
            WritePageExtras(page, content);

            _package.SetXml(_slidePart, slide);
            if (_package.Contains(_relationshipsPart))
                _package.SetXml(_relationshipsPart, _relationships);
            else
                _package.AddPart(_relationshipsPart, PptxNames.Serialize(_relationships));

            WriteNotes(page, content);

            _pageReports.Add(new McpComposePageReport
            {
                Page = page,
                RadiusM = (int)Math.Round(SiteJson.Number(_pageProfile["radius"])),
                Crop = _crop.Select(value => (int)Math.Round(value)).ToList(),
                HtmlNativeObjects = used,
                CadOnlyParcelOutline = Bool(_pageProfile["redline"]),
                SourceFeatures = (_data["features"] as JsonArray)?.Count ?? 0,
                EditablePaths = slide.Descendants(PptxNames.A + "custGeom").Count(),
                NativeTexts = slide.Descendants(PptxNames.P + "txBody").Count()
            });
        }

        private void CheckViewport()
        {
            var viewport = _profile.Viewport;
            var actual = _data["viewport"] as JsonObject;
            if (actual is null
                || (int)Math.Round(SiteJson.Number(actual["width"])) != viewport.Width
                || (int)Math.Round(SiteJson.Number(actual["height"])) != viewport.Height)
            {
                throw new SiteContextException(
                    $"P{_page} 采集视口不是 {viewport.Width}x{viewport.Height}；请重新采集");
            }
        }

        private void ReadCrop()
        {
            _crop = ProfileJson.NumberList(_pageProfile, "crop").ToArray();
            var overrideCrop = ProfileJson.OptionalArray(_profile.PerPage(_page), "cropOverride");
            if (overrideCrop is not null)
                _crop = overrideCrop.Select(item => SiteJson.Number(item)).ToArray();
            if (_crop.Length != 4 || _crop[2] <= _crop[0] || _crop[3] <= _crop[1])
                throw new SiteContextException($"P{_page} 的 crop 非法：{string.Join(',', _crop)}");

            var scaleX = _figure.Width / (_crop[2] - _crop[0]);
            var scaleY = _figure.Height / (_crop[3] - _crop[1]);
            if (Math.Abs(scaleX - scaleY) > _tolerance)
            {
                throw new SiteContextException(
                    $"P{_page} 底图裁切长宽比与分析图框不一致：{scaleX:F4} 对 {scaleY:F4}，容差 {_tolerance}；" +
                    "请把 crop 调成与分析图框同比例");
            }

            _scaleX = scaleX;
            _scaleY = scaleY;
        }

        private void RemoveOtherObjects()
        {
            var keepContains = ProfileJson.Text(_profile.Compose, "keepExistingTextContains");
            var maxLength = ProfileJson.Integer(_profile.Compose, "keepExistingTextMaxLength");
            foreach (var shape in _writer.Tree.Elements().Skip(2).ToList())
            {
                var text = string.Concat(shape.Descendants(PptxNames.A + "t").Select(element => element.Value));
                if (!text.Contains(keepContains, StringComparison.Ordinal) || text.Length > maxLength)
                    shape.Remove();
            }
        }

        private void WriteTextBlocks(int page, JsonObject content)
        {
            var layout = _profile.Layout;
            WriteLayoutText("section", ProfileJson.Text(content, "sub"), ProfileJson.Object(layout, "section"));
            WriteLayoutText("analysis", ProfileJson.Text(content, "body"), ProfileJson.Object(layout, "body"));
            WriteLayoutText("figure-title", ProfileJson.Text(content, "figure"), ProfileJson.Object(layout, "figureTitle"));

            if (Bool(_profile.PerPage(page)["poiIndex"]))
            {
                var entries = new List<string>();
                foreach (var node in _data["features"] as JsonArray ?? [])
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    if (node is not JsonObject feature)
                        continue;
                    var number = SiteJson.Text(feature["number"]);
                    if (IsTruthy(feature["number"]))
                        entries.Add(number + "  " + SiteJson.Text(feature["name"]));
                }

                var wrapped = string.Join("\n\n", entries.Select(entry => string.Join("\n", Wrap(entry, 16))));
                WriteLayoutText("poi-index", wrapped, ProfileJson.Object(layout, "poiIndex"));
            }

            if ((ProfileJson.OptionalText(content, "sideText") ?? ProfileJson.OptionalText(_profile.PerPage(page), "planningStatus")) is { Length: > 0 } planning)
                WriteLayoutText("planning-status", planning, ProfileJson.Object(layout, "planningStatus"));

            if ((content["sideImage"] as JsonObject ?? ProfileJson.OptionalObject(_profile.PerPage(page), "historicalImage")) is { } historical)
            {
                var imagePath = Path.Combine(_work, (ProfileJson.OptionalText(historical, "file") ?? ProfileJson.Text(historical, "fileName")));
                if (!File.Exists(imagePath))
                    throw new SiteContextException($"找不到历史影像：{imagePath}");
                var rect = ProfileJson.Object(layout, "historicalImage");
                var extension = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
                var relationshipId = AddMedia($"site-history-{page}", extension, File.ReadAllBytes(imagePath));
                _writer.Picture(
                    relationshipId,
                    ProfileJson.Number(rect, "x"),
                    ProfileJson.Number(rect, "y"),
                    ProfileJson.Number(rect, "width"),
                    ProfileJson.Number(rect, "height"));
                WriteLayoutText(
                    "historical-image-caption",
                    ProfileJson.Text(historical, "caption"),
                    ProfileJson.Object(layout, "historicalCaption"));
            }
        }

        private void WriteLayoutText(string name, string text, JsonObject box) =>
            _writer.Text(
                name,
                text,
                ProfileJson.Number(box, "x"),
                ProfileJson.Number(box, "y"),
                ProfileJson.Number(box, "width"),
                ProfileJson.Number(box, "height"),
                ProfileJson.Number(box, "pt"),
                Color(ProfileJson.Text(box, "color")));

        private void AddBasemap(int page)
        {
            var viewport = _profile.Viewport;
            var screenshotPath = Path.Combine(_maps, $"P{page}-screen.jpg");
            if (!File.Exists(screenshotPath))
                throw new SiteContextException($"找不到截图：{screenshotPath}");
            var bytes = File.ReadAllBytes(screenshotPath);
            var size = JpegHeader.ReadSize(bytes);
            if (size.Width != viewport.Width || size.Height != viewport.Height)
            {
                throw new SiteContextException(
                    $"P{page} 截图尺寸是 {size.Width}x{size.Height}，应为 {viewport.Width}x{viewport.Height}");
            }

            if (_data["error"] is not null)
                throw new SiteContextException($"P{page} 采集数据带 error：{SiteJson.Text(_data["error"])}；请重新采集");

            var minBytes = ProfileJson.Number(
                ProfileJson.Object(_profile.Acceptance, "screenshot"), "minBytes");
            if (bytes.Length < minBytes)
                _warnings.Add($"P{page} 截图只有 {bytes.Length} 字节，可能未加载底图；阈值 {(long)minBytes}。");

            var relationshipId = AddMedia($"site-p{page}", "jpeg", bytes);
            double width = viewport.Width;
            double height = viewport.Height;
            _writer.Picture(
                relationshipId,
                _figure.X,
                _figure.Y,
                _figure.Width,
                _figure.Height,
                (_crop[0] / width, _crop[1] / height, (width - _crop[2]) / width, (height - _crop[3]) / height));
        }

        private List<(string Text, double X, double Y, JsonObject Style, string Name)> AddFeatures(
            int page,
            Dictionary<string, int> used)
        {
            var labels = new List<(string Text, double X, double Y, JsonObject Style, string Name)>();
            foreach (var node in _data["features"] as JsonArray ?? [])
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (node is not JsonObject feature)
                    continue;
                var features = ReadFeature(page, feature);
                var objects = new List<XElement>();
                switch (features.Kind)
                {
                    case "polygon":
                        var polygon = ComposeGeometry.ClipPolygon(features.Points, _crop[0], _crop[1], _crop[2], _crop[3]);
                        if (polygon.Count > 2)
                        {
                            var holes = new List<List<(double X, double Y)>>();
                            foreach (var ring in (feature["rings"] as JsonArray ?? []).Skip(1))
                            {
                                var hole = ComposeGeometry.ClipPolygon(ReadPoints(ring), _crop[0], _crop[1], _crop[2], _crop[3]);
                                if (hole.Count > 2) holes.Add(Transform(hole));
                            }
                            objects.Add(_writer.Path(features.Name, Transform(polygon), features.StrokeColor, 1, features.FillColor, true, holes));
                        }
                        break;
                    case "line":
                        var chunks = ComposeGeometry.ClipLine(features.Points, _crop[0], _crop[1], _crop[2], _crop[3]);
                        for (var index = 0; index < chunks.Count; index++)
                        {
                            objects.Add(_writer.Path(
                                features.Name + "-" + index,
                                Transform(chunks[index]),
                                features.StrokeColor,
                                1,
                                null,
                                false));
                        }

                        break;
                    case "point":
                        if (features.Points.Count > 0 && Inside(features.Points[0]))
                        {
                            var (x, y) = Transform(features.Points[0]);
                            var size = SiteJson.Number(features.Style["size"], 10) * _scaleX;
                            objects.Add(_writer.Shape(
                                features.Name,
                                x - size / 2,
                                y - size / 2,
                                size,
                                size,
                                features.FillColor,
                                features.StrokeColor,
                                "ellipse"));
                        }

                        break;
                }

                var featureWidth = SiteJson.NumberOrNull(feature["width"]);
                foreach (var shape in objects)
                {
                    StyleObject(shape, features.Style, featureWidth);
                    used[features.Layer] = used.GetValueOrDefault(features.Layer) + 1;
                }

                if (objects.Count == 0)
                    continue;

                if (features.Kind == "point")
                    AddSymbol(features.Layer, features.Points[0]);

                _lineage.AddNode(new JsonObject
                {
                    ["page"] = page,
                    ["ppt_object_prefix"] = features.Name,
                    ["feature_id"] = features.FeatureId,
                    ["source"] = features.Source,
                    ["file_sha256"] = feature["file_sha256"]?.DeepClone(),
                    ["file"] = feature["file"]?.DeepClone(),
                    ["source_feature_id"] = feature["source_feature_id"]?.DeepClone(),
                    ["evidence"] = feature["evidence"]?.DeepClone(),
                    ["layer"] = features.Layer
                });

                if (features.Kind == "point")
                {
                    var text = IsTruthy(feature["number"])
                        ? SiteJson.Text(feature["number"])
                        : SiteJson.Text(feature["name"]);
                    labels.Add((text, features.Points[0].X, features.Points[0].Y, features.Style, features.Name));
                }
                else if (feature["labelAt"] is JsonArray labelAt && labelAt.Count >= 2)
                {
                    labels.Add((
                        SiteJson.Text(feature["name"]),
                        SiteJson.Number(labelAt[0]),
                        SiteJson.Number(labelAt[1]),
                        features.Style,
                        features.Name));
                }
            }

            return labels;
        }

        private Feature ReadFeature(int page, JsonObject feature)
        {
            var source = SiteJson.Text(feature["source"]);
            if (!source.StartsWith("HTML/", StringComparison.Ordinal) && source != "专题/registered")
            {
                throw new SiteContextException(
                    $"P{page} 的要素缺少 HTML/ 来源前缀：{source}（{SiteJson.Text(feature["id"])}）");
            }



            var style = feature["style"] as JsonObject
                        ?? throw new SiteContextException($"P{page} 要素 {SiteJson.Text(feature["id"])} 缺少 style");
            var kind = SiteJson.Text(feature["kind"]);
            var fill = style["fill"] as JsonObject;
            var stroke = style["stroke"] as JsonObject;
            var fillColor = SiteJson.Number(fill?["opacity"]) > 0 && kind != "line"
                ? Color(SiteJson.Text(fill?["color"]))
                : null;

            return new Feature
            {
                Source = source,
                FeatureId = SiteJson.Text(feature["id"]),
                Name = "HTML:" + SiteJson.Text(feature["id"]),
                Layer = SiteJson.Text(feature["layer"]),
                Kind = kind,
                Style = style,
                Points = ReadPoints(feature["xy"]),
                FillColor = fillColor,
                StrokeColor = Color(SiteJson.Text(stroke?["color"]))
            };
        }

        private void AddSymbol(string layer, Point point)
        {
            if (_pageProfile["symbols"] is not JsonObject symbols || symbols[layer] is not JsonObject symbol)
                return;

            var page = ProfileJson.Integer(symbol, "page");
            var shapeId = ProfileJson.Text(symbol, "shape_id");
            var slide = _symbolSource.GetXml($"ppt/slides/slide{page}.xml");
            var picture = slide
                .Descendants(PptxNames.P + "pic")
                .FirstOrDefault(candidate => candidate
                    .Element(PptxNames.P + "nvPicPr")?
                    .Element(PptxNames.P + "cNvPr")?
                    .Attribute("id")?.Value == shapeId)
                ?? throw new SiteContextException($"模板第 {page} 页找不到符号 shape_id={shapeId}");

            var oldId = picture.Descendants(PptxNames.A + "blip").FirstOrDefault()?.Attribute(PptxNames.R + "embed")?.Value
                        ?? throw new SiteContextException($"符号 picture 缺少 blip：shape_id={shapeId}");
            var symbolRelationships = _symbolSource.GetXml($"ppt/slides/_rels/slide{page}.xml.rels");
            var target = symbolRelationships.Root?.Elements()
                .FirstOrDefault(element => element.Attribute("Id")?.Value == oldId)?
                .Attribute("Target")?.Value
                ?? throw new SiteContextException($"符号关系缺失：{oldId}");
            var part = PptxParts.Normalize("ppt/slides", target);
            var extension = Path.GetExtension(part).TrimStart('.').ToLowerInvariant();
            var relationshipId = AddMedia(
                $"site-symbol-{_page}-{layer}",
                extension,
                _symbolSource.GetBytes(part));

            var (x, y) = Transform(point);
            var width = ProfileJson.Number(symbol, "width");
            var height = ProfileJson.Number(symbol, "height");
            _writer.Picture(relationshipId, x - width / 2, y - height / 2, width, height);
        }

        private void AddCadOverlay()
        {
            if (!Bool(_pageProfile["redline"]))
                return;

            var radius = (int)Math.Round(SiteJson.Number(_pageProfile["radius"]));
            if (!ProfileJson.IntList(_profile.Acceptance, "allowedRadii").Contains(radius))
                throw new SiteContextException($"P{_page} 视野半径 {radius} 不在允许值内");

            var style = _pageProfile["parcelOutlineStyle"] as JsonObject
                        ?? throw new SiteContextException($"P{_page} 缺少 parcelOutlineStyle");
            var handle = ProfileJson.Text(ProfileJson.Object(_outline, "metadata"), "handle");
            var stroke = style["stroke"] as JsonObject;
            var shape = _writer.Path(
                "CAD:用地红线:" + handle,
                OutlinePoints(),
                Color(SiteJson.Text(stroke?["color"])),
                1,
                null,
                true);
            StyleObject(shape, style, null);
            _lineage.AddNode(new JsonObject
            {
                ["page"] = _page,
                ["ppt_object_prefix"] = "CAD:用地红线:" + handle,
                ["source"] = "DWG:" + handle,
                ["layer"] = "parcel-outline"
            });
        }

        private List<(double X, double Y)> OutlinePoints()
        {
            var centroid = _outline["metadata"]?["centroid"] as JsonArray
                           ?? throw new SiteContextException("redline-outline.json 缺少 metadata.centroid");
            if (_outline["metadata"]?["project"] is JsonObject registered)
            {
                var site = _data["site"] as JsonObject ?? throw new SiteContextException("地图快照缺少基地位置");
                if (Math.Abs(ProfileJson.Number(site, "lng") - ProfileJson.Number(registered, "lng")) > 1e-7 ||
                    Math.Abs(ProfileJson.Number(site, "lat") - ProfileJson.Number(registered, "lat")) > 1e-7)
                    throw new SiteContextException("地图中心与配准后的红线中心不符，请用红线工具返回的 project 重新采集");
            }
            var centerX = SiteJson.Number(centroid[0]);
            var centerY = SiteJson.Number(centroid[1]);
            var origin = _data["p0"] as JsonObject
                         ?? throw new SiteContextException($"P{_page} 采集数据缺少 p0");
            var metersPerPixel = ProfileJson.Number(_data, "mpp");
            var originX = ProfileJson.Number(origin, "x");
            var originY = ProfileJson.Number(origin, "y");

            var points = new List<(double X, double Y)>();
            foreach (var node in _outline["points"] as JsonArray
                     ?? throw new SiteContextException("redline-outline.json 缺少 points"))
            {
                if (node is not JsonArray pair || pair.Count < 2)
                    continue;
                var x = SiteJson.Number(pair[0]);
                var y = SiteJson.Number(pair[1]);
                points.Add(Transform(originX + (x - centerX) / metersPerPixel,
                    originY - (y - centerY) / metersPerPixel));
            }

            return points;
        }

        private void AddProjectMarker(
            int page,
            List<(string Text, double X, double Y, JsonObject Style, string Name)> labels)
        {
            var marker = ProfileJson.Object(_profile.Compose, "projectMarker");
            if (!ProfileJson.IntList(marker, "pages").Contains(page))
                return;
            if ((_data["features"] as JsonArray ?? []).OfType<JsonObject>()
                .Any(feature => SiteJson.Text(feature["layer"]) == "project"))
                return;

            var origin = _data["p0"] as JsonObject
                         ?? throw new SiteContextException($"P{page} 采集数据缺少 p0");
            var originX = ProfileJson.Number(origin, "x");
            var originY = ProfileJson.Number(origin, "y");
            var layer = ProfileJson.Object(_pageProfile, "layers")["project"] as JsonObject
                        ?? throw new SiteContextException($"P{page} 缺少 project 图层样式");
            var fill = layer["fill"] as JsonObject;
            var size = ProfileJson.Number(marker, "sizePx");
            var (x, y) = Transform(new Point(originX, originY));
            _writer.Shape(
                ProfileJson.Text(marker, "objectName"),
                x - size / 2,
                y - size / 2,
                size,
                size,
                Color(SiteJson.Text(fill?["color"])),
                null,
                "ellipse");
            labels.Add((ProfileJson.Text(marker, "label"), originX, originY, layer, ProfileJson.Text(marker, "objectName")));
        }

        private void AddNameLabels(
            int page,
            List<(string Text, double X, double Y, JsonObject Style, string Name)> labels)
        {
            var nameLabels = ProfileJson.OptionalObject(_profile.PerPage(page), "nameLabels");
            if (nameLabels is null)
                return;
            var source = ResolvePath(_data, ProfileJson.Text(nameLabels, "source"));
            var layer = ProfileJson.Object(_pageProfile, "layers")[ProfileJson.Text(nameLabels, "layer")] as JsonObject
                        ?? throw new SiteContextException($"P{page} 缺少 {ProfileJson.Text(nameLabels, "layer")} 图层样式");
            var max = ProfileJson.Integer(nameLabels, "max");
            var minDistance = ProfileJson.Number(nameLabels, "minDistance");
            var count = 0;
            foreach (var node in source as JsonArray ?? [])
            {
                if (node is not JsonObject item)
                    continue;
                var x = ProfileJson.Number(item, "x");
                var y = ProfileJson.Number(item, "y");
                var name = ProfileJson.Text(ProfileJson.Object(item, "src"), "name");
                if (!Inside(new Point(x, y)))
                    continue;
                if (labels.Any(label => ComposeGeometry.Distance(
                        new Point(x, y),
                        new Point(label.X, label.Y)) < minDistance))
                    continue;
                labels.Add((name, x, y, layer, "HTML:name-" + count));
                count++;
                if (count >= max)
                    break;
            }
        }

        private void PlaceLabels(
            List<(string Text, double X, double Y, JsonObject Style, string Name)> labels)
        {
            var offsets = ProfileJson.OptionalObject(_profile.PerPage(_page), "labelOffsets");
            var fallback = ProfileJson.Object(_profile.Compose, "labelDefaultOffset");
            foreach (var label in labels)
            {
                var dx = ProfileJson.Number(fallback, "dx");
                var dy = ProfileJson.Number(fallback, "dy");
                if (offsets?[label.Text] is JsonArray pair && pair.Count >= 2)
                {
                    dx = SiteJson.Number(pair[0]);
                    dy = SiteJson.Number(pair[1]);
                }

                Label(label.Text, label.X, label.Y, label.Style, label.Name, dx, dy);
            }
        }

        private void Label(string text, double x, double y, JsonObject style, string name, double dx, double dy)
        {
            if (text.Length == 0 || !Inside(new Point(x, y)))
                return;
            var labelStyle = style["label"] as JsonObject ?? new JsonObject();
            if (labelStyle["show"] is JsonValue showValue
                && showValue.TryGetValue<bool>(out var show)
                && !show)
                return;

            var pointSize = SiteJson.Number(labelStyle["size"], 16) * _scaleX * 0.75;
            pointSize *= ProfileJson.OptionalNumber(_profile.PerPage(_page), "labelScale", 1);
            var (px, py) = Transform(new Point(x, y));
            var width = Math.Max(55, text.Length * pointSize * 1.34 + 18);
            var height = pointSize * 1.7 + 8;
            px += dx;
            py += dy;
            px = Math.Min(Math.Max(_figure.X + 4, px), _figure.X + _figure.Width - width - 4);
            py = Math.Min(Math.Max(_figure.Y + 4, py), _figure.Y + _figure.Height - height - 4);

            var shifts = _page == 2
                ? new[] { 0.0 }
                : ProfileJson.NumberList(_profile.Compose, "labelShift").ToArray();
            foreach (var shift in shifts)
            {
                var candidate = Math.Min(
                    Math.Max(_figure.Y + 3, py + shift),
                    _figure.Y + _figure.Height - height - 3);
                if (_placed.All(existing =>
                        px + width < existing.X || px > existing.X + existing.Width
                        || candidate + height < existing.Y || candidate > existing.Y + existing.Height))
                {
                    py = candidate;
                    break;
                }
            }

            _placed.Add((px, py, width, height));
            var box = SiteJson.Text(labelStyle["box"]);
            if (box.Length == 0)
                box = ProfileJson.Text(_profile.Compose, "labelBoxDefault");
            var background = "1E1C11";
            var opacity = 0.4;
            if (box.StartsWith("rgba(", StringComparison.Ordinal))
            {
                var values = box[5..^1].Split(',');
                background = string.Concat(values.Take(3).Select(value =>
                    ((int)double.Parse(value, CultureInfo.InvariantCulture)).ToString("X2", CultureInfo.InvariantCulture)));
                opacity = double.Parse(values[3], CultureInfo.InvariantCulture);
            }

            var shape = _writer.Shape(name + "-label-background", px, py, width, height, background);
            Alpha(
                shape.Element(PptxNames.P + "spPr")?
                    .Element(PptxNames.A + "solidFill")?
                    .Element(PptxNames.A + "srgbClr"),
                opacity);
            var color = SiteJson.Text(labelStyle["color"]);
            _writer.Text(
                name + "-label",
                text,
                px + 7,
                py + 2,
                width - 9,
                height,
                pointSize,
                Color(color.Length > 0 ? color : "#FFFFFF"));
        }

        private void WritePageExtras(int page, JsonObject content)
        {
            var layout = _profile.Layout;
            WriteLayoutText("caption", ProfileJson.Text(content, "caption"), ProfileJson.Object(layout, "caption"));

            var attribution = ProfileJson.Text(content, "attribution");
            WriteLayoutText("attribution", attribution, ProfileJson.Object(layout, "attribution"));
            WriteLayoutText("page-number", page.ToString("00", CultureInfo.InvariantCulture),
                ProfileJson.Object(layout, "pageNumber"));
        }

        private void WriteLegend()
        {
            var layout = ProfileJson.Object(_profile.Layout, "legend");
            var layers = ProfileJson.Object(_pageProfile, "layers");
            var names = ProfileJson.Object(_profile.Compose, "legendLabels");
            var keys = _pageProfile["legendKeys"] is JsonArray configured
                ? configured.Select(key => SiteJson.Text(key)).ToList()
                : (_profile.PerPage(_page)["legendKeys"] as JsonArray ?? []).Select(item => SiteJson.Text(item)).ToList();
            var lineKeys = new HashSet<string>(StringComparer.Ordinal) { "road", "express", "ring" };
            var startX = ProfileJson.Number(layout, "x");
            var labelX = ProfileJson.Number(layout, "labelX");
            var startY = ProfileJson.Number(layout, "yStart");
            var step = ProfileJson.Number(layout, "step");
            var labelHeight = ProfileJson.Number(layout, "labelHeight");
            var labelOffsetY = ProfileJson.Number(layout, "labelOffsetY");
            var labelWidth = ProfileJson.Number(layout, "labelWidth");
            var labelPointSize = ProfileJson.Number(layout, "labelPt");

            for (var index = 0; index < keys.Count; index++)
            {
                var key = keys[index];
                var style = layers[key] as JsonObject
                            ?? throw new SiteContextException($"P{_page} 的图层 {key} 没有样式");
                var y = startY + index * step;
                if (key == "ring")
                {
                    var contrast = ProfileJson.Object(layout, "ringContrast");
                    _writer.Shape(
                        "legend-ring-contrast",
                        ProfileJson.Number(contrast, "x"),
                        y + ProfileJson.Number(contrast, "yOffset"),
                        ProfileJson.Number(contrast, "width"),
                        ProfileJson.Number(contrast, "height"),
                        ProfileJson.Text(contrast, "color"));
                }

                XElement shape;
                if (lineKeys.Contains(key))
                {
                    var lineY = y + ProfileJson.Number(layout, "lineOffsetY");
                    shape = _writer.Path(
                        "legend-" + key,
                        [(startX, lineY), (startX + ProfileJson.Number(layout, "lineLength"), lineY)],
                        Color(SiteJson.Text((style["stroke"] as JsonObject)?["color"])),
                        1,
                        null,
                        false);
                }
                else
                {
                    shape = _writer.Shape(
                        "legend-" + key,
                        startX,
                        y,
                        ProfileJson.Number(layout, "swatchWidth"),
                        ProfileJson.Number(layout, "swatchHeight"),
                        Color(SiteJson.Text((style["fill"] as JsonObject)?["color"])),
                        Color(SiteJson.Text((style["stroke"] as JsonObject)?["color"])));
                }

                StyleObject(shape, style, null);
                _writer.Text(
                    "legend-label-" + key,
                    SiteJson.Text(_contents[_page.ToString()]?["legendLabels"]?[key] ?? names[key] ?? JsonValue.Create(key)),
                    labelX,
                    y + labelOffsetY,
                    labelWidth,
                    labelHeight,
                    labelPointSize,
                    "373737");
            }

            if (!Bool(_pageProfile["redline"]))
                return;

            var parcel = _pageProfile["parcelOutlineStyle"] as JsonObject
                         ?? throw new SiteContextException($"P{_page} 缺少 parcelOutlineStyle");
            var parcelY = startY + keys.Count * step + ProfileJson.Number(layout, "lineOffsetY");
            var parcelShape = _writer.Path(
                "legend-parcel",
                [(startX, parcelY), (startX + ProfileJson.Number(layout, "lineLength"), parcelY)],
                Color(SiteJson.Text((parcel["stroke"] as JsonObject)?["color"])),
                1,
                null,
                false);
            StyleObject(parcelShape, parcel, null);
            _writer.Text(
                "legend-parcel-label",
                ProfileJson.Text(layout, "parcelLabel"),
                labelX,
                startY + keys.Count * step + labelOffsetY,
                labelWidth,
                labelHeight,
                labelPointSize,
                "373737");
        }

        private void WriteNotes(int page, JsonObject content)
        {
            var part = $"ppt/notesSlides/notesSlide{page}.xml";
            if (!_package.Contains(part))
                return;

            var notes = _package.GetXml(part);
            var texts = notes.Root?.Descendants(PptxNames.A + "t").ToList() ?? [];
            foreach (var text in texts)
                text.Value = "";
            if (texts.Count == 0)
                return;


            var payload = new JsonObject
            {
                ["source"] = $"过程/地图/P{page}.json",
                ["page"] = page,
                ["radius_m"] = SiteJson.Number(_pageProfile["radius"]),
                ["crop"] = IntArray(_crop.Select(value => (int)Math.Round(value)).ToList()),
                ["capturedAt"] = SiteJson.Text(_data["capturedAt"]),
                ["limitations"] = ProfileJson.Text(content, "caption"),
                ["sources"] = content["sources"]?.DeepClone(),
                ["remaining_gaps"] = content["remaining_gaps"]?.DeepClone(),
                ["attribution"] = content["attribution"]?.DeepClone()
            };
            texts[0].Value = SiteJson.ToCompact(payload);
            _package.SetXml(part, notes);
        }

        private void StyleObject(XElement shape, JsonObject style, double? width)
        {
            var properties = shape.Element(PptxNames.P + "spPr")
                             ?? throw new SiteContextException("新形状缺少 p:spPr");
            var fill = style["fill"] as JsonObject;
            Alpha(
                properties.Element(PptxNames.A + "solidFill")?.Element(PptxNames.A + "srgbClr"),
                SiteJson.Number(fill?["opacity"], 1));

            var line = properties.Element(PptxNames.A + "ln")
                       ?? throw new SiteContextException("新形状缺少 a:ln");
            var stroke = style["stroke"] as JsonObject ?? new JsonObject();
            var opacity = SiteJson.Number(stroke["opacity"]);
            var strokeWidth = SiteJson.Number(stroke["width"]);
            if (opacity == 0 || strokeWidth == 0)
            {
                line.RemoveNodes();
                line.Add(new XElement(PptxNames.A + "noFill"));
                return;
            }

            Alpha(line.Element(PptxNames.A + "solidFill")?.Element(PptxNames.A + "srgbClr"), opacity);
            var scale = width ?? SiteJson.NumberOrNull(stroke["width"]) ?? 1;
            line.SetAttributeValue("w", (long)Math.Round(scale * _scaleX * EmuPerPx, MidpointRounding.ToEven));
            line.Add(new XElement(PptxNames.A + "prstDash", new XAttribute("val", Dash(stroke["dash"]))));
            line.Add(new XElement(PptxNames.A + "round"));
        }

        private static string Dash(JsonNode? dash)
        {
            if (dash is null)
                return "solid";
            if (dash is JsonValue value && value.TryGetValue<string>(out var text))
                return text switch { "dashed" => "dash", "dotted" => "dot", _ => "solid" };
            return "dash";
        }

        private static void Alpha(XElement? color, double opacity)
        {
            if (color is null)
                return;
            color.Add(new XElement(
                PptxNames.A + "alpha",
                new XAttribute("val", (long)Math.Round(opacity * 100000, MidpointRounding.ToEven))));
        }

        private string AddMedia(string stem, string extension, byte[] bytes)
        {
            _mediaCounter++;
            var part = _package.NewMediaPart($"{stem}-{_mediaCounter}", extension);
            _package.AddPart(part, bytes);
            _mediaExtensions.Add(extension);

            var baseId = $"rIdV23{_mediaCounter}";
            var relationshipId = baseId;
            var duplicate = 1;
            while (RelationshipExists(relationshipId))
                relationshipId = baseId + "-" + duplicate++;
            _relationships.Root?.Add(new XElement(
                PptxNames.Rel + "Relationship",
                new XAttribute("Id", relationshipId),
                new XAttribute("Type", PptxNames.RelationshipAttributeNamespace + "/image"),
                new XAttribute("Target", "../media/" + Path.GetFileName(part))));
            return relationshipId;
        }

        private bool RelationshipExists(string relationshipId) =>
            _relationships.Root?.Elements()
                .Any(element => element.Attribute("Id")?.Value == relationshipId) ?? false;

        private void EnsureContentTypes()
        {
            if (_mediaExtensions.Count == 0)
                return;
            const string partName = "[Content_Types].xml";
            if (!_package.Contains(partName))
                throw new SiteContextException("pptx 包内缺少 [Content_Types].xml");
            var contentTypes = _package.GetXml(partName);
            var root = contentTypes.Root
                       ?? throw new SiteContextException("[Content_Types].xml 缺少根节点");
            var existing = root.Elements()
                .Where(element => element.Name.LocalName == "Default")
                .Select(element => element.Attribute("Extension")?.Value ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in _mediaExtensions)
            {
                if (existing.Contains(extension))
                    continue;
                root.Add(new XElement(
                    PptxNames.ContentTypes + "Default",
                    new XAttribute("Extension", extension),
                    new XAttribute("ContentType", extension == "png" ? "image/png" : "image/jpeg")));
            }

            _package.SetXml(partName, contentTypes);
        }

        private List<string> BuildRemainingGaps(IReadOnlyList<int> pages)
        {
            var gaps = new List<string>();
            foreach (var page in pages)
            {
                if (_contents[page.ToString()] is not JsonObject content)
                    continue;
                foreach (var item in content["remaining_gaps"] as JsonArray ?? [])
                    gaps.Add($"P{page}: {SiteJson.Text(item)}");
            }

            return gaps;
        }

        private JsonArray PageReportArray()
        {
            var array = new JsonArray();
            foreach (var page in _pageReports)
            {
                var used = new JsonObject();
                foreach (var pair in page.HtmlNativeObjects)
                    used[pair.Key] = pair.Value;
                array.AddNode(new JsonObject
                {
                    ["page"] = page.Page,
                    ["radius_m"] = page.RadiusM,
                    ["crop"] = IntArray(page.Crop),
                    ["html_native_objects"] = used,
                    ["cad_only_parcel_outline"] = page.CadOnlyParcelOutline,
                    ["source_features"] = page.SourceFeatures,
                    ["editable_paths"] = page.EditablePaths,
                    ["native_texts"] = page.NativeTexts
                });
            }

            return array;
        }

        private (double X, double Y) Transform(Point point) =>
            Transform(point.X, point.Y);

        private (double X, double Y) Transform(double x, double y) =>
            (_figure.X + (x - _crop[0]) * _scaleX, _figure.Y + (y - _crop[1]) * _scaleY);

        private List<(double X, double Y)> Transform(IReadOnlyList<Point> points) =>
            points.Select(point => Transform(point)).ToList();

        private bool Inside(Point point) =>
            _crop[0] <= point.X && point.X <= _crop[2] && _crop[1] <= point.Y && point.Y <= _crop[3];

        private static List<Point> ReadPoints(JsonNode? node)
        {
            var values = (node as JsonArray ?? []).Select(item => SiteJson.Number(item)).ToList();
            var points = new List<Point>();
            for (var index = 0; index + 1 < values.Count; index += 2)
                points.Add(new Point(values[index], values[index + 1]));
            return points;
        }

        private static JsonNode? ResolvePath(JsonNode? node, string path)
        {
            var current = node;
            foreach (var segment in path.Split('.'))
            {
                current = current is JsonObject target ? target[segment] : null;
                if (current is null)
                    throw new SiteContextException($"采集数据缺少字段：{path}");
            }

            return current;
        }

        private static JsonArray IntArray(IEnumerable<int> values) =>
            new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

        private static string Color(string value) => value.TrimStart('#');

        private static bool Bool(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

        private static bool IsTruthy(JsonNode? node)
        {
            if (node is null)
                return false;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var flag))
                    return flag;
                if (value.TryGetValue<string>(out var text))
                    return text.Length > 0;
                if (value.TryGetValue<double>(out var number))
                    return number != 0;
            }

            return true;
        }

        private static IReadOnlyList<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            var current = new StringBuilder();
            foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (candidate.Length <= width)
                {
                    current.Clear();
                    current.Append(candidate);
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                var remaining = word;
                while (remaining.Length > width)
                {
                    lines.Add(remaining[..width]);
                    remaining = remaining[width..];
                }

                current.Append(remaining);
            }

            if (current.Length > 0)
                lines.Add(current.ToString());
            return lines;
        }

        private sealed class Feature
        {
            public string Source { get; init; } = "";
            public string FeatureId { get; init; } = "";
            public string Name { get; init; } = "";
            public string Layer { get; init; } = "";
            public string Kind { get; init; } = "";
            public JsonObject Style { get; init; } = new();
            public List<Point> Points { get; init; } = [];
            public string? FillColor { get; init; }
            public string StrokeColor { get; init; } = "";
        }
    }
}
