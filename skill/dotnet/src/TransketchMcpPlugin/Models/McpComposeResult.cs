using System.ComponentModel;

namespace TransketchMcpPlugin;

/// <summary>
/// 组版与验收的统一回执；action 决定哪个字段有值
/// </summary>
/// <date>2026年11月16日</date>
public sealed class McpComposeResult
{
    /// <summary>
    /// 本次执行的 action，reuse、miss、compose 或 validate
    /// </summary>
    [Description("返回状态。reuse 查询返回 reuse 或 miss，组版和验收分别返回 compose 和 validate")]
    public string Action { get; set; } = "";

    /// <summary>
    /// action=compose 时的组版回执
    /// </summary>
    [Description("compose 或 reuse 命中时返回组版回执")]
    public McpComposeReport? Compose { get; set; }

    /// <summary>
    /// action=validate 时的验收回执
    /// </summary>
    [Description("validate 或 reuse 命中时返回验收回执")]
    public McpRunValidation? Validation { get; set; }
}

/// <summary>
/// 一次组版的结果，对应 组版检查.json
/// </summary>
/// <date>2026年11月16日</date>
public sealed class McpComposeReport
{
    /// <summary>
    /// 生成 pptx 的绝对路径
    /// </summary>
    [Description("生成 pptx 的绝对路径")]
    public string PptxPath { get; set; } = "";

    /// <summary>
    /// pptx 的 sha256
    /// </summary>
    [Description("生成 pptx 的 sha256")]
    public string PptxSha256 { get; set; } = "";

    /// <summary>
    /// 本次重排的页号
    /// </summary>
    [Description("本次重排的页号")]
    public List<int> SelectedPages { get; set; } = [];

    /// <summary>
    /// 封面页 XML 是否与模板逐字节一致
    /// </summary>
    [Description("封面页 XML 是否与模板逐字节一致；应当为 true")]
    public bool CoverXmlIdentical { get; set; }

    /// <summary>
    /// 逐页统计
    /// </summary>
    [Description("逐页统计")]
    public List<McpComposePageReport> Pages { get; set; } = [];

    /// <summary>
    /// 页面内容里声明的剩余缺口
    /// </summary>
    [Description("页面内容里声明的剩余缺口，形如 P6: ...")]
    public List<string> RemainingGaps { get; set; } = [];

    /// <summary>
    /// 非阻断告警，例如截图字节数偏小
    /// </summary>
    [Description("非阻断告警，例如截图字节数偏小")]
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// 图层对象来源.json 的绝对路径
    /// </summary>
    [Description("图层对象来源.json 的绝对路径")]
    public string LineagePath { get; set; } = "";

    /// <summary>
    /// 组版检查.json 的绝对路径
    /// </summary>
    [Description("组版检查.json 的绝对路径")]
    public string ReportPath { get; set; } = "";
}

/// <summary>
/// 单页组版统计
/// </summary>
/// <date>2026年11月16日</date>
public sealed class McpComposePageReport
{
    /// <summary>
    /// 页号
    /// </summary>
    [Description("页号")]
    public int Page { get; set; }

    /// <summary>
    /// 高德视野半径，米
    /// </summary>
    [Description("高德视野半径，米")]
    public int RadiusM { get; set; }

    /// <summary>
    /// 底图裁切框，1280x720 像素坐标，顺序为左、上、右、下
    /// </summary>
    [Description("底图裁切框，1280x720 像素坐标，顺序为左、上、右、下")]
    public List<int> Crop { get; set; } = [];

    /// <summary>
    /// 按图层统计的网页原生对象数
    /// </summary>
    [Description("按图层统计的网页原生对象数")]
    public Dictionary<string, int> HtmlNativeObjects { get; set; } = [];

    /// <summary>
    /// 是否包含 CAD 红线叠图
    /// </summary>
    [Description("是否包含 CAD 用地红线叠图；除红线外不允许其它 CAD 对象")]
    public bool CadOnlyParcelOutline { get; set; }

    /// <summary>
    /// 采集数据里的要素数
    /// </summary>
    [Description("采集数据里的要素数")]
    public int SourceFeatures { get; set; }

    /// <summary>
    /// 页面上 a:custGeom 对象数，即可编辑路径数
    /// </summary>
    [Description("页面上 a:custGeom 对象数，即可编辑路径数")]
    public int EditablePaths { get; set; }

    /// <summary>
    /// 页面上 p:txBody 对象数，即原生文本框数
    /// </summary>
    [Description("页面上 p:txBody 对象数，即原生文本框数")]
    public int NativeTexts { get; set; }
}

/// <summary>
/// 验收结果，对应 验收结果.json
/// </summary>
/// <date>2026年11月16日</date>
public sealed class McpRunValidation
{
    /// <summary>
    /// 生成与溯源检查是否全通过
    /// </summary>
    [Description("生成与溯源检查是否全通过；false 时逐条读取 errors")]
    public bool GenerationAndProvenancePass { get; set; }

    /// <summary>
    /// 失败条目，原文沿用原脚本的断言文案
    /// </summary>
    [Description("失败条目，原文沿用原脚本的断言文案")]
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// 参与比对的封面依赖 part 数
    /// </summary>
    [Description("参与比对的封面依赖 part 数")]
    public int CoverDependenciesChecked { get; set; }

    /// <summary>
    /// 逐页统计
    /// </summary>
    [Description("逐页统计")]
    public List<McpValidationPage> Pages { get; set; } = [];

    /// <summary>
    /// 数据完整性是否通过；资料缺失时恒为 false
    /// </summary>
    [Description("数据完整性是否通过；资料缺失时恒为 false")]
    public bool DataCompletenessPass { get; set; }

    /// <summary>
    /// 数据完整性缺口
    /// </summary>
    [Description("数据完整性缺口，来自 profile 的 acceptance.dataCompleteness.missing")]
    public List<string> Missing { get; set; } = [];

    /// <summary>
    /// 被验收 pptx 的 sha256
    /// </summary>
    [Description("被验收 pptx 的 sha256")]
    public string PptxSha256 { get; set; } = "";

    /// <summary>
    /// 验收结果.json 的绝对路径
    /// </summary>
    [Description("验收结果.json 的绝对路径")]
    public string ReportPath { get; set; } = "";
}

/// <summary>
/// 单页验收统计
/// </summary>
/// <date>2026年11月16日</date>
public sealed class McpValidationPage
{
    /// <summary>
    /// 页号
    /// </summary>
    [Description("页号")]
    public int Page { get; set; }

    /// <summary>
    /// 采集数据里的要素数
    /// </summary>
    [Description("采集数据里的要素数")]
    public int Features { get; set; }

    /// <summary>
    /// 页面上 a:custGeom 对象数
    /// </summary>
    [Description("页面上 a:custGeom 对象数")]
    public int NativePaths { get; set; }
}
