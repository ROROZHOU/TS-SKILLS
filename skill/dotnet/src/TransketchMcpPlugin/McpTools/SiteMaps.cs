using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

public sealed record MapRequest(string RunDirectory, int Width = 1280, int Height = 720, string? PhotosDirectory = null, string? ReuseRunDirectory = null);
public sealed record MapSession(string Directory, int[] Pages)
{
    public string[] Warnings { get; init; } = [];
}
public sealed class AmapCredentials(string key, string securityJsCode)
{
    public string Key { get; } = key;
    public string SecurityJsCode { get; } = securityJsCode;
}

public partial class PluginMcpTools
{
    public static string? InstalledSkillDirectory { private get; set; }

    // Set once at host startup. Read the current credential for each new job.
    public static Func<CancellationToken, ValueTask<AmapCredentials>>? ReadAmapCredentials { private get; set; }

    [McpServerTool(Name = PluginNames.SiteMap, UseStructuredContent = true)]
    [Description("采集本地项目的卫星底图和可编辑几何快照，返回输出目录及页码。")]
    public static async Task<MapSession> CaptureMap(
        [Description("项目运行目录绝对路径和截图视口宽高（CSS 像素）。")] MapRequest request,
        CancellationToken cancellationToken)
    {
        var read = ReadAmapCredentials ?? throw new McpException("请由 MCP 宿主配置高德凭据读取器。");
        if (request is null || string.IsNullOrWhiteSpace(request.RunDirectory) || !Path.IsPathFullyQualified(request.RunDirectory))
            throw new McpException("需要项目运行目录绝对路径。");
        AmapCredentials credentials;
        try { credentials = await read(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { throw new McpException("无法读取高德凭据，请用当前 Windows 账号重新配置。"); }
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Key) || string.IsNullOrWhiteSpace(credentials.SecurityJsCode))
            throw new McpException("请设置同一高德应用的 Web 端 Key 和安全密钥。");

        var input = new JsonObject {
            ["request"] = JsonSerializer.SerializeToNode(request, PluginJsonContext.Default.MapRequest),
            ["credentials"] = new JsonObject { ["key"] = credentials.Key, ["securityJsCode"] = credentials.SecurityJsCode }
        };
        var output = await RunWorker(input, cancellationToken);
        return JsonSerializer.Deserialize(output, PluginJsonContext.Default.MapSession) ?? throw new McpException("地图采集结果无效。");
    }

    [McpServerTool(Name = PluginNames.Redline, UseStructuredContent = true)]
    [Description("列出模型空间闭合红线候选；按实体 handle 提取边界，可按控制点配准到 GCJ02。")]
    public static async Task<RedlineResult> RegisterRedline(
        [Description("DWG 绝对路径")] string dwgPath,
        [Description("红线图层名，省略时列出所有支持的闭合实体")] string? layer = null,
        [Description("十六进制实体句柄；省略时只列候选")] string? handle = null,
        [Description("提取或配准结果 JSON 的绝对路径")] string? outputPath = null,
        [Description("配准控制文件绝对路径；省略时输出原始 CAD 边界")] string? registrationPath = null,
        CancellationToken cancellationToken = default)
    {
        var input = new JsonObject { ["action"]="redline", ["dwgPath"]=dwgPath, ["layer"]=layer, ["handle"]=handle,
                                     ["outputPath"]=outputPath, ["registrationPath"]=registrationPath };
        var result = await RunWorker(input, cancellationToken);
        return JsonSerializer.Deserialize(result, PluginJsonContext.Default.RedlineResult) ?? throw new McpException("红线结果无效");
    }

    private static async Task<string> RunWorker(JsonObject input, CancellationToken cancellationToken)
    {
        var root = InstalledSkillDirectory ?? Environment.GetEnvironmentVariable("TRANSKETCH_SITE_CONTEXT_SKILL");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new McpException("宿主需登记已安装的 site-context-ppt skill 根目录。");
        var executable = Path.Combine(root, "runtime", "SiteMapRenderer", "MapRenderer.exe");
        if (!File.Exists(executable)) throw new McpException("skill 内缺少 runtime/SiteMapRenderer/MapRenderer.exe，请安装完整技能包。");
        using var worker = new Process { StartInfo = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        }};
        worker.Start();
        try
        {
            var output = worker.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = worker.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await worker.StandardInput.WriteLineAsync(input.ToJsonString().AsMemory(), cancellationToken);
                worker.StandardInput.Close();
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // An apphost with a missing runtime may close stdin before reading the request.
                await worker.WaitForExitAsync(cancellationToken);
            }
            await worker.WaitForExitAsync(cancellationToken);
            var diagnostic = (await error).Trim();
            var response = await output;
            if (diagnostic.Contains("install or update .NET", StringComparison.OrdinalIgnoreCase) ||
                diagnostic.Contains("aka.ms/dotnet/app-launch-failed", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(response) && diagnostic.Length == 0)
                throw new McpException("渲染器未启动或未返回结果。请用 dotnet --list-runtimes 检查 x64 Microsoft.NETCore.App 8.0 或更高版本。");
            if (worker.ExitCode != 0)
                throw new McpException((diagnostic.StartsWith("地图采集：", StringComparison.Ordinal) || diagnostic.StartsWith("红线：", StringComparison.Ordinal)) ? diagnostic : "地图采集失败，请检查渲染器安装和 Edge。");
            return response;
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
        }
    }
}

public sealed record RedlineResult(string Status, string? OutputPath, System.Text.Json.JsonElement Details);
