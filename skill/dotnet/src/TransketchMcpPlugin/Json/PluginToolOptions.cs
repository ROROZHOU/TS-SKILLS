namespace TransketchMcpPlugin;

/// <summary>
/// 插件工具使用的 JSON 选项；与主程序 cadJsonOptions 相互独立。
/// </summary>
public static class PluginToolOptions
{
    private static readonly JsonSerializerOptions Instance = new(PluginJsonContext.Default.Options);

    /// <summary>
    /// 供主程序 WithTools 注册插件工具时使用的 JSON 选项。
    /// </summary>
    public static JsonSerializerOptions Options => Instance;
}
