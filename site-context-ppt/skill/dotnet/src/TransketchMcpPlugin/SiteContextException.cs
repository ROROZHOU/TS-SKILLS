namespace TransketchMcpPlugin;

/// <summary>
/// 周边场地分析链路失败；消息直接返回给模型，需包含阶段、文件和期望/实际
/// </summary>
/// <date>2026年11月16日</date>
public sealed class SiteContextException(string message) : McpException(message);
