using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

// A reviewed tracing from a PDF page or raster image uses its original page/pixel coordinates.
internal static class DrawingRegistration
{
    internal static void ToGcj02(JsonObject document, JsonObject evidence, bool cad = false)
    {
        var r=evidence["registration"] as JsonObject ?? throw new SiteContextException("图件缺少配准参数。");
        var fit=r["fit"] as JsonArray;
        var check=r["check"] as JsonArray;
        if(fit?.Count!=3 || check is null || check.Count==0 || (double?)r["maxErrorM"] is not double tolerance || !double.IsFinite(tolerance) || tolerance<=0)
            throw new SiteContextException("图件需要三对非共线控制点、独立检核点和正数 maxErrorM。");
        static (double x,double y) Pair(JsonNode? n)
        {
            if(n is not JsonArray {Count:2} || (double?)n[0] is not double x || (double?)n[1] is not double y || !double.IsFinite(x)||!double.IsFinite(y))
                throw new SiteContextException("图件配准点格式错误。");
            return (x,y);
        }
        const double scale=6378137*Math.PI/180;
        (double x,double y) M(JsonNode? n) {var p=Pair(n); if(cad)return p; if(Math.Abs(p.x)>180||Math.Abs(p.y)>85)throw new SiteContextException("GCJ02 经纬度越界。");
            return (p.x,Math.Asinh(Math.Tan(p.y*Math.PI/180))*180/Math.PI);}
        var s=fit.Select(v=>Pair(v?["drawing"])).ToArray();
        var t=fit.Select(v=>M(v?[cad ? "cad" : "map"])).ToArray();
        double ax=s[1].x-s[0].x, ay=s[1].y-s[0].y, bx=s[2].x-s[0].x, by=s[2].y-s[0].y;
        double det=ax*by-ay*bx;
        if(Math.Abs(det)<=1e-10*Math.Max(1,Math.Sqrt((ax*ax+ay*ay)*(bx*bx+by*by))))throw new SiteContextException("图件控制点共线。");
        var targetArea=(t[1].x-t[0].x)*(t[2].y-t[0].y)-(t[1].y-t[0].y)*(t[2].x-t[0].x);
        if(Math.Abs(targetArea)<1e-16)throw new SiteContextException("地图控制点重合或共线。");
        (double x,double y) Map((double x,double y) p)
        {
            var x=p.x-s[0].x;var y=p.y-s[0].y;var u=(x*by-y*bx)/det;var v=(ax*y-ay*x)/det;
            return(t[0].x+u*(t[1].x-t[0].x)+v*(t[2].x-t[0].x),t[0].y+u*(t[1].y-t[0].y)+v*(t[2].y-t[0].y));
        }
        double max=0;
        foreach(var cp in check)
        {
            var p=Pair(cp?["drawing"]);var expected=M(cp?[cad ? "cad" : "map"]);var actual=Map(p);
            if(s.Contains(p)||t.Contains(expected))throw new SiteContextException("独立检核点与控制点重复。");
            var error=Math.Sqrt(Math.Pow(actual.x-expected.x,2)+Math.Pow(actual.y-expected.y,2))*(cad ? 1 : scale/Math.Cosh(expected.y*Math.PI/180));
            if(!double.IsFinite(error)||error>tolerance)throw new SiteContextException("图件配准误差超出 maxErrorM。");
            max=Math.Max(max,error);
        }
        void Transform(JsonArray a)
        {
            if(a.Count==0)throw new SiteContextException("图件缺少坐标。");
            if(a[0] is JsonArray){foreach(var v in a)Transform(v!.AsArray());return;}
            var p=Map(Pair(a));a[0]=p.x;a[1]=cad ? p.y : Math.Atan(Math.Sinh(p.y*Math.PI/180))*180/Math.PI;
        }
        var items=document["features"] as JsonArray;
        foreach(var f in items is null ? new[]{document} : items.OfType<JsonObject>())
        {
            Transform(f["geometry"]!["coordinates"]!.AsArray());
            if(f["properties"]?["label_anchor"] is JsonArray anchor)Transform(anchor);
        }
        r["maxResidualM"]=max;
    }
}
