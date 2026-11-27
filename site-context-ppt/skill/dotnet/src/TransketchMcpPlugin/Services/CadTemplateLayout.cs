using System.Numerics;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TransketchMcpPlugin;

internal static class CadTemplateLayout
{
    private sealed record Item(int Id,string Name,string Text,XElement Shape,Matrix3x2 Matrix,Vector2 Size,Vector2 Min,Vector2 Max);
    private static readonly XNamespace A=PptxNames.A,P=PptxNames.P;
    private static Vector2 Pair(XElement? e,string x,string y)=>new((float?)e?.Attribute(x)??0,(float?)e?.Attribute(y)??0);
    private static Matrix3x2 Orientation(XElement x,Vector2 size)
    {
        var flip=Matrix3x2.CreateScale((bool?)x.Attribute("flipH")==true?-1:1,(bool?)x.Attribute("flipV")==true?-1:1,size/2);
        return flip*Matrix3x2.CreateRotation(((float?)x.Attribute("rot")??0)*MathF.PI/(60000*180),size/2);
    }
    private static void Walk(XElement tree,Matrix3x2 parent,List<Item> items)
    {
        foreach(var obj in tree.Elements())
        {
            var props=obj.Element(P+"grpSpPr")??obj.Element(P+"spPr");
            var x=props?.Element(A+"xfrm");
            if(x==null)continue;
            var offset=Pair(x.Element(A+"off"),"x","y");var size=Pair(x.Element(A+"ext"),"cx","cy");
            var matrix=Orientation(x,size)*Matrix3x2.CreateTranslation(offset)*parent;
            if(obj.Name==P+"grpSp")
            {
                var origin=Pair(x.Element(A+"chOff"),"x","y");var extent=Pair(x.Element(A+"chExt"),"cx","cy");
                if(extent.X!=0 && extent.Y!=0)Walk(obj,Matrix3x2.CreateTranslation(-origin)*Matrix3x2.CreateScale(size/extent)*matrix,items);
                continue;
            }
            var nv=obj.Elements().Elements(P+"cNvPr").FirstOrDefault();
            if(nv==null)continue;
            var corners=new[]{Vector2.Zero,new Vector2(size.X,0),size,new Vector2(0,size.Y)}.Select(v=>Vector2.Transform(v,matrix)).ToArray();
            items.Add(new((int)nv.Attribute("id")!, (string?)nv.Attribute("name")??"",string.Concat(obj.Descendants(A+"t").Select(e=>e.Value)),obj,matrix,size,
                corners.Aggregate(Vector2.Min),corners.Aggregate(Vector2.Max)));
        }
    }
    private static bool Contains(Item face,Vector2 center)
    {
        if(!Matrix3x2.Invert(face.Matrix,out var inverse))return false;
        var q=Vector2.Transform(center,inverse);
        if(q.X<0 || q.Y<0 || q.X>face.Size.X || q.Y>face.Size.Y)return false;
        var path=face.Shape.Descendants(A+"path").FirstOrDefault();
        if(path==null)return true;
        var extent=Pair(path,"w","h");
        if(extent.X<=0 || extent.Y<=0 || path.Elements().Any(e=>e.Name!=A+"moveTo" && e.Name!=A+"lnTo" && e.Name!=A+"close"))return false;
        var vertices=path.Elements().Elements(A+"pt").Select(e=>Pair(e,"x","y")/extent*face.Size).ToArray();
        bool inside=false;
        for(int i=0,j=vertices.Length-1;i<vertices.Length;j=i++)
        {
            var a=vertices[i];var b=vertices[j];
            if((a.Y>q.Y)!=(b.Y>q.Y) && q.X<(b.X-a.X)*(q.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
        }
        return inside;
    }
    private static bool Has(string text,params string[] words)=>words.Any(text.Contains);
    internal static JsonObject Extract(PptxPackage package,string hash)
    {
        var layouts=new JsonObject();
        var imageReview=new JsonArray();
        foreach(var (page,part) in PptxParts.SlideParts(package))
        {
            var items=new List<Item>();
            Walk(package.GetXml(part).Descendants(P+"spTree").First(),Matrix3x2.CreateScale(1f/9525),items);
            var labels=items.Where(i=>Has(i.Text,"容积率","用地面积","建筑密度","绿地率","计容面积","用地性质")).ToArray();
            if(labels.Length==0 && !items.Any(i=>Has(i.Text+i.Name,"红线","退界","退线","建筑后退","规划指标","勘测定界","用地范围")))
            {
                if(items.Any(i=>i.Shape.Name==P+"pic"))imageReview.Add((JsonNode?)page);
                continue;
            }
            var faces=items.Where(i=>i.Shape.Name==P+"sp" && i.Text.Trim().Length==0 &&
                (i.Shape.Descendants(A+"close").Any() || (string?)i.Shape.Descendants(A+"prstGeom").FirstOrDefault()?.Attribute("prst") is "rect" or "roundRect") &&
                i.Shape.Element(P+"spPr")?.Element(A+"noFill")==null && Math.Min(i.Max.X-i.Min.X,i.Max.Y-i.Min.Y)>12).ToArray();
            var styles=new JsonObject();var evidence=new JsonArray();
            foreach(var face in faces)
            {
                var matches=labels.Where(l=>Contains(face,(l.Min+l.Max)/2)).ToArray();
                var slot=new JsonObject{["parcelStyleId"]=face.Id};
                if(matches.Length==1 && faces.Count(f=>Contains(f,(matches[0].Min+matches[0].Max)/2))==1)
                    slot["indicatorTextStyleId"]=matches[0].Id;
                styles["shape-"+face.Id]=slot;
                evidence.AddNode(new JsonObject{["id"]=face.Id,["name"]=face.Name,["textCandidates"]=SiteJson.StringArray(matches.Select(m=>m.Id.ToString()))});
            }
            var layout=new JsonObject{["keepIds"]=new JsonArray(),["parcelStyles"]=styles,["candidates"]=evidence,
                ["textObjects"]=SiteJson.StringArray(labels.Select(l=>$"{l.Id} {l.Text[..Math.Min(l.Text.Length,48)]}"))};
            if(faces.Length>0)
            {
                var min=faces.Select(f=>f.Min).Aggregate(Vector2.Min);var max=faces.Select(f=>f.Max).Aggregate(Vector2.Max);
                layout["frame"]=new JsonArray((double)min.X,(double)min.Y,(double)(max.X-min.X),(double)(max.Y-min.Y));
            }
            layouts[page.ToString()]=layout;
        }
        return new JsonObject{["template_sha256"]=hash,["reviewed"]=false,["imagePagesForReview"]=imageReview,["cadPageLayout"]=layouts};
    }
}
