using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

/// <summary>
/// site-context-profile.json 的读取入口；组版与验收的全部项目相关数值都从这里取，代码里不再写死
/// </summary>
/// <date>2026年11月16日</date>
internal sealed class SiteContextProfile
{
    private readonly JsonObject _root;

    private SiteContextProfile(JsonObject root, string path)
    {
        _root = root;
        Path = path;
    }

    /// <summary>
    /// profile 文件绝对路径
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// compose 段
    /// </summary>
    public JsonObject Compose => Section("compose");

    /// <summary>
    /// acceptance 段
    /// </summary>
    public JsonObject Acceptance => Section("acceptance");

    /// <summary>
    /// layout 段
    /// </summary>
    public JsonObject Layout => Section("layout");

    /// <summary>
    /// template 段
    /// </summary>
    public JsonObject Template => Section("template");

    /// <summary>
    /// canvas 段
    /// </summary>
    public JsonObject Canvas => Section("canvas");

    /// <summary>
    /// 模板 pptx 文件名，位于运行目录的 过程/输入 下
    /// </summary>
    public string TemplateFileName => ProfileJson.Text(Template, "pptxFileName");

    /// <summary>
    /// 期望总页数
    /// </summary>
    public int ExpectedSlideCount => ProfileJson.Integer(Template, "expectedSlideCount");

    /// <summary>
    /// CAD 基准 pptx 文件名，位于运行目录的 过程/输入 下
    /// </summary>
    public string CadBaselineFileName => ProfileJson.Text(ProfileJson.Object(Template, "cadBaseline"), "pptxFileName");

    /// <summary>
    /// 从 CAD 基准复制到输出的页号
    /// </summary>
    public IReadOnlyList<int> CadBaselinePages =>
        ProfileJson.IntList(ProfileJson.Object(Template, "cadBaseline"), "pages");

    /// <summary>
    /// 本次重排的页号
    /// </summary>
    public IReadOnlyList<int> ComposePages => ProfileJson.IntList(Compose, "pages");

    /// <summary>
    /// 输出 pptx 文件名
    /// </summary>
    public string OutputFileName => ProfileJson.Text(Compose, "outputFileName");

    /// <summary>
    /// 采集视口尺寸
    /// </summary>
    public (int Width, int Height) Viewport
    {
        get
        {
            var viewport = ProfileJson.Object(Canvas, "viewport");
            return (ProfileJson.Integer(viewport, "width"), ProfileJson.Integer(viewport, "height"));
        }
    }

    /// <summary>
    /// 分析图框，像素
    /// </summary>
    public (double X, double Y, double Width, double Height) FigureRect
    {
        get
        {
            var rect = ProfileJson.Object(Canvas, "figureRect");
            return (
                ProfileJson.Number(rect, "x"),
                ProfileJson.Number(rect, "y"),
                ProfileJson.Number(rect, "width"),
                ProfileJson.Number(rect, "height"));
        }
    }

    /// <summary>
    /// 底图裁切长宽比容差
    /// </summary>
    public double CropAspectTolerance => ProfileJson.Number(Canvas, "cropAspectTolerance");

    /// <summary>
    /// 某一页的 compose 专属参数；未声明时返回空对象
    /// </summary>
    /// <param name="page">页号</param>
    public JsonObject PerPage(int page) =>
        ProfileJson.OptionalObject(Compose, "perPage")?[page.ToString()] as JsonObject ?? new JsonObject();

