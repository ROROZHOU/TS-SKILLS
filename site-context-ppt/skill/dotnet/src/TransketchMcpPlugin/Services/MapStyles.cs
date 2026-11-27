using System.Text.Json.Nodes;
namespace TransketchMcpPlugin;

internal static class MapStyles
{
    internal static JsonObject Config(string work)
    {
        var current=Path.Combine(work,"输入","项目配置.json");
        return SiteJson.LoadObject(File.Exists(current) ? current : Path.Combine(work,"地图","site.config.json"));
    }

    internal static void Apply(JsonObject config,int page,JsonObject data)
    {
        var profile=config["ppt"]?["pages"]?[page.ToString()] as JsonObject ?? throw new SiteContextException($"缺少 P{page} 页面配置");
        if ((int?)data["schema"]==2)
        {
            if (!JsonNode.DeepEquals(data["pageProfile"],profile) || !JsonNode.DeepEquals(data["config"]?["project"],config["project"]))
                throw new SiteContextException("旧快照与当前项目配置不一致，请重新采集。");
            return;
        }
        if ((int?)data["schema"]!=3 || !JsonNode.DeepEquals(data["captureSpec"],CaptureSpec.ForPage(config,page)))
            throw new SiteContextException($"P{page} 地图采集条件已改变，请重新采集。");
        data["pageProfile"]=profile.DeepClone();
        var features=data["features"]!.AsArray();
        var labelPoints=new Dictionary<string,List<(double X,double Y)>>();
        foreach(var f in features.OfType<JsonObject>().ToArray())
        {
            var layer=SiteJson.Text(f["layer"]);
            var source=SiteJson.Text(f["source"]);
            if (source is "HTML/PXc.expLabels" or "HTML/PX.roadNames" && profile["crop"] is JsonArray crop)
            {
                if (!labelPoints.TryGetValue(source,out var points)) labelPoints[source]=points=[];
                double x=(double)f["xy"]![0]!, y=(double)f["xy"]![1]!;
                if (points.Count>=5 || x<(double)crop[0]! || x>(double)crop[2]! || y<(double)crop[1]! || y>(double)crop[3]! ||
                    points.Any(p=>Math.Pow(x-p.X,2)+Math.Pow(y-p.Y,2)<110*110)) { features.Remove(f); continue; }
                points.Add((x,y));
            }
            var basis=(f["style_key"] is JsonValue key ? profile["layerStyles"]?[SiteJson.Text(key)] :
                profile["layers"]?[layer] ?? config["layers"]?[layer]) as JsonObject ?? throw new SiteContextException($"P{page} 缺少图层样式 {layer}");
            var style=(JsonObject)(basis["variants"]?[SiteJson.Text(f["level"])] ?? basis).DeepClone();
            var way=basis["way"];
            if ((source=="HTML/GEO.waterways" || source=="HTML/L.waterNames" && (string?)f["kindOfWater"]=="way") &&
                ((bool?)way?["on"]==false || source=="HTML/L.waterNames" && (bool?)way?["label"]?["show"]==false))
            { features.Remove(f); continue; }
            if (source=="HTML/GEO.waterways")
            {
                var metres=SiteJson.Number(f["measuredWidthM"],SiteJson.Number(way?["widthM"]?[SiteJson.Text(f["class"])],SiteJson.Number(f["defaultWidthM"],1)));
                var w=Math.Max(1.1,metres/SiteJson.Number(data["mpp"]));
                style["stroke"]=new JsonObject { ["color"]=(way?["color"] ?? basis["fill"]?["color"])?.DeepClone(),
                    ["opacity"]=(way?["opacity"] ?? basis["fill"]?["opacity"])?.DeepClone(), ["width"]=w, ["dash"]="solid" };
                f["width"]=w;
            }
            if (source=="HTML/GEO.roadL" && (string?)basis["widthMode"]!="template")
            {
                int level=Math.Clamp((int?)f["level"] ?? 0,0,2);
                f["width"]=Math.Max(SiteJson.Number((basis["width"] as JsonArray)?.ElementAtOrDefault(level),2),
                    new[]{22,13,7.5}[level]/SiteJson.Number(data["mpp"])*0.4);
            }
            if (source is "HTML/L.waterNames" or "HTML/PXc.expLabels" or "HTML/PX.roadNames" or "HTML/config verified label")
            {
                style["size"]=1; style["fill"]=new JsonObject { ["opacity"]=0 };
                style["stroke"]=new JsonObject { ["opacity"]=0, ["width"]=0 };
                if ((string?)f["kindOfWater"]=="way" && way?["label"] is JsonObject labels && style["label"] is JsonObject dest)
                    foreach(var (name,value) in labels) dest[name]=value?.DeepClone();
            }
            f["style"]=style;
        }
    }
}
