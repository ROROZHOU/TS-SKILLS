namespace TransketchMcpPlugin;

/// <summary>
/// 地块资源导入结果
/// </summary>
public sealed class McpResourceImport
{
    /// <summary>
    /// 输出坐标参考系，固定为 GCJ02
    /// </summary>
    [Description("输出坐标参考系，固定为 GCJ02")]
    public string Crs { get; set; } = "";

    /// <summary>
    /// 输入坐标参考系
    /// </summary>
    [Description("输入坐标参考系：WGS84 或 GCJ02")]
    public string InputCrs { get; set; } = "";

    /// <summary>
    /// 导出的要素个数
    /// </summary>
    [Description("导出的要素个数")]
    public int FeatureCount { get; set; }

    /// <summary>
    /// 多边形孔洞总数
    /// </summary>
    [Description("多边形孔洞总数")]
    public int Holes { get; set; }

    /// <summary>
    /// 出现的类别键，按首次出现顺序
    /// </summary>
    [Description("出现的类别键，按首次出现顺序")]
    public List<string> Classes { get; set; } = [];

    /// <summary>
    /// GeoJSON 输入文件的实际 sha256
    /// </summary>
    [Description("GeoJSON 输入文件的实际 sha256")]
    public string GeoJsonSha256 { get; set; } = "";

    /// <summary>
    /// 输出 JSON 的绝对路径
    /// </summary>
    [Description("输出 JSON 的绝对路径")]
    public string OutputPath { get; set; } = "";
}
