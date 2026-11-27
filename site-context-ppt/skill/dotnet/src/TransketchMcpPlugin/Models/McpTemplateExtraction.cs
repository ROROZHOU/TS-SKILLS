namespace TransketchMcpPlugin;

/// <summary>
/// 一页幻灯片在对象树中的统计
/// </summary>
public sealed class McpSlidePart
{
    /// <summary>
    /// 页号，从 1 开始
    /// </summary>
    [Description("页号，从 1 开始")]
    public int Page { get; set; }

    /// <summary>
    /// 幻灯片在 pptx 包内的 part 路径
    /// </summary>
    [Description("幻灯片在 pptx 包内的 part 路径")]
    public string Part { get; set; } = "";

    /// <summary>
    /// 对象树中的对象个数
    /// </summary>
    [Description("对象树中的对象个数")]
    public int ObjectCount { get; set; }

    /// <summary>
    /// 文字 run 个数
    /// </summary>
    [Description("文字 run 个数")]
    public int RunCount { get; set; }

    /// <summary>
    /// 颜色或样式无法从解析证据确定的个数，需要回看原始 XML
    /// </summary>
    [Description("颜色或样式无法从解析证据确定的个数，需要回看原始 XML")]
    public int UnresolvedCount { get; set; }
}

/// <summary>
/// 模板提取结果：实际哈希、复用判断、页号与 part 映射、产物路径
/// </summary>
public sealed class McpTemplateExtraction
{
    /// <summary>
    /// 参考模板的实际 sha256
    /// </summary>
    [Description("参考模板的实际 sha256")]
    public string TemplateSha256 { get; set; } = "";

    /// <summary>
    /// 提取成果 manifest.json 中记录的 sha256；没有旧成果时为空
    /// </summary>
    [Description("提取成果 manifest.json 中记录的 sha256；没有旧成果时为空")]
    public string? ManifestSha256 { get; set; }

    /// <summary>
    /// 实际哈希与旧成果是否一致，false 表示旧成果已过期
    /// </summary>
    [Description("实际哈希与旧成果是否一致，false 表示旧成果已过期，需要重提取")]
    public bool Matches { get; set; }

    /// <summary>
    /// 是否直接复用了已有提取成果
    /// </summary>
    [Description("是否直接复用了已有提取成果")]
    public bool Reused { get; set; }

    /// <summary>
    /// 幻灯片页数
    /// </summary>
    [Description("幻灯片页数")]
    public int SlideCount { get; set; }

    /// <summary>
    /// 幻灯片尺寸属性，键为 sldSz 属性名，值为 EMU
    /// </summary>
    [Description("幻灯片尺寸属性，键为 sldSz 属性名，值为 EMU")]
    public Dictionary<string, long> SlideSizeEmu { get; set; } = [];

    /// <summary>
    /// 逐页 part 与对象统计
    /// </summary>
    [Description("逐页 part 与对象统计")]
    public List<McpSlidePart> Pages { get; set; } = [];

    /// <summary>
    /// 提取目录绝对路径；复用或新提取都返回
    /// </summary>
    [Description("提取目录绝对路径")]
    public string OutputDir { get; set; } = "";

    /// <summary>
    /// 对象树清单 inventory.json 的绝对路径
    /// </summary>
    [Description("对象树清单 inventory.json 的绝对路径；可直接读取，不要重复放入工具参数")]
    public string InventoryPath { get; set; } = "";

    /// <summary>
    /// 样式解析结果 resolved-styles.json 的绝对路径
    /// </summary>
    [Description("样式解析结果 resolved-styles.json 的绝对路径")]
    public string StylesPath { get; set; } = "";

    [Description("上传模板的逐页红线面、指标文字及图框候选，需结合原页审核")]
    public string? CadLayoutDraftPath { get; set; }

    /// <summary>
    /// 已过期的成果文件列表
    /// </summary>
    [Description("哈希不一致时仍有旧哈希的成果文件列表；为空表示没有过期文件")]
    public List<string> StaleArtifacts { get; set; } = [];

    /// <summary>
    /// 需要模型注意的提示
    /// </summary>
    [Description("需要模型注意的提示信息")]
    public List<string> Warnings { get; set; } = [];
}
