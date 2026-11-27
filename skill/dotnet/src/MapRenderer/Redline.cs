using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ACadSharp.Entities;
using ACadSharp.IO;

internal static class Redline
{
    private const double Radius = 6378137;
    private const double Rad = Math.PI / 180;
    private readonly record struct Point(double X, double Y)
    {
        public JsonArray Pair() => new(X, Y);
        public double Distance(Point p) => Math.Sqrt(Math.Pow(X-p.X,2)+Math.Pow(Y-p.Y,2));
    }

    internal static JsonObject Process(JsonObject request)
    {
        string NeedPath(string key)
        {
            var path = (string?)request[key];
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException($"需要绝对路径：{key}");
            return Path.GetFullPath(path);
        }
        var dwg = NeedPath("dwgPath");
        using var input = File.OpenRead(dwg);
        var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var handle=(string?)request["handle"];
        string? output = string.IsNullOrWhiteSpace(handle) ? null : NeedPath("outputPath");
        using var outputLock=output==null ? null : ArtifactReceipt.Lock(output);
        var signature=new JsonArray("redline-v3", hash,(string?)request["layer"],handle,
            request["registrationPath"] is null ? null : ArtifactReceipt.Digest(NeedPath("registrationPath"))).ToJsonString();
        if (output != null && ArtifactReceipt.Matches(output,signature))
        {
            var saved=JsonNode.Parse(File.ReadAllText(output))!;
            return new JsonObject { ["status"]=saved["status"]!.DeepClone(), ["outputPath"]=output, ["details"]=saved["details"]!.DeepClone() };
        }
        input.Position = 0;
        var doc = DwgReader.Read(input);
        var candidates = doc.Entities.OfType<LwPolyline>().Where(p => p.IsClosed && p.Vertices.Count >= 3 &&
            (request["layer"] is null || p.Layer.Name.Equals((string?)request["layer"], StringComparison.OrdinalIgnoreCase))).ToList();
        var list = new JsonArray();
        foreach (var p in candidates)
            list.Add(new JsonObject { ["handle"]=p.Handle.ToString("X"), ["layer"]=p.Layer.Name, ["vertices"]=p.Vertices.Count,
                ["curved"]=p.Vertices.Any(v => v.Bulge != 0) });
        if (output is null || string.IsNullOrWhiteSpace(handle)) return new JsonObject { ["status"]="candidates", ["details"]=list };
        var entity=candidates.SingleOrDefault(p=>p.Handle.ToString("X").Equals(handle,StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("未找到所选闭合 LWPOLYLINE；请核对图层和 handle。");
        if (Math.Abs(entity.Normal.X)>1e-9 || Math.Abs(entity.Normal.Y)>1e-9 || Math.Abs(entity.Normal.Z-1)>1e-9)
            throw new InvalidDataException("仅支持法向量朝 +Z 的平面红线。");
        if (File.Exists(output)) throw new InvalidDataException("输出已存在，请指定新文件。");
        var vertices=new JsonArray();
        foreach(var v in entity.Vertices) vertices.Add(new JsonArray(v.Location.X,v.Location.Y,v.Bulge));
        var metadata=new JsonObject { ["handle"]=entity.Handle.ToString("X"), ["layer"]=entity.Layer.Name,
            ["source"]=dwg, ["sha256"]=hash, ["closed"]=true, ["elevation"]=entity.Elevation,
            ["parser"]="ACadSharp 3.8.35", ["drawingUnits"]=doc.Header.InsUnits.ToString() };
        var file=new JsonObject { ["metadata"]=metadata, ["cadVertices"]=vertices };
        // Keep planar linework in drawing units. Both CAD pages use this background and the same boundary.
        var linework=new JsonArray();
        foreach (var item in doc.Entities)
        {
            if (item.Handle==entity.Handle) continue;
            JsonArray path;
            bool closed=false;
            if (item is Line line)
                path=new JsonArray(new JsonArray(line.StartPoint.X,line.StartPoint.Y,0),new JsonArray(line.EndPoint.X,line.EndPoint.Y,0));
            else if (item is LwPolyline poly && poly.Vertices.Count>=2 && Math.Abs(poly.Normal.Z-1)<1e-9)
            {
                path=new JsonArray(); closed=poly.IsClosed;
                foreach (var v in poly.Vertices) path.Add(new JsonArray(v.Location.X,v.Location.Y,v.Bulge));
            }
            else continue;
            linework.Add(new JsonObject { ["handle"]=item.Handle.ToString("X"), ["layer"]=item.Layer.Name, ["closed"]=closed, ["vertices"]=path });
        }
        file["cadLinework"]=linework;
        var details=new JsonObject { ["handle"]=metadata["handle"]!.DeepClone(), ["vertices"]=vertices.Count };
        string status="extracted";
        if (request["registrationPath"] is not null)
        {
            var control=JsonNode.Parse(File.ReadAllText(NeedPath("registrationPath")))!.AsObject();
            Register(entity,control,file,metadata,details);
            status="registered";
        }
        file["details"]=details.DeepClone(); file["status"]=status;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var tmp=output+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllText(tmp,file.ToJsonString()); File.Move(tmp,output); }
        finally { File.Delete(tmp); }
        ArtifactReceipt.Save(output,signature);
        return new JsonObject { ["status"]=status, ["outputPath"]=output, ["details"]=details };
    }

    private static void Register(LwPolyline poly, JsonObject c, JsonObject file, JsonObject meta, JsonObject result)
    {
        if ((string?)c["crs"]!="GCJ02" || string.IsNullOrWhiteSpace((string?)c["source"]))
            throw new InvalidDataException("请提供 crs=GCJ02 的实测控制点及其 source。");
        var tolerance=(double?)c["maxErrorM"] ?? 0;
        if (!double.IsFinite(tolerance) || tolerance<=0) throw new InvalidDataException("需要正数 maxErrorM。");
        static Point Pair(JsonNode? v)
        {
            if (v is not JsonArray a || a.Count!=2 || a[0] is null || a[1] is null) throw new InvalidDataException("点须为两个数值。");
            var p=new Point((double)a[0]!, (double)a[1]!);
            if (!double.IsFinite(p.X)||!double.IsFinite(p.Y)) throw new InvalidDataException("坐标必须有限。");
            return p;
        }
        var controls=c["fit"] as JsonArray;
        var checks=c["check"] as JsonArray;
        if(controls is null || controls.Count<2 || checks is null || checks.Count<1)
            throw new InvalidDataException("至少需要两对拟合点及一对独立检查点。");
        var origin=Pair(controls[0]?["map"]);
        double cos=Math.Cos(origin.Y*Rad);
        Point Project(Point ll)
        {
            if(Math.Abs(ll.X)>180 || Math.Abs(ll.Y)>85) throw new InvalidDataException("地图经纬度越界。");
            return new((ll.X-origin.X)*Rad*Radius*cos, Radius*cos*Math.Log(Math.Tan(Math.PI/4+ll.Y*Rad/2)/Math.Tan(Math.PI/4+origin.Y*Rad/2)));
        }
        Point Unproject(Point p)=>new(origin.X+p.X/(Rad*Radius*cos),
            (2*Math.Atan(Math.Exp(p.Y/(Radius*cos))*Math.Tan(Math.PI/4+origin.Y*Rad/2))-Math.PI/2)/Rad);
        var pairs=controls.Select(n=>(s:Pair(n?["cad"]),t:Project(Pair(n?["map"])))).ToArray();
        var src=new Point(pairs.Average(v=>v.s.X),pairs.Average(v=>v.s.Y));
        var dst=new Point(pairs.Average(v=>v.t.X),pairs.Average(v=>v.t.Y));
        double den=0,a=0,b=0;
        foreach(var v in pairs)
        {
            double x=v.s.X-src.X,y=v.s.Y-src.Y,u=v.t.X-dst.X,w=v.t.Y-dst.Y;
            den+=x*x+y*y; a+=x*u+y*w; b+=x*w-y*u;
        }
        if(den<=0) throw new InvalidDataException("拟合 CAD 点重合。");
        a/=den;b/=den;
        double scale=Math.Sqrt(a*a+b*b);
        if(!(scale>0)||!double.IsFinite(scale)) throw new InvalidDataException("配准比例无效。");
        Point Map(double x,double y)=>new(dst.X+a*(x-src.X)-b*(y-src.Y), dst.Y+b*(x-src.X)+a*(y-src.Y));
        var errors=pairs.Select(v=>Map(v.s.X,v.s.Y).Distance(v.t)).ToList();
        foreach(var n in checks)
        {
            var cp=Pair(n?["cad"]);var target=Project(Pair(n?["map"]));
            if(pairs.Any(v=>cp.Distance(v.s)*scale<0.001 || target.Distance(v.t)<0.001))
                throw new InvalidDataException("检查点与拟合点重复。");
            errors.Add(Map(cp.X,cp.Y).Distance(target));
        }
        if(errors.Any(e=>!double.IsFinite(e)||e>tolerance)) throw new InvalidDataException("配准残差超出 maxErrorM，请核对配对关系、单位和坐标系。");
        var ring=new List<Point>();
        for(int i=0;i<poly.Vertices.Count;i++)
        {
            var v=poly.Vertices[i];var next=poly.Vertices[(i+1)%poly.Vertices.Count];
            var start=Map(v.Location.X,v.Location.Y);var end=Map(next.Location.X,next.Location.Y);
            ring.Add(start);
            var bulge=v.Bulge;var chord=start.Distance(end);
            if(Math.Abs(bulge)<1e-12 || chord==0) continue;
            var k=(1-bulge*bulge)/(4*bulge);
            var center=new Point((start.X+end.X)/2-(end.Y-start.Y)*k,(start.Y+end.Y)/2+(end.X-start.X)*k);
            var angle=Math.Atan2(start.Y-center.Y,start.X-center.X);var sweep=4*Math.Atan(bulge);var radius=start.Distance(center);
            var steps=Math.Ceiling(Math.Abs(sweep)/(2*Math.Acos(Math.Clamp(1-0.02/radius,-1,1))));
            if(!double.IsFinite(steps)||steps>100000) throw new InvalidDataException("圆弧过大，请核验图纸单位与配准比例。");
            for(int j=1;j<steps;j++)ring.Add(new(center.X+radius*Math.Cos(angle+sweep*j/steps),center.Y+radius*Math.Sin(angle+sweep*j/steps)));
        }
        double twiceArea=0,cx=0,cy=0;
        for(int i=0;i<ring.Count;i++)
        {
            var p=ring[i];var q=ring[(i+1)%ring.Count];var cross=p.X*q.Y-q.X*p.Y;
            twiceArea+=cross;cx+=(p.X+q.X)*cross;cy+=(p.Y+q.Y)*cross;
        }
        if(Math.Abs(twiceArea)<1e-8)throw new InvalidDataException("边界面积为零。");
        var centroid=new Point(cx/(3*twiceArea),cy/(3*twiceArea));
        var ll=Unproject(centroid);
        // Existing slide overlay uses local ground metres relative to its map centre.
        var ratio=Math.Cos(ll.Y*Rad)/cos;
        var points=new JsonArray();foreach(var p in ring)points.Add(new JsonArray((p.X-centroid.X)*ratio,(p.Y-centroid.Y)*ratio));
        file["points"]=points;meta["centroid"]=new JsonArray(0,0);meta["registration"]=c.DeepClone();
        meta["maxResidualM"]=errors.Max();meta["areaM2"]=Math.Abs(twiceArea)*ratio*ratio/2;
        var project=new JsonObject{["lng"]=ll.X,["lat"]=ll.Y,["coord"]="GCJ02"};
        meta["project"]=project.DeepClone(); result["project"]=project;result["maxResidualM"]=errors.Max();
    }
}
