using System.Text.Json.Nodes;

internal static class CaptureSpec
{
    internal static JsonObject ForPage(JsonObject config, int page)
    {
        var spec=(JsonObject)config.DeepClone();
        spec.Remove("ppt"); spec.Remove("layers"); spec.Remove("_说明");
        var p=config["ppt"]!["pages"]![page.ToString()]!;
        foreach (var name in new[] {"radius","basemapStyle","enabled","rings","verifiedLabels","verifiedPOIs","planningFeatures","resourceFeatures"})
            if (p[name] != null) spec[name]=p[name]!.DeepClone();
        // Numeric strings are accepted by the renderer for compatibility with older runs.
        foreach (var key in new[] {"lng","lat"})
            if (spec["project"]?[key] is JsonValue value && value.TryGetValue<string>(out var raw) &&
                double.TryParse(raw,System.Globalization.CultureInfo.InvariantCulture,out var n)) spec["project"]![key]=n;
        return spec;
    }
}
