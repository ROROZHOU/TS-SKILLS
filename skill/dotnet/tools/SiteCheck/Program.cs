using System.Text;
using System.Text.Json;
using TransketchMcpPlugin;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length < 2 || !Path.IsPathFullyQualified(args[0]))
{
    Console.Error.WriteLine("SiteCheck <解压后的技能绝对路径> <命令> [参数]，见 references/release-check.md");
    return 2;
}
PluginMcpTools.InstalledSkillDirectory = args[0];
try
{
    object result;
    switch (args[1], args.Length)
    {
        case ("template", 4):
            result = PluginMcpTools.PrepareTemplate(args[2], args[3]);
            break;
        case ("template", 7):
            result = PluginMcpTools.PrepareTemplate(args[2], args[3], args[4], args[5], args[6]);
            break;
        case ("redline", 3):
            result = await PluginMcpTools.RegisterRedline(args[2]);
            break;
        case ("redline", 5 or 6):
            result = await PluginMcpTools.RegisterRedline(args[2], handle: args[3], outputPath: args[4],
                registrationPath: args.ElementAtOrDefault(5));
            break;
        case ("topic", 7 or 8):
            result = PluginMcpTools.PrepareTopic(args[2], args[3], args[4], args[5], args[6], args.ElementAtOrDefault(7) ?? "class");
            break;
        case ("capture", 3):
            PluginMcpTools.ReadAmapCredentials = async token =>
            {
                using var json = JsonDocument.Parse(await Console.In.ReadLineAsync(token) ?? "{}");
                var fields = json.RootElement;
                return new AmapCredentials(fields.GetProperty("key").GetString()!, fields.GetProperty("securityJsCode").GetString()!);
            };
            result = await PluginMcpTools.CaptureMap(new MapRequest(args[2]), CancellationToken.None);
            break;
        case ("compose" or "validate" or "reuse", 4):
            result = PluginMcpTools.Compose(args[1], args[2], args[3]);
            break;
        default:
            Console.Error.WriteLine("命令或参数数量有误，见 references/release-check.md");
            return 2;
    }
    Console.WriteLine(JsonSerializer.Serialize(result, result.GetType()));
    if (result is McpComposeResult { Validation: { } validation } &&
        (!validation.GenerationAndProvenancePass || !validation.DataCompletenessPass)) return 1;
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
