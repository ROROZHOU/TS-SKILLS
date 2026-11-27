using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// 本模块的 JSON 文件读写；未知 schema 的大文件统一走 JsonNode，避免 AOT 反射
/// </summary>
/// <date>2026年11月16日</date>
internal static class SiteJson
{
    /// <summary>
    /// 读取 JSON 文件
    /// </summary>
    /// <param name="path">文件路径</param>
    public static JsonNode LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new SiteContextException($"找不到 JSON 文件：{path}");
        var text = File.ReadAllText(path, Encoding.UTF8);
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
               ?? throw new SiteContextException($"JSON 文件内容为空：{path}");
    }

    /// <summary>
    /// 读取 JSON 文件并断言是对象
    /// </summary>
    /// <param name="path">文件路径</param>
    public static JsonObject LoadObject(string path) =>
        LoadFile(path) as JsonObject
        ?? throw new SiteContextException($"JSON 顶层必须是对象：{path}");

    /// <summary>
    /// 把 JSON 节点序列化为缩进文本；中文不转义，数字保留原文
    /// </summary>
    /// <param name="node">JSON 节点</param>
    public static string ToIndented(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 把 JSON 节点序列化为单行文本；中文不转义
    /// </summary>
    /// <param name="node">JSON 节点</param>
    public static string ToCompact(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// 写出缩进 JSON，目录不存在时创建
    /// </summary>
    /// <param name="path">目标路径</param>
    /// <param name="node">JSON 节点</param>
    public static void SaveFile(string path, JsonNode node)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, ToIndented(node), new UTF8Encoding(false));
    }

    /// <summary>
    /// 计算文件 sha256 十六进制串
    /// </summary>
    /// <param name="path">文件路径</param>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 字符串集合转 JSON 数组，null 保留为 null
    /// </summary>
    /// <param name="values">字符串集合</param>
    public static JsonArray StringArray(IEnumerable<string?> values) =>
        new(values.Select(value => value is null ? null : JsonValue.Create(value)).ToArray());

    /// <summary>
    /// 属性集合转 JSON 对象，保留顺序
    /// </summary>
    /// <param name="attributes">XML 属性集合</param>
    public static JsonObject AttributeObject(IEnumerable<XAttribute> attributes)
    {
        var result = new JsonObject();
        foreach (var attribute in attributes)
            result[attribute.Name.LocalName] = attribute.Value;
        return result;
    }

    /// <summary>
    /// 字典转 JSON 对象，保留顺序
    /// </summary>
    /// <param name="pairs">键值对集合</param>
    public static JsonObject ObjectOf(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var result = new JsonObject();
        foreach (var pair in pairs)
            result[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>
    /// 把节点当作字符串读取；数字按原文返回，null 返回空串
    /// </summary>
    /// <param name="node">JSON 节点</param>
    public static string Text(JsonNode? node)
    {
        if (node is null)
            return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return node.ToJsonString();
    }

    /// <summary>
    /// 把节点当作数字读取，兼容字符串形式的数字
    /// </summary>
    /// <param name="node">JSON 节点</param>
    /// <param name="fallback">无法解析时的返回值</param>
    public static double Number(JsonNode? node, double fallback = 0)
    {
        if (node is null)
            return fallback;
        if (node is JsonValue value && value.TryGetValue<double>(out var number))
            return number;
        return double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    /// <summary>
    /// 把节点当作数字读取；null 或无法解析时返回 null
    /// </summary>
    /// <param name="node">JSON 节点</param>
    public static double? NumberOrNull(JsonNode? node)
    {
        if (node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<double>(out var number))
            return number;
        return double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 读取对象字段，非对象时返回 null
    /// </summary>
    /// <param name="node">JSON 节点</param>
    /// <param name="property">字段名</param>
    public static JsonNode? Field(JsonNode? node, string property) =>
        node is JsonObject target ? target[property] : null;

    /// <summary>
    /// 深拷贝一个节点，避免同一节点挂到两个父节点上
    /// </summary>
    /// <param name="node">JSON 节点</param>
    public static JsonNode? Copy(JsonNode? node) => node?.DeepClone();
}

/// <summary>
/// JsonArray 追加节点；直接调用 Add(JsonNode) 的重载选择会落到泛型版本，AOT 下不可用
/// </summary>
internal static class JsonArrayExtensions
{
    /// <summary>
    /// 追加一个 JSON 节点
    /// </summary>
    /// <param name="array">目标数组</param>
    /// <param name="node">待追加节点</param>
    public static void AddNode(this JsonArray array, JsonNode? node) => array.Add(node);
}