    /// <summary>
    /// 读取 profile 文件
    /// </summary>
    /// <param name="path">profile 路径</param>
    public static SiteContextProfile Load(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var profile=new SiteContextProfile(SiteJson.LoadObject(fullPath),fullPath);
        if(profile.Template["cadLayoutFile"] is JsonValue file)
        {
            var name=file.GetValue<string>();
            if(System.IO.Path.GetFileName(name)!=name)throw new SiteContextException("cadLayoutFile 应为与 profile 同目录的文件名");
            var reviewed=SiteJson.LoadObject(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fullPath)!,name));
            if((bool?)reviewed["reviewed"]!=true || reviewed["cadPageLayout"] is not JsonObject { Count:>0 } layouts ||
                (string?)reviewed["template_sha256"] is not { Length:64 })
                throw new SiteContextException("请先审核红线页绑定和模板哈希");
            foreach(var (key,node) in layouts)
                if(!int.TryParse(key,out var n)||n<=0||node?["frame"] is not JsonArray { Count:4 } frame ||
                    !frame.All(v=> (double?)v is double d && double.IsFinite(d)) || (double)frame[2]!<=0 || (double)frame[3]!<=0 ||
                    node["keepIds"] is not JsonArray || node["parcelStyles"] is not JsonObject { Count:>0 })
                    throw new SiteContextException("红线页绑定缺少有效的页码、图框、保留对象或地块面："+key);
            profile.Template["redlinePages"]=new JsonArray(layouts.Select(k=>(JsonNode?)JsonValue.Create(int.Parse(k.Key,System.Globalization.CultureInfo.InvariantCulture))).ToArray());
            profile.Template["cadPageLayout"]=layouts.DeepClone();
            profile.Template["cadLayoutHash"]=reviewed["template_sha256"]!.DeepClone();
        }
        return profile;
    }

    private JsonObject Section(string name) =>
        _root[name] as JsonObject
        ?? throw new SiteContextException($"site-context-profile 缺少 {name} 段：{Path}");
}

/// <summary>
/// profile 节点读取；缺失即报错，避免悄悄回落到代码里的旧默认值
/// </summary>
/// <date>2026年11月16日</date>
internal static class ProfileJson
{
    /// <summary>
    /// 读取必填对象
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static JsonObject Object(JsonObject parent, string name) =>
        parent[name] as JsonObject
        ?? throw new SiteContextException($"site-context-profile 缺少对象字段 {name}");

    /// <summary>
    /// 读取可选对象
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static JsonObject? OptionalObject(JsonObject parent, string name) => parent[name] as JsonObject;

    /// <summary>
    /// 读取可选数组
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static JsonArray? OptionalArray(JsonObject parent, string name) => parent[name] as JsonArray;

    /// <summary>
    /// 读取必填字符串
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static string Text(JsonObject parent, string name)
    {
        var value = SiteJson.Text(parent[name]);
        return value.Length > 0
            ? value
            : throw new SiteContextException($"site-context-profile 缺少字符串字段 {name}");
    }

    /// <summary>
    /// 读取可选字符串，缺失时返回空串
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static string OptionalText(JsonObject parent, string name) => SiteJson.Text(parent[name]);

    /// <summary>
    /// 读取必填数字
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static double Number(JsonObject parent, string name) =>
        SiteJson.NumberOrNull(parent[name])
        ?? throw new SiteContextException($"site-context-profile 缺少数字字段 {name}");

    /// <summary>
    /// 读取可选数字
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    /// <param name="fallback">缺失时的返回值</param>
    public static double OptionalNumber(JsonObject parent, string name, double fallback) =>
        SiteJson.NumberOrNull(parent[name]) ?? fallback;

    /// <summary>
    /// 读取必填整数
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static int Integer(JsonObject parent, string name) => (int)Math.Round(Number(parent, name));

    /// <summary>
    /// 读取可选整数，缺失时返回 null
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static int? OptionalInteger(JsonObject parent, string name)
    {
        var value = SiteJson.NumberOrNull(parent[name]);
        return value is null ? null : (int)Math.Round(value.Value);
    }

    /// <summary>
    /// 读取必填整数数组
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static List<int> IntList(JsonObject parent, string name) =>
        (parent[name] as JsonArray
         ?? throw new SiteContextException($"site-context-profile 缺少数组字段 {name}"))
        .Select(item => (int)Math.Round(SiteJson.Number(item)))
        .ToList();

    /// <summary>
    /// 读取必填数字数组
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static List<double> NumberList(JsonObject parent, string name) =>
        (parent[name] as JsonArray
         ?? throw new SiteContextException($"site-context-profile 缺少数组字段 {name}"))
        .Select(item => SiteJson.Number(item))
        .ToList();

    /// <summary>
    /// 读取必填字符串数组
    /// </summary>
    /// <param name="parent">父对象</param>
    /// <param name="name">字段名</param>
    public static List<string> TextList(JsonObject parent, string name) =>
        (parent[name] as JsonArray
         ?? throw new SiteContextException($"site-context-profile 缺少数组字段 {name}"))
        .Select(item => SiteJson.Text(item))
        .ToList();
}
