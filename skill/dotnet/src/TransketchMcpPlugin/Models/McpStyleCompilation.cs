namespace TransketchMcpPlugin;

/// <summary>
/// 单条样式绑定的编译结果，unresolved 非空表示该项仍缺继承证据
/// </summary>
public sealed class McpBindingReport
{
    /// <summary>
    /// 页号
    /// </summary>
    [Description("页号")]
    public int Page { get; set; }

    /// <summary>
    /// 绑定目标：layer、parcel 或 resource_style
    /// </summary>
    [Description("绑定目标：layer、parcel 或 resource_style")]
    public string Target { get; set; } = "";

    /// <summary>
    /// 图层键；parcel 目标时为空
    /// </summary>
    [Description("图层键；parcel 目标时为空")]
    public string? Layer { get; set; }

    /// <summary>
    /// 仍需回看原始 XML 才能确定的项，例如 fill inheritance
    /// </summary>
    [Description("仍需回看原始 XML 才能确定的项，例如 fill inheritance；空表示全部取到显式值")]
    public List<string> Unresolved { get; set; } = [];
}

/// <summary>
/// 样式编译结果
/// </summary>
public sealed class McpStyleCompilation
{
    /// <summary>
    /// 编译的绑定条数
    /// </summary>
    [Description("编译的绑定条数")]
    public int CompiledCount { get; set; }

    /// <summary>
    /// 编译所用的样式文件哈希
    /// </summary>
    [Description("编译所用的样式文件哈希")]
    public string TemplateSha256 { get; set; } = "";

    /// <summary>
    /// 编译后的 site.config.json 绝对路径
    /// </summary>
    [Description("编译后的 site.config.json 绝对路径")]
    public string OutputPath { get; set; } = "";

    /// <summary>
    /// 逐条绑定报告
    /// </summary>
    [Description("逐条绑定报告")]
    public List<McpBindingReport> Report { get; set; } = [];

    /// <summary>
    /// 从原始配置带来的关键提示，例如覆盖来源说明
    /// </summary>
    [Description("需要模型注意的提示信息")]
    public List<string> Warnings { get; set; } = [];
}
