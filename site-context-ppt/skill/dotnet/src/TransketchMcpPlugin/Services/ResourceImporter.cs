using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

/// <summary>
/// 把已核实、已配准的 GeoJSON 转为网页资源要素，保留类别与孔洞
/// </summary>
/// <date>2026年11月16日</date>
internal static class ResourceImporter
{
    /// <summary>
    /// 执行导入
    /// </summary>
    /// <param name="document">GeoJSON 根节点</param>
    /// <param name="evidence">来源证据；调用方已补入 file_sha256</param>
    /// <param name="crs">输入坐标系，仅接受 WGS84 或 GCJ02</param>
    /// <param name="classField">类别字段名</param>
    public static (JsonObject Output, int FeatureCount, int Holes, List<string> Classes) Convert(
        JsonObject document,
        JsonObject evidence,
        string crs,
        string classField)
    {
        if (crs is not ("WGS84" or "GCJ02"))
            throw new SiteContextException("Explicit WGS84 or GCJ02 required; register projected data first");
        if (SiteJson.Text(evidence["status"]) != "verified" || evidence["registration"] is null)
            throw new SiteContextException("Verified provenance and registration required");
        if (SiteJson.Text(evidence["source_url"]).Length == 0 || SiteJson.Text(evidence["title"]).Length == 0)
            throw new SiteContextException("Source URL and title required");

        (double Lng, double Lat) Point(JsonNode? node)
        {
            if (node is not JsonArray array || array.Count < 2)
                throw new SiteContextException("Invalid longitude/latitude");
            var lng = SiteJson.NumberOrNull(array[0]);
            var lat = SiteJson.NumberOrNull(array[1]);
            if (lng is null || lat is null || !double.IsFinite(lng.Value) || !double.IsFinite(lat.Value)
                || lng is < -180 or > 180 || lat is < -90 or > 90)
                throw new SiteContextException("Invalid longitude/latitude");
            return crs == "WGS84" ? Gcj02Transform.Wgs2Gcj(lng.Value, lat.Value) : (lng.Value, lat.Value);
        }

        JsonArray Ring(JsonNode? node)
        {
            if (node is not JsonArray array || array.Count < 4)
                throw new SiteContextException("Polygon ring must be explicitly closed");
            var first = Point(array[0]);
            var last = Point(array[^1]);
            if (first.Lng != last.Lng || first.Lat != last.Lat)
                throw new SiteContextException("Polygon ring must be explicitly closed");

            var ring = new JsonArray();
            foreach (var coordinate in array)
            {
                var (lng, lat) = Point(coordinate);
                ring.AddNode(Coordinate(lng, lat));
            }

            return ring;
        }

        var features = SiteJson.Text(document["type"]) == "FeatureCollection"
            ? document["features"] as JsonArray
            : new JsonArray(document.DeepClone());
        if (features is null)
            throw new SiteContextException("GeoJSON FeatureCollection required");

        var output = new JsonArray();
        var classes = new List<string>();
        var holes = 0;
        for (var index = 0; index < features.Count; index++)
        {
            if (features[index] is not JsonObject feature)
                throw new SiteContextException("GeoJSON FeatureCollection required");

            var properties = feature["properties"] as JsonObject ?? new JsonObject();
            var originalClass = SiteJson.Text(properties[classField]);
            if (string.IsNullOrWhiteSpace(originalClass)) throw new SiteContextException("专题缺少原始类别：" + classField);
            if (!classes.Contains(originalClass)) classes.Add(originalClass);

            if (feature["geometry"] is not JsonObject geometry)
                throw new SiteContextException("GeoJSON FeatureCollection required");
            var type = SiteJson.Text(geometry["type"]);
            var coordinates = geometry["coordinates"];
            string kind;
            JsonArray groups;
            switch (type)
            {
                case "Polygon":
                    kind = "polygon";
                    groups = new JsonArray(coordinates?.DeepClone());
                    break;
                case "MultiPolygon":
                    kind = "polygon";
                    groups = coordinates as JsonArray ?? new JsonArray();
                    break;
                case "LineString":
                    kind = "line";
                    groups = new JsonArray(coordinates?.DeepClone());
                    break;
                case "MultiLineString":
                    kind = "line";
                    groups = coordinates as JsonArray ?? new JsonArray();
                    break;
                case "Point":
                    kind = "point";
                    groups = new JsonArray(coordinates?.DeepClone());
                    break;
                case "MultiPoint":
                    kind = "point";
                    groups = coordinates as JsonArray ?? new JsonArray();
                    break;
                default:
                    throw new SiteContextException("Unsupported geometry " + type);
            }

            if(groups.Count==0) throw new SiteContextException("专题坐标组为空。");
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                var itemEvidence = (JsonObject)evidence.DeepClone();
                itemEvidence["input_crs"] = crs;
                itemEvidence["export_crs"] = "GCJ02";

                var item = new JsonObject
                {
                    ["id"] = SiteJson.Text(feature["id"]).Length > 0
                        ? SiteJson.Text(feature["id"]) + "-" + groupIndex
                        : index + "-" + groupIndex,
                    ["resource_id"] = SiteJson.Text(evidence["resource_id"]) is { Length: > 0 } resourceId
                        ? resourceId
                        : "resource",
                    ["class"] = originalClass,
                    ["kind"] = kind,
                    ["name"] = SiteJson.Text(properties["name"]),
                    ["properties"] = properties.DeepClone(),
                    ["evidence"] = itemEvidence
                };

                if (kind == "polygon")
                {
                    var rings = new JsonArray();
                    foreach (var ringNode in group as JsonArray ?? [])
                        rings.AddNode(Ring(ringNode));
                    if(rings.Count==0) throw new SiteContextException("专题面缺少外环。");
                    item["rings"] = rings;
                    item["coordinates"] = rings.Count > 0 ? rings[0]!.DeepClone() : new JsonArray();
                    holes += Math.Max(0, rings.Count - 1);
                }
                else if (kind == "line")
                {
                    if (group is not JsonArray { Count: >= 2 } line)
                        throw new SiteContextException("Line needs two points");
                    var points = new JsonArray();
                    foreach (var coordinate in line)
                    {
                        var (lng, lat) = Point(coordinate);
                        points.AddNode(Coordinate(lng, lat));
                    }

                    item["coordinates"] = points;
                }
                else
                {
                    var (lng, lat) = Point(group);
                    item["coordinates"] = Coordinate(lng, lat);
                }

                if (properties["label_anchor"] is not null)
                {
                    var (lng, lat) = Point(properties["label_anchor"]);
                    item["label_anchor"] = Coordinate(lng, lat);
                }

                output.AddNode(item);
            }
        }

        return (new JsonObject
        {
            ["resourceFeatures"] = output,
            ["crs"] = "GCJ02",
            ["statistics"] = new JsonObject
            {
                ["features"] = output.Count,
                ["holes"] = holes
            }
        }, output.Count, holes, classes);
    }

    private static JsonArray Coordinate(double lng, double lat) =>
        new(JsonValue.Create(lng), JsonValue.Create(lat));
}
