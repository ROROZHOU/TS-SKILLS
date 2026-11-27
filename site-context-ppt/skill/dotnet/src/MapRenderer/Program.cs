using System.Text.Json;
using System.Text.Json.Nodes;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
try
{
    var input = JsonNode.Parse(await Console.In.ReadLineAsync() ?? "")!.AsObject();
    if ((string?)input["action"] == "drawing") { Console.WriteLine(await ImageReview.Trace(input)); return 0; }
    if ((string?)input["action"] == "redline")
    {
        try { Console.WriteLine(Redline.Process(input).ToJsonString()); return 0; }
        catch (Exception e) { Console.Error.WriteLine("红线：" + (e is InvalidDataException ? e.Message : "无法解析文件或控制点，请检查输入。")); return 1; }
    }
    var request = input["request"].Deserialize<MapRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("缺少采集请求");
    var result = await Renderer.CaptureSiteMaps(request, input["credentials"]!.AsObject(), CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}
catch (Exception error)
{
    // Do not forward browser diagnostics or request URLs containing credentials.
    Console.Error.WriteLine("地图采集：" + (error is InvalidDataException ? error.Message : "请检查运行目录、Edge、网络及高德凭据。"));
    return 1;
}

