using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace TransketchMcpPlugin;

internal static class ThematicOverlay
{
    // Files are selected by page so several independently sourced datasets can share a view.
    internal static void Append(string work, int page, JsonObject snapshot)
    {
        var config = SiteJson.LoadObject(Path.Combine(work,"输入","项目配置.json"));
        var profile = config["ppt"]?["pages"]?[page.ToString()];
        var styles = profile?["layerStyles"] as JsonObject ?? new JsonObject();
        var dir = Path.Combine(work,"专题");
        var selections = profile?["topics"] as JsonObject;
        var legacy = Path.Combine(dir,$"P{page}");
        var files = selections != null ? selections.Select(entry => {
            if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Key, @"\A[\p{L}\p{N}_-]{1,80}\z"))
                throw new SiteContextException("无效专题名称。");
            return Path.Combine(dir,entry.Key+".json");
        }).ToArray() : Directory.Exists(legacy) ? Directory.GetFiles(legacy,"*.json").Order(StringComparer.Ordinal).ToArray() : [];
        if (files.Length == 0) return;
        var features = snapshot["features"]!.AsArray();
        var project = Projection(snapshot);
        foreach (var path in files)
        {
            var package = SiteJson.LoadObject(path);
            if ((selections is null && ((int?)package["page"] != page || !JsonNode.DeepEquals(package["template_sha256"],config["ppt"]?["template_sha256"]))) || (string?)package["crs"] != "GCJ02" || package["resourceFeatures"] is not JsonArray items)
                throw new SiteContextException($"专题文件页码、坐标系或要素列表不符：{path}");
            var digest = SiteJson.HashFile(path);
            var used = new HashSet<string>(features.Select(f=>SiteJson.Text(f?["id"])));
            foreach (var item in items.OfType<JsonObject>())
            {
                var category = SiteJson.Text(item["class"]);
                var key = selections == null ? SiteJson.Text(item["style_key"]) :
                    (string?)selections[Path.GetFileNameWithoutExtension(path)]?[category] ?? category;
                var kind = SiteJson.Text(item["kind"]);
                if (styles[key] is not JsonObject style || (string?)style["style"] != kind ||
                    !JsonNode.DeepEquals(style["_template"]?["sha256"],config["ppt"]?["template_sha256"]) ||
                    style["_template"]?["style_refs"] is null)
                    throw new SiteContextException($"P{page} 专题 {key} 的几何类型 {kind} 或模板来源不匹配。");
                if ((string?)item["evidence"]?["status"] != "verified" || item["evidence"]?["registration"] is null)
                    throw new SiteContextException($"专题 {key} 缺少核实或配准依据。");
                var id = "topic-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    Path.GetFileName(path) + "/" + SiteJson.Text(item["id"]))))[..24];
                if (!used.Add(id)) throw new SiteContextException("专题要素 ID 重复：" + id);
                var f = new JsonObject { ["id"]=id, ["source"]="专题/registered", ["layer"]="resource", ["kind"]=kind,
                    ["style_key"]=key, ["name"]=item["name"]?.DeepClone(), ["style"]=style.DeepClone(),
                    ["evidence"]=item["evidence"]?.DeepClone(), ["file_sha256"]=digest, ["file"]=Path.GetFileName(path),
                    ["source_feature_id"]=item["id"]?.DeepClone() };
                JsonArray Line(JsonArray coords)
                {
                    var xy = new JsonArray();
                    foreach (var pair in coords) { var p = project(pair!); xy.AddNode(p.Item1); xy.AddNode(p.Item2); }
                    return xy;
                }
                if (kind=="polygon")
                {
                    var rings=new JsonArray();
                    foreach(var ring in item["rings"]!.AsArray()) rings.AddNode(Line(ring!.AsArray()));
                    if(rings.Count==0) throw new SiteContextException("专题面无外环。");
                    f["xy"]=rings[0]!.DeepClone(); f["rings"]=rings;
                }
                else if(kind=="line") f["xy"]=Line(item["coordinates"]!.AsArray());
                else if(kind=="point") { var p=project(item["coordinates"]!); f["xy"]=new JsonArray(p.Item1,p.Item2); }
                else throw new SiteContextException("专题要素类型不支持：" + kind);
                if(item["label_anchor"] is JsonArray anchor) { var p=project(anchor); f["labelAt"]=new JsonArray(p.Item1,p.Item2); }
                features.AddNode(f);
            }
        }
    }

    private static Func<JsonNode,(double,double)> Projection(JsonObject snapshot)
    {
        if ((string?)snapshot["crs"]!="GCJ02" || snapshot["cornerGCJ"] is not JsonArray {Count:4} corners)
            throw new SiteContextException("底图缺少 GCJ02 四角坐标，请重新采集。");
        static (double x,double y) Merc(JsonNode n)
        {
            var lng=(double)n[0]!;var lat=(double)n[1]!;
            if(Math.Abs(lng)>180 || Math.Abs(lat)>85.05113) throw new SiteContextException("专题经纬度超界。");
            return (lng,Math.Asinh(Math.Tan(lat*Math.PI/180))*180/Math.PI);
        }
        var o=Merc(corners[0]!); var r=Merc(corners[1]!); var b=Merc(corners[2]!);
        var dx=r.x-o.x; var dy=r.y-o.y; var ex=b.x-o.x; var ey=b.y-o.y;
        var det=dx*ey-dy*ex;
        if(Math.Abs(det)<1e-14)throw new SiteContextException("底图视口退化。");
        var width=(double)snapshot["viewport"]!["width"]!; var height=(double)snapshot["viewport"]!["height"]!;
        (double,double) Screen(JsonNode n) {var p=Merc(n); var x=p.x-o.x;var y=p.y-o.y;
            return ((x*ey-y*ex)/det*width,(dx*y-dy*x)/det*height);}
        var br=Screen(corners[3]!);
        if(Math.Abs(br.Item1-width)>1 || Math.Abs(br.Item2-height)>1)throw new SiteContextException("底图透视误差超过 1 像素，请使用平面视图。");
        return Screen;
    }
}
