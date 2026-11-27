using System.Text.Json.Nodes;
namespace TransketchMcpPlugin;

/// <summary>
/// 周边场地分析（site-context-ppt skill）配套的 MCP 工具入口
/// </summary>
/// <date>2026年11月16日</date>

public partial class PluginMcpTools
{
    [McpServerTool(Name = PluginNames.PrepareTemplate, UseStructuredContent = true)]
    [Description("提取 PPTX 对象及样式；已有审核绑定时，同次调用编译逐页配置。")]
    public static PreparedTemplate PrepareTemplate(
        [Description("参考 PPTX 绝对路径")] string templatePath,
        [Description("提取目录绝对路径")] string outputDir,
        [Description("已审核样式绑定路径；省略时只提取供审核")] string? bindingsPath = null,
        [Description("项目种子配置路径，与绑定一并提供")] string? configPath = null,
        [Description("编译后的项目配置路径")] string? outputPath = null,
        [Description("覆盖过期的提取成果")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((bindingsPath is null) != (configPath is null) || (bindingsPath is null) != (outputPath is null))
            throw new McpException("编译时请同时提供 bindingsPath、configPath、outputPath。");
        using var extractionLock=ArtifactReceipt.Lock(outputDir);
        var extraction = ExtractTemplate(templatePath, outputDir, force);
        if (extraction.StaleArtifacts.Count > 0 || bindingsPath is null)
            return new(extraction, null);
        cancellationToken.ThrowIfCancellationRequested();
        using var configLock=ArtifactReceipt.Lock(outputPath!);
        var signature=new System.Text.Json.Nodes.JsonArray("config-v3",SiteJson.HashFile(extraction.StylesPath),
            SiteJson.HashFile(bindingsPath),SiteJson.HashFile(configPath!)).ToJsonString();
        var report=outputPath!+".compilation";
        if (ArtifactReceipt.Matches(outputPath!,signature) && ArtifactReceipt.Matches(report,signature))
            return new(extraction,JsonSerializer.Deserialize(File.ReadAllText(report),PluginJsonContext.Default.McpStyleCompilation));
        var compiled=CompilePageConfig(extraction.StylesPath,bindingsPath,configPath!,outputPath!);
        File.WriteAllText(report,JsonSerializer.Serialize(compiled,PluginJsonContext.Default.McpStyleCompilation));
        ArtifactReceipt.Save(report,signature);
        ArtifactReceipt.Save(outputPath!,signature);
        return new(extraction,compiled);
    }

    /// <summary>
    /// 检查参考模板并提取对象树、样式与逐页 part
    /// </summary>
    /// <date>2026年11月16日</date>
    /// <param name="templatePath">参考模板 pptx 的绝对路径</param>
    /// <param name="outputDir">提取成果目录；哈希一致时直接复用，避免重复提取</param>
    /// <param name="force">模板哈希与已有成果不一致时是否覆盖重提取</param>
    /// <returns>实际哈希、复用判断、逐页 part 与产物路径</returns>
    public static McpTemplateExtraction ExtractTemplate(
        [Description("参考模板 pptx 的绝对路径")]
        string templatePath,
        [Description("提取成果目录的绝对路径；建议放在模板同级的 default-template-extraction")]
        string outputDir,
        [Description("模板哈希与已有成果不一致时是否覆盖重提取；默认 false 只回报状态")]
        bool force = false)
    {
        return TemplateExtractionService.Extract(templatePath, outputDir, force);
    }

    /// <summary>
    /// 把已审核的样式绑定编译进 site.config
    /// </summary>
    /// <date>2026年11月16日</date>
    /// <param name="stylesPath">resolved-styles.json 的绝对路径</param>
    /// <param name="bindingsPath">已审核的 style-bindings.json 绝对路径</param>
    /// <param name="configPath">原始 site.config.json 绝对路径</param>
    /// <param name="outputPath">编译结果输出路径</param>
    /// <returns>编译条数与逐条报告</returns>
    public static McpStyleCompilation CompilePageConfig(
        [Description("resolved-styles.json 的绝对路径")]
        string stylesPath,
        [Description("已审核的 style-bindings.json 绝对路径")]
        string bindingsPath,
        [Description("原始 site.config.json 绝对路径，只读，不会被修改")]
        string configPath,
        [Description("编译结果 site.config.json 的输出绝对路径")]
        string outputPath)
    {
        var styles = SiteJson.LoadObject(Path.GetFullPath(stylesPath));
        var bindings = SiteJson.LoadObject(Path.GetFullPath(bindingsPath));
        var config = SiteJson.LoadObject(Path.GetFullPath(configPath));
        var (output, report) = PageConfigCompiler.Compile(styles, bindings, config);
        outputPath = Path.GetFullPath(outputPath);
        SiteJson.SaveFile(outputPath, output);

        var result = new McpStyleCompilation
        {
            CompiledCount = report.Count,
            TemplateSha256 = SiteJson.Text(styles["template_sha256"]),
            OutputPath = outputPath,
            Report = report
        };
        var unresolved = report.Count(item => item.Unresolved.Count > 0);
        if (unresolved > 0)
            result.Warnings.Add($"{unresolved} 条绑定存在未解析项，组版前需要补齐证据或更换引用对象。");
        return result;
    }

    /// <summary>
    /// 把已核实、已配准的 GeoJSON 转为网页资源要素
    /// </summary>
    /// <date>2026年11月16日</date>
    /// <param name="geojsonPath">GeoJSON 文件绝对路径</param>
    /// <param name="evidencePath">来源证据 JSON 绝对路径，必须 status=verified 且有 registration</param>
    /// <param name="crs">输入坐标系，仅接受 WGS84 或 GCJ02</param>
    /// <param name="outputPath">输出 JSON 绝对路径</param>
    /// <param name="classField">类别字段名，默认 class</param>
    /// <returns>要素数、孔洞数、类别列表与输入哈希</returns>
    [McpServerTool(Name = PluginNames.ImportResource, UseStructuredContent = true)]
    [Description("处理已获取的专题数据，生成与模板无关的点、线、面数据集。支持 GeoJSON、KML、CSV 及经引导取点的 PNG/JPEG/PDF。")]
    public static McpResourceImport PrepareTopic(
        [Description("项目运行目录绝对路径")] string runDirectory,
        [Description("已获取的专题源文件绝对路径")] string sourcePath,
        [Description("来源与配准证据 JSON 绝对路径")] string evidencePath,
        [Description("WGS84、GCJ02、图件配准到地图 DRAWING 或米制 CAD 图纸 DRAWING_CAD；KML 使用 WGS84")] string crs,
        [Description("项目内唯一数据集名称，用作输出文件名")] string dataset,
        [Description("原数据中的类别字段")] string classField = "class",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!Path.IsPathFullyQualified(runDirectory) || !Path.IsPathFullyQualified(sourcePath) ||
            !Path.IsPathFullyQualified(evidencePath) || !System.Text.RegularExpressions.Regex.IsMatch(dataset, @"\A[\p{L}\p{N}_-]{1,80}\z"))
            throw new SiteContextException("请提供绝对路径和数据集名（字母、数字、下划线或短横线）。");
        var work=Path.Combine(runDirectory,"过程");
        if(Path.GetExtension(sourcePath).Equals(".kml",StringComparison.OrdinalIgnoreCase) && crs!="WGS84")
            throw new SiteContextException("KML 的坐标系应为 WGS84。");

        var evidence=SiteJson.LoadObject(evidencePath);
        evidence["file_sha256"]=SiteJson.HashFile(sourcePath);
        evidence["resource_id"]=dataset;
        evidence.Remove("classMap");
        var cad = crs=="DRAWING_CAD";
        var outputPath=Path.Combine(work,cad ? "CAD专题" : "专题",dataset+".json");
        using var topicLock=ArtifactReceipt.Lock(outputPath);
        var signature=new System.Text.Json.Nodes.JsonArray("topic-v3",crs,classField,evidence.DeepClone()).ToJsonString();
        McpResourceImport Result(System.Text.Json.Nodes.JsonObject body) => new()
        {
            Crs=cad ? "CAD" : "GCJ02", InputCrs=crs, FeatureCount=(int)body["statistics"]!["features"]!,
            Holes=(int)body["statistics"]!["holes"]!, Classes=body["classes"]!.AsArray().Select(x=>(string)x!).ToList(),
            GeoJsonSha256=SiteJson.Text(evidence["file_sha256"]), OutputPath=outputPath
        };
        if (ArtifactReceipt.Matches(outputPath,signature)) return Result(SiteJson.LoadObject(outputPath));
        var image = Path.GetExtension(sourcePath).ToLowerInvariant() is ".pdf" or ".png" or ".jpg" or ".jpeg";
        JsonObject document;
        if (image)
        {
            if (crs is not ("DRAWING" or "DRAWING_CAD") || evidence["tracing"] is not JsonObject tracing || classField!="class")
                throw new SiteContextException("图件需要 tracing、默认类别字段 class 及图件配准坐标系。");
            var request = new JsonObject { ["action"]="drawing", ["sourcePath"]=sourcePath, ["tracing"]=tracing.DeepClone() };
            document = JsonNode.Parse(RunWorker(request,cancellationToken).GetAwaiter().GetResult())!.AsObject();
        }
        else document=TopicSource.Read(sourcePath,classField);
        if(cad)
        {
            if ((string?)evidence["status"]!="verified" || string.IsNullOrWhiteSpace((string?)evidence["title"]) ||
                (string?)evidence["registration"]?["cadSha256"] is not {Length:64})
                throw new SiteContextException("CAD 图件需记录 verified、图名和米制目标 DWG 的 cadSha256。");
            DrawingRegistration.ToGcj02(document,evidence,true);
            var features=document["features"]!.AsArray();
            if(features.Count==0) throw new SiteContextException("图件没有可用要素。");
            document["classes"]=SiteJson.StringArray(features.Select(n=>SiteJson.Text(n?["properties"]?[classField])).Distinct());
            document["statistics"]=new JsonObject{["features"]=features.Count,["holes"]=0};
            document["evidence"]=evidence.DeepClone();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var tmp=outputPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {SiteJson.SaveFile(tmp,document);cancellationToken.ThrowIfCancellationRequested();File.Move(tmp,outputPath,true);}
            finally {File.Delete(tmp);}
            ArtifactReceipt.Save(outputPath,signature);
            return Result(document);
        }
        if (crs=="DRAWING") DrawingRegistration.ToGcj02(document,evidence);
        var (output, featureCount, holes, classes) =
            ResourceImporter.Convert(document, evidence, crs=="DRAWING" ? "GCJ02" : crs, classField);

        if(featureCount==0) throw new SiteContextException("未提取到可用专题要素。");
        output["classes"]=SiteJson.StringArray(classes);
        output["source_crs"]=crs;
        var pending=outputPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { SiteJson.SaveFile(pending,output); cancellationToken.ThrowIfCancellationRequested(); File.Move(pending,outputPath,true); }
        finally { File.Delete(pending); }
        ArtifactReceipt.Save(outputPath,signature);
        return Result(output);
    }

