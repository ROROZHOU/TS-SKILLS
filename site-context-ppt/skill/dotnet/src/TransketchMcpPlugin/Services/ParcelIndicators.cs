using System.Text.Json.Nodes;
using System.Xml.Linq;
using Point = TransketchMcpPlugin.ComposeGeometry.Point;

namespace TransketchMcpPlugin;

internal static class ParcelIndicators
{
    internal sealed record Group(string Handle, string Title, JsonObject Values)
    {
        public string[] Lines => (Title.Length == 0 ? Array.Empty<string>() : new[] { Title })
            .Concat(Values.Select(row => row.Key + "：" + (string)row.Value!)).ToArray();
    }

    internal static List<Group> Read(JsonNode? input,string primary,string? title)
    {
        if(input is null) return [];
        if(input is JsonObject single) return single.Count==0 ? [] : [new(primary,title??"",single)];
        if(input is not JsonArray groups) throw new SiteContextException("指标须为单地块对象或多地块数组");
        var result=new List<Group>();
        foreach(var group in groups)
        {
            var handle=((string?)group?["handle"])?.Trim().ToUpperInvariant();
            if(string.IsNullOrEmpty(handle) || group?["values"] is not JsonObject { Count: >0 } values ||
                result.Any(g=>g.Handle.Equals(handle,StringComparison.OrdinalIgnoreCase)))
                throw new SiteContextException("每组规划指标需要唯一的 CAD handle 和非空 values");
            result.Add(new(handle,(string?)group["name"]??"",values));
        }
        return result;
    }

    // The complete text rectangle must lie inside even a concave parcel.
    internal static bool Fits(IReadOnlyList<Point> closed, double x,double y,double w,double h)
    {
        if(w<=0 || h<=0 || closed.Count<4)return false;
        double cx=x+w/2,cy=y+h/2;
        bool inside=false;
        for(int i=1;i<closed.Count;i++)
        {
            var a=closed[i-1];var b=closed[i];
            if((a.Y>cy)!=(b.Y>cy) && cx<(b.X-a.X)*(cy-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
        }
        return inside && ComposeGeometry.ClipLine(closed,x,y,x+w,y+h).Count==0;
    }

    internal static bool Valid(XDocument slide,Group group)
    {
        var p=PptxNames.P;var a=PptxNames.A;
        var shapes=slide.Descendants(p+"spTree").First().Elements().ToList();
        string? Name(XElement e)=>(string?)e.Element(p+"nvSpPr")?.Element(p+"cNvPr")?.Attribute("name");
        var label=shapes.SingleOrDefault(e=>Name(e)=="地块指标:"+group.Handle);
        var parcel=shapes.SingleOrDefault(e=>Name(e)=="CAD:用地红线:"+group.Handle);
        if(parcel==null || label==null || shapes.IndexOf(label)<=shapes.IndexOf(parcel) ||
           string.Concat(label.Descendants(a+"t").Select(t=>t.Value))!=string.Concat(group.Lines))return false;
        var props=parcel.Element(p+"spPr")!;
        var path=props.Descendants(a+"path").First();
        var offset=props.Element(a+"xfrm")!.Element(a+"off")!;
        var extent=props.Element(a+"xfrm")!.Element(a+"ext")!;
        var ring=path.Descendants(a+"pt").Select(pt=>new Point((double)offset.Attribute("x")!+
            (double)pt.Attribute("x")!*(double)extent.Attribute("cx")!/(double)path.Attribute("w")!,
            (double)offset.Attribute("y")!+(double)pt.Attribute("y")!*(double)extent.Attribute("cy")!/(double)path.Attribute("h")!)).ToList();
        ring.Add(ring[0]);
        var rect=label.Element(p+"spPr")!.Element(a+"xfrm")!;
        var at=rect.Element(a+"off")!;var size=rect.Element(a+"ext")!;
        return Fits(ring,(double)at.Attribute("x")!,(double)at.Attribute("y")!,(double)size.Attribute("cx")!,(double)size.Attribute("cy")!);
    }

    internal static void Draw(PptxSlideWriter writer,Group group,Point[] vertices,XElement? sample)
    {
        var a=PptxNames.A;
        var paragraphs=sample?.Element(PptxNames.P+"txBody")?.Elements(a+"p").ToArray();
        XElement? Style(int i)=>paragraphs?.ElementAtOrDefault(i)?.Descendants(a+"rPr").FirstOrDefault();
        var bodyStyle=Style(1)??Style(0);
        double original=(double?)bodyStyle?.Attribute("sz")/100??18;
        var lines=group.Lines;
        double left=vertices.Min(q=>q.X),right=vertices.Max(q=>q.X),top=vertices.Min(q=>q.Y),bottom=vertices.Max(q=>q.Y);
        var centers=(from i in Enumerable.Range(1,31) from j in Enumerable.Range(1,31)
            orderby (i-16)*(i-16)+(j-16)*(j-16)
            select new Point(left+(right-left)*i/32,top+(bottom-top)*j/32)).ToArray();
        for(double pt=original;pt>=Math.Min(original,12);pt-=1)
        {
            foreach(int columns in new[]{32,24,18,14,10})
            {
                var wrapped=new List<(string text,bool heading)>();
                double widest=0;
                for(int i=0;i<lines.Length;i++)
                {
                    string part="";double units=0;
                    foreach(var ch in lines[i])
                    {
                        double next=ch<128 ? 0.68 : 1.08;
                        if(units+next>columns && part.Length>0)
                        {wrapped.Add((part,i==0 && group.Title.Length>0));widest=Math.Max(widest,units);part="";units=0;}
                        part+=ch;units+=next;
                    }
                    wrapped.Add((part,i==0 && group.Title.Length>0));widest=Math.Max(widest,units);
                }
                double width=(widest+1)*pt*4/3, height=(wrapped.Count*1.5+0.3)*pt*4/3;
                foreach(var center in centers)
                {
                    double x=center.X-width/2,y=center.Y-height/2;
                    if(!Fits(vertices,x-4,y-4,width+8,height+8))continue;
                    var shape=writer.Text("地块指标:"+group.Handle,string.Join("\n",wrapped.Select(r=>r.text)),x,y,width,height,pt,"373737");
                    var ps=shape.Descendants(a+"p").ToArray();
                    for(int k=0;k<ps.Length;k++)
                    {
                        var style=wrapped[k].heading ? Style(0)??bodyStyle : bodyStyle;
                        if(style==null)continue;
                        var cloned=new XElement(style);cloned.SetAttributeValue("sz",(int)Math.Round(pt*100));
                        ps[k].Descendants(a+"rPr").Single().ReplaceWith(cloned);
                    }
                    return;
                }
            }
        }
        throw new SiteContextException($"地块 {group.Handle} 内放不下完整指标，请放大该页图框或分地块组版。");
    }
}
