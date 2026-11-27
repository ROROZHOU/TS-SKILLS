using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.VisualBasic.FileIO;

namespace TransketchMcpPlugin;

internal static class TopicSource
{
    // Remote inputs are downloaded by the host's web tool and retained in the run for provenance.
    internal static JsonObject Read(string path, string classField)
    {
        if (!File.Exists(path)) throw new SiteContextException("找不到专题源文件：" + path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" or ".geojson" => SiteJson.LoadObject(path),
            ".csv" => Csv(path,classField),
            ".kml" => Kml(path),
            _ => throw new SiteContextException("支持 GeoJSON、KML 和经纬度点表 CSV；图件须先按原图坐标提取几何并配准。")
        };
    }

    private static JsonObject Feature(string kind, JsonNode coords, JsonObject properties) =>
        new() { ["type"]="Feature", ["geometry"]=new JsonObject{["type"]=kind,["coordinates"]=coords},["properties"]=properties };
    private static JsonObject Collection(JsonArray items) => new(){["type"]="FeatureCollection",["features"]=items};
    private static double Number(string raw) => double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out var v) && double.IsFinite(v)
        ? v : throw new SiteContextException("专题坐标不是有限数字：" + raw);

    private static JsonObject Csv(string path,string classField)
    {
        using var table=new TextFieldParser(path,System.Text.Encoding.UTF8,true){TextFieldType=FieldType.Delimited,HasFieldsEnclosedInQuotes=true};
        table.SetDelimiters(",");
        var fields=table.ReadFields() ?? [];
        var x=Array.IndexOf(fields,"lng"); var y=Array.IndexOf(fields,"lat");
        if(x<0 || y<0 || !fields.Contains(classField) || fields.Distinct().Count()!=fields.Length)
            throw new SiteContextException("CSV 需要 lng、lat 和类别列，列名不可重复。");
        var items=new JsonArray();
        while(!table.EndOfData)
        {
            var row=table.ReadFields() ?? [];
            if(row.Length!=fields.Length) throw new SiteContextException("CSV 行列数不一致。");
            var props=new JsonObject();
            for(int i=0;i<row.Length;i++)props[fields[i]]=row[i];
            items.AddNode(Feature("Point",new JsonArray(Number(row[x]),Number(row[y])),props));
        }
        return Collection(items);
    }

    private static JsonObject Kml(string path)
    {
        var xml=XDocument.Load(path);
        XNamespace k="http://www.opengis.net/kml/2.2";
        var items=new JsonArray();
        JsonArray Points(XElement? geom)
        {
            var raw=geom?.Element(k+"coordinates")?.Value ?? throw new SiteContextException("KML 缺 coordinates。");
            var points=new JsonArray();
            foreach(var token in raw.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries))
            {
                var parts=token.Split(',');
                if(parts.Length<2) throw new SiteContextException("KML 坐标无效。");
                points.AddNode(new JsonArray(Number(parts[0]),Number(parts[1])));
            }
            return points;
        }
        foreach(var mark in xml.Descendants(k+"Placemark"))
        {
            var props=new JsonObject{["name"]=mark.Element(k+"name")?.Value};
            foreach(var value in mark.Descendants().Where(n=>n.Name==k+"Data"||n.Name==k+"SimpleData"))
                if(value.Attribute("name")?.Value is string name)props[name]=value.Element(k+"value")?.Value??value.Value;
            var geoms=mark.Descendants().Where(n=>n.Name==k+"Point" || n.Name==k+"LineString" || n.Name==k+"Polygon").ToList();
            if(geoms.Count==0)throw new SiteContextException("KML 地物缺少支持的点、线、面。");
            foreach(var g in geoms)
            {
                JsonNode coords;
                if(g.Name==k+"Polygon")
                {
                    var rings=new JsonArray(); rings.AddNode(Points(g.Element(k+"outerBoundaryIs")?.Element(k+"LinearRing")));
                    foreach(var hole in g.Elements(k+"innerBoundaryIs"))rings.AddNode(Points(hole.Element(k+"LinearRing")));
                    coords=rings;
                }
                else
                {
                    var points=Points(g);
                    coords=g.Name==k+"Point" ? points.Count==1 ? points[0]!.DeepClone() : throw new SiteContextException("KML Point 需要单个坐标。") : points;
                }
                items.AddNode(Feature(g.Name.LocalName,coords,(JsonObject)props.DeepClone()));
            }
        }
        return Collection(items);
    }
}