    /// <summary>
    /// 组版成 pptx 或验收已有成果
    /// </summary>
    /// <date>2026年11月16日</date>
    /// <param name="action">reuse、compose 或 validate</param>
    /// <param name="runDir">运行目录绝对路径</param>
    /// <param name="profilePath">site-context-profile.json 绝对路径，页面布局、图例与验收阈值全部来自这里</param>
    /// <param name="baselinePptxPath">修订基准 pptx；为空时基于过程/输入里的模板并套用 CAD 基准页</param>
    /// <param name="pages">只重排部分页，逗号分隔；非空时必须同时给 baselinePptxPath</param>
    /// <returns>compose 返回组版回执，validate 返回验收回执</returns>
    [McpServerTool(Name = PluginNames.Compose, UseStructuredContent = true),
     Description("action=reuse 查询已验收成果，返回 reuse 或 miss。action=compose 把运行目录里的采集数据、截图与页面内容写成原生可编辑 pptx，" +
                 "卫星页使用采集几何和已配准专题，红线页使用 CAD 图形。action=validate 核对保留页依赖、采集来源、" +
                 "页数与原生对象符合 profile 约定，并把结果写到 过程/验收结果.json。" +
                 "compose 需要 过程/输入 里的模板、项目 CAD 几何 cad-geometry.json 或含线稿的 redline-outline.json（旧 profile 可用 CAD 页基准） 与 过程/页面内容.json；" +
                 "图片按 srcRect 在 PPT 内裁切，不做重编码。大工程建议调用超时设置为 180 秒")]
    public static McpComposeResult Compose(
        [Description("动作：reuse 查询已验收成果，compose 组版，validate 验收")]
        string action,
        [Description("运行目录绝对路径")]
        string runDir,
        [Description("site-context-profile.json 绝对路径")]
        string profilePath,
        [Description("修订基准 pptx 绝对路径；为空时基于模板并套用 profile 里的 CAD 基准页")]
        string? baselinePptxPath = null,
        [Description("逗号分隔的页号，只重排这些页；非空时必须同时给 baselinePptxPath")]
        string? pages = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DeliveryReuse.Execute(action,runDir,profilePath,baselinePptxPath,pages,cancellationToken,() => action switch
        {
            "compose" => ComposeService.Compose(runDir, profilePath, baselinePptxPath, pages, cancellationToken),
            "validate" => ValidationService.Validate(runDir, profilePath, baselinePptxPath, pages, cancellationToken),
            _ => throw new SiteContextException($"未知的 action：{action}；只接受 reuse、compose、validate")
        });
    }
}

public sealed record PreparedTemplate(McpTemplateExtraction Extraction, McpStyleCompilation? Compilation);
