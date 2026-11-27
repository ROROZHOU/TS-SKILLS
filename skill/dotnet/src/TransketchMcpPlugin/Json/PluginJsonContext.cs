namespace TransketchMcpPlugin;

/// <summary>
/// 插件独享的 JSON 源生成上下文，避免改动主程序的 McpJsonContext。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PreparedTemplate))]
[JsonSerializable(typeof(RedlineResult))]
[JsonSerializable(typeof(MapRequest))]
[JsonSerializable(typeof(MapSession))]
[JsonSerializable(typeof(McpTemplateExtraction))]
[JsonSerializable(typeof(McpSlidePart))]
[JsonSerializable(typeof(McpStyleCompilation))]
[JsonSerializable(typeof(McpBindingReport))]
[JsonSerializable(typeof(McpResourceImport))]
[JsonSerializable(typeof(McpComposeResult))]
[JsonSerializable(typeof(McpComposeReport))]
[JsonSerializable(typeof(McpComposePageReport))]
[JsonSerializable(typeof(McpRunValidation))]
[JsonSerializable(typeof(McpValidationPage))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
public partial class PluginJsonContext : JsonSerializerContext
{
}

