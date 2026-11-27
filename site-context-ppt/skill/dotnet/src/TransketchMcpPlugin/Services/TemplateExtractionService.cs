using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

/// <summary>
/// 模板提取编排：哈希比对、复用判断、对象树与样式落盘
/// </summary>
/// <date>2026年11月16日</date>
internal static class TemplateExtractionService
{
    /// <summary>
    /// 由本模块生成、可在重提取时覆盖的文件
    /// </summary>
    private static readonly string[] ManagedArtifacts =
        ["manifest.json", "inventory.json", "resolved-styles.json", "template.pptx", "cad-layout-draft.json"];

    /// <summary>
    /// 执行模板提取；哈希不一致且未要求覆盖时只回报状态，不写文件
    /// </summary>
    /// <param name="templatePath">参考模板路径</param>
    /// <param name="outputDir">提取成果目录</param>
    /// <param name="force">模板哈希与已有成果不一致时是否覆盖重提取</param>
    public static McpTemplateExtraction Extract(string templatePath, string outputDir, bool force)
    {
        templatePath = Path.GetFullPath(templatePath);
        outputDir = Path.GetFullPath(outputDir);
        if (!File.Exists(templatePath))
            throw new SiteContextException($"找不到参考模板：{templatePath}");

        var actualHash = SiteJson.HashFile(templatePath);
        var manifestPath = Path.Combine(outputDir, "manifest.json");
        var inventoryPath = Path.Combine(outputDir, "inventory.json");
        var stylesPath = Path.Combine(outputDir, "resolved-styles.json");
        var layoutPath=Path.Combine(outputDir,"cad-layout-draft.json");
        void WriteLayout(PptxPackage deck)
        {
            SiteJson.SaveFile(layoutPath,CadTemplateLayout.Extract(deck,actualHash));
            ArtifactReceipt.Save(layoutPath,actualHash+"-cad-v2");
        }
        var manifestHash = ReadManifestHash(manifestPath);
        var matches = manifestHash is not null && string.Equals(manifestHash, actualHash, StringComparison.OrdinalIgnoreCase);

        if (!force && matches && ArtifactReceipt.Matches(inventoryPath,actualHash) && ArtifactReceipt.Matches(stylesPath,actualHash))
        {
            if(!ArtifactReceipt.Matches(layoutPath,actualHash+"-cad-v2")) WriteLayout(PptxPackage.Load(templatePath));
            return BuildResult(outputDir, actualHash, manifestHash, true, true, []);
        }

        if (!force && manifestHash is not null)
        {
            var stale = ManagedArtifacts
                .Select(name => Path.Combine(outputDir, name))
                .Where(File.Exists)
                .ToList();
            if (Directory.Exists(Path.Combine(outputDir, "ooxml")))
                stale.Add(Path.Combine(outputDir, "ooxml"));

            var result = BuildResult(outputDir, actualHash, manifestHash, false, false, stale);
            result.Warnings.Add(
                $"提取结果校验未通过：manifest 记录 {manifestHash}，模板实际 {actualHash}。" +
                "提取成果是模板的派生物，需要重提取；确认覆盖后以 force=true 重新调用。");
            return result;
        }

        if (!force && Directory.Exists(outputDir) && ManagedArtifacts.Any(name => File.Exists(Path.Combine(outputDir, name))))
            throw new SiteContextException(
                $"提取目录已存在且有成果：{outputDir}。请换一个新目录，或以 force=true 覆盖已有成果。");

        if (force)
            RemoveManagedArtifacts(outputDir);

        Directory.CreateDirectory(outputDir);
        File.Copy(templatePath, Path.Combine(outputDir, "template.pptx"), true);

        var package = PptxPackage.Load(templatePath);
        WriteLayout(package);
        var inventory = TemplateService.BuildInventory(package, actualHash);
        SiteJson.SaveFile(inventoryPath, inventory);
        TemplateService.WriteOoxml(package, outputDir);

        var styles = StyleExtractor.Extract(package, actualHash);
        SiteJson.SaveFile(stylesPath, styles);

        ArtifactReceipt.Save(inventoryPath,actualHash);
        ArtifactReceipt.Save(stylesPath,actualHash);
        SiteJson.SaveFile(manifestPath, new JsonObject
        {
            ["template_sha256"] = actualHash,
            ["slide_count"] = (inventory["slides"] as JsonArray)?.Count ?? 0,
            ["semantic_review_required"] = true,
            ["inventory"] = "inventory.json",
            ["original"] = "template.pptx"
        });

        return BuildResult(outputDir, actualHash, actualHash, true, false, []);
    }

    private static string? ReadManifestHash(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return null;
        return SiteJson.LoadObject(manifestPath)["template_sha256"]?.GetValue<string>();
    }

    private static void RemoveManagedArtifacts(string outputDir)
    {
        foreach (var name in ManagedArtifacts)
        {
            var path = Path.Combine(outputDir, name);
            if (File.Exists(path))
                File.Delete(path);
        }

        var ooxml = Path.Combine(outputDir, "ooxml");
        if (Directory.Exists(ooxml))
            Directory.Delete(ooxml, true);
    }

    private static McpTemplateExtraction BuildResult(
        string outputDir,
        string actualHash,
        string? manifestHash,
        bool matches,
        bool reused,
        List<string> staleArtifacts)
    {
        var inventoryPath = Path.Combine(outputDir, "inventory.json");
        var stylesPath = Path.Combine(outputDir, "resolved-styles.json");
        var result = new McpTemplateExtraction
        {
            TemplateSha256 = actualHash,
            ManifestSha256 = manifestHash,
            Matches = matches,
            Reused = reused,
            OutputDir = outputDir,
            InventoryPath = inventoryPath,
            StylesPath = stylesPath,
            CadLayoutDraftPath = matches ? Path.Combine(outputDir,"cad-layout-draft.json") : null,
            StaleArtifacts = staleArtifacts
        };

        if (!File.Exists(inventoryPath))
            return result;

        var inventory = SiteJson.LoadObject(inventoryPath);
        var styles = File.Exists(stylesPath) ? SiteJson.LoadObject(stylesPath) : new JsonObject();
        result.SlideSizeEmu = TemplateService.ReadSlideSize(inventory);
        var objectCounts = TemplateService.ReadObjectCounts(inventory);
        var styleCounts = StyleExtractor.ReadCounts(styles);

        if (inventory["slides"] is JsonArray slides)
        {
            foreach (var slide in slides.OfType<JsonObject>())
            {
                var page = slide["page"]?.GetValue<int>() ?? 0;
                styleCounts.TryGetValue(page, out var counts);
                result.Pages.Add(new McpSlidePart
                {
                    Page = page,
                    Part = slide["part"]?.GetValue<string>() ?? "",
                    ObjectCount = objectCounts.GetValueOrDefault(page),
                    RunCount = counts.Runs,
                    UnresolvedCount = counts.Unresolved
                });
            }
        }

        result.SlideCount = result.Pages.Count;
        return result;
    }
}
