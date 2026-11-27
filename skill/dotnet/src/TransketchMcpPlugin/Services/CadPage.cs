using System.Text.Json.Nodes;
using System.Xml.Linq;
using Point = TransketchMcpPlugin.ComposeGeometry.Point;

namespace TransketchMcpPlugin;

internal static class CadPage
{
    public static int Draw(PptxSlideWriter writer, JsonObject layout, JsonObject cad, JsonObject? content, string work, JsonNode? projectParcels, CancellationToken cancellationToken = default)
    {
        var p = PptxNames.P;
        var a = PptxNames.A;
        var keep = layout["keepIds"]!.AsArray().Select(n => (int)n!).ToHashSet();
        int? Id(XElement shape) => (int?)shape.Elements().Elements(p + "cNvPr").FirstOrDefault()?.Attribute("id");
        var symbols=writer.Tree.Descendants().Where(e=>Id(e).HasValue && e.Element(p+"spPr")!=null).ToDictionary(e=>Id(e)!.Value);
        var overlays=new List<(JsonObject feature,XElement symbol,string dataset)>();
        foreach(var entry in content?["cadTopics"] as JsonArray ?? [])
        {
            var name=(string)entry!;
            if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"\A[\p{L}\p{N}_-]{1,80}\z")) throw new SiteContextException("CAD 专题名无效");
            var data=SiteJson.LoadObject(Path.Combine(work,"CAD专题",name+".json"));
            if(!string.Equals((string?)data["evidence"]?["registration"]?["cadSha256"],(string?)cad["metadata"]?["sha256"],StringComparison.OrdinalIgnoreCase))
                throw new SiteContextException("专题配准的 DWG 与本项目不同");
            foreach(var feature in data["features"]!.AsArray().OfType<JsonObject>())
            {
                var cls=(string)feature["properties"]!["class"]!;
                var binding=layout["topicStyles"]?[cls] ?? throw new SiteContextException("CAD 页缺少专题类别绑定："+cls);
                overlays.Add((feature,symbols[(int)binding["styleId"]!],name));
                foreach(var id in binding["legendIds"] as JsonArray ?? []) keep.Add((int)id!);
            }
        }


        var frame = layout["frame"]!.AsArray().Select(n => (double)n!).ToArray();
        var boundary = Vertices(cad["cadVertices"]!.AsArray(), true);
        string primary=((string)cad["metadata"]!["handle"]!).ToUpperInvariant();
        var groups=ParcelIndicators.Read(content?["indicators"],primary,(string?)content?["parcelName"]);
        var selection=CadParcelSelection.Resolve(projectParcels,content,layout,primary,groups);
        var boundIds=selection.Values.SelectMany(v=>new[]{(int?)v["parcelStyleId"],(int?)v["indicatorTextStyleId"]}).Where(id=>id!=null).ToHashSet();
        foreach(var kept in writer.Tree.Elements().Where(e=>Id(e) is int id && keep.Contains(id)))
            if(kept.DescendantsAndSelf().Any(e=>Id(e) is int id && boundIds.Contains(id)))
                throw new SiteContextException("保留对象中含有待替换的旧地块或指标文字，请调整 keepIds");
        writer.Tree.Elements().Where(e => Id(e) is int id && !keep.Contains(id)).Remove();
        var parcels=new Dictionary<string,List<Point>>(StringComparer.OrdinalIgnoreCase);
        foreach(var handle in selection.Keys)
        {
            if(handle==primary){parcels.Add(handle,boundary);continue;}
            var entity=cad["cadLinework"]!.AsArray().SingleOrDefault(n=>string.Equals((string?)n?["handle"],handle,StringComparison.OrdinalIgnoreCase));
            if(entity?["vertices"] is not JsonArray {Count:>=3} vertices || (bool?)entity["closed"]!=true)
                throw new SiteContextException("未找到指定地块的闭合 CAD 边界："+handle);
            parcels.Add(handle,Vertices(vertices,true));
        }
        var all=parcels.Values.SelectMany(r=>r).ToArray();
        var minX = all.Min(v => v.X);
        var minY = all.Min(v => v.Y);
        var spanX = all.Max(v => v.X) - minX;
        var spanY = all.Max(v => v.Y) - minY;
        if (spanX <= 0 || spanY <= 0) throw new SiteContextException("CAD 红线面域无有效面积");
        var scale = 0.80 * Math.Min(frame[2] / spanX, frame[3] / spanY);
        Point Place(Point v) => new(frame[0] + frame[2]/2 + (v.X-minX-spanX/2)*scale,
                                   frame[1] + frame[3]/2 - (v.Y-minY-spanY/2)*scale);

        int count = 0;
        foreach (var line in cad["cadLinework"]!.AsArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(parcels.ContainsKey((string)line!["handle"]!))continue;
            var path = Vertices(line!["vertices"]!.AsArray(), (bool)line["closed"]!).Select(Place).ToArray();
            foreach (var chunk in ComposeGeometry.ClipLine(path, frame[0], frame[1], frame[0]+frame[2], frame[1]+frame[3]))
            {
                if (chunk.Count < 2 || chunk.All(v => v == chunk[0])) continue;
                writer.Path($"CAD:{line["layer"]}:{line["handle"]}", chunk.Select(v => (v.X,v.Y)).ToArray(), "B1B1B1", 0.65, null, false);
                count++;
            }
        }
        bool IsStyle(XElement e) => e.Name != a+"xfrm" && e.Name != a+"custGeom" && e.Name != a+"prstGeom";
        foreach(var (handle,outline) in parcels)
        {
            if(selection[handle]["parcelStyleId"] is not JsonValue faceId || !symbols.TryGetValue((int)faceId,out var prototype) || prototype.Name!=p+"sp")
                throw new SiteContextException("本页模板没有地块面样式对象："+handle);
            var style=prototype.Element(p+"spPr")!;
            var ring = outline.SkipLast(1).Select(Place).Select(v => (v.X,v.Y)).ToArray();
            var parcel = writer.Path($"CAD:用地红线:{handle}", ring, "000000", 1, "FFFFFF", true);
            var props = parcel.Element(p + "spPr")!;
            props.Elements().Where(IsStyle).Remove();
            props.Add(style.Elements().Where(IsStyle).Select(e => new XElement(e)));
            if (prototype.Element(p+"style") is XElement styleRef) props.AddAfterSelf(new XElement(styleRef));
        }
        foreach(var (feature,symbol,dataset) in overlays)
        {
            var geometry=feature["geometry"]!;
            var coords=geometry["coordinates"]!.AsArray();
            var title="图件:"+dataset+":"+feature["properties"]!["class"];
            Point From(JsonNode? v)=>Place(new Point((double)v![0]!, (double)v[1]!));
            var extent=(left:frame[0],top:frame[1],right:frame[0]+frame[2],bottom:frame[1]+frame[3]);
            List<XElement> shapes=[];
            switch((string?)geometry["type"])
            {
                case "LineString":
                    foreach(var chunk in ComposeGeometry.ClipLine(coords.Select(From).ToArray(),extent.left,extent.top,extent.right,extent.bottom))
                        shapes.Add(writer.Path(title,chunk.Select(q=>(q.X,q.Y)).ToList(),"000000",1,null,false));
                    break;
                case "Polygon":
                    var rings=coords.Select(r=>ComposeGeometry.ClipPolygon(r!.AsArray().Select(From).ToArray(),extent.left,extent.top,extent.right,extent.bottom)
                        .Select(q=>(q.X,q.Y)).ToList()).ToList();
                    if(rings[0].Count>=3) shapes.Add(writer.Path(title,rings[0],"000000",1,"FFFFFF",true,rings.Skip(1).Where(r=>r.Count>=3).ToList()));
                    break;
                case "Point":
                    var at=From(coords);
                    if(at.X>=extent.left && at.X<=extent.right && at.Y>=extent.top && at.Y<=extent.bottom)
                        shapes.Add(writer.Shape(title,at.X-4,at.Y-4,8,8,"FFFFFF",null,"ellipse"));
                    break;
                default:throw new SiteContextException("不支持的 CAD 专题几何类型");
            }
            if(shapes.Count==0)throw new SiteContextException("专题要素落在 CAD 图框以外："+title);
            foreach(var shape in shapes)
            {
                var dest=shape.Element(p+"spPr")!;
                dest.Elements().Where(IsStyle).Remove();
                dest.Add(symbol.Element(p+"spPr")!.Elements().Where(IsStyle).Select(e=>new XElement(e)));
                if(symbol.Element(p+"style") is XElement reference)dest.AddAfterSelf(new XElement(reference));
            }
        }
        if (content?["body"] is JsonValue body && body.TryGetValue<string>(out var note) && !string.IsNullOrWhiteSpace(note))
            writer.Text("CAD页说明", note, 65, 430, 408, 550, 20, "373737");
        foreach(var group in groups)
        {
            XElement? textSample=null;
            if(selection[group.Handle]["indicatorTextStyleId"] is JsonValue textId &&
                (!symbols.TryGetValue((int)textId,out textSample) || textSample.Element(p+"txBody")==null))
                throw new SiteContextException("模板缺少该地块的指标文字对象："+group.Handle);
            if(layout["parcelStyles"]!=null && textSample==null)throw new SiteContextException("请绑定该地块的指标文字样式："+group.Handle);
            ParcelIndicators.Draw(writer,group,parcels[group.Handle].Select(Place).ToArray(),textSample);
        }
        return count;
    }

    // LWPOLYLINE bulge is the tangent of a quarter of the included arc angle.
    private static List<Point> Vertices(JsonArray vertices, bool closed)
    {
        var points = vertices.Select(v => new Point((double)v![0]!, (double)v[1]!)).ToArray();
        var result = new List<Point> { points[0] };
        for (int i = 0; i < points.Length - (closed ? 0 : 1); i++)
        {
            var start = points[i];
            var end = points[(i + 1) % points.Length];
            double bulge = (double?)vertices[i]![2] ?? 0;
            double dx = end.X-start.X, dy = end.Y-start.Y;
            if (Math.Abs(bulge) > 1e-10 && (dx != 0 || dy != 0))
            {
                double k = (1-bulge*bulge)/(4*bulge);
                double cx = (start.X+end.X)/2-dy*k, cy = (start.Y+end.Y)/2+dx*k;
                double angle = Math.Atan2(start.Y-cy,start.X-cx), sweep = 4*Math.Atan(bulge);
                double radius = Math.Sqrt(Math.Pow(start.X-cx,2)+Math.Pow(start.Y-cy,2));
                int segments = (int)Math.Ceiling(Math.Abs(sweep)/(Math.PI/90));
                for (int j=1; j<segments; j++)
                {
                    double at = angle+sweep*j/segments;
                    result.Add(new Point(cx+radius*Math.Cos(at),cy+radius*Math.Sin(at)));
                }
            }
            result.Add(end);
        }
        return result;
    }
}
