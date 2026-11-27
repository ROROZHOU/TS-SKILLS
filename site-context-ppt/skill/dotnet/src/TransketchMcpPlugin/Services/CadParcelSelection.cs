using System.Text.Json.Nodes;

namespace TransketchMcpPlugin;

internal static class CadParcelSelection
{
    // Project handles come from the drawing. Template object IDs belong to one slide only.
    internal static Dictionary<string,JsonObject> Resolve(JsonNode? project,JsonObject? page,JsonObject layout,
        string primary,IReadOnlyList<ParcelIndicators.Group> groups)
    {
        var result=new Dictionary<string,JsonObject>(StringComparer.OrdinalIgnoreCase);
        if(project is null)
        {
            if(page?["parcelKeys"]!=null || page?["parcelSlots"]!=null || layout["parcelStyles"]!=null)
                throw new SiteContextException("模板或页面使用多地块绑定，需在页面内容中填写 cadParcels");
            result[primary]=layout;
            foreach(var group in groups)result[group.Handle]=layout;
            return result;
        }
        if(project is not JsonArray { Count: >0 } items) throw new SiteContextException("cadParcels 需要非空地块清单");
        var handles=new Dictionary<string,string>(StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var parcel in items)
        {
            var key=(string?)parcel?["key"];var handle=((string?)parcel?["handle"])?.Trim().ToUpperInvariant();
            if(string.IsNullOrWhiteSpace(key)||string.IsNullOrEmpty(handle)||!seen.Add(handle)||!handles.TryAdd(key,handle))
                throw new SiteContextException("cadParcels 的 key 和 DWG handle 均须非空且唯一");
        }
        var keys=page?["parcelKeys"] is JsonArray subset ? subset.Select(n=>(string)n!).ToArray() : handles.Keys.ToArray();
        if(keys.Length==0 || keys.Distinct().Count()!=keys.Length)throw new SiteContextException("parcelKeys 须非空且无重复");
        var choices=page?["parcelSlots"] as JsonObject;
        if(choices!=null && choices.Any(c=>!keys.Contains(c.Key)))throw new SiteContextException("parcelSlots 含未展示的地块");
        foreach(var key in keys)
        {
            if(!handles.TryGetValue(key,out var handle))throw new SiteContextException("未知地块："+key);
            var slot=(string?)choices?[key];
            if(layout["parcelStyles"] is JsonObject styles)
            {
                if(slot==null && styles.Count==1)slot=styles.First().Key;
                if(slot==null || styles[slot] is not JsonObject binding)
                    throw new SiteContextException("请为本页地块选择已审核的模板面样式："+key);
                result.Add(handle,binding);
            }
            else if(slot!=null)throw new SiteContextException("本页未定义 parcelStyles："+slot);
            else result.Add(handle,layout);
        }
        if(groups.Any(g=>!result.ContainsKey(g.Handle)))throw new SiteContextException("指标绑定的地块未列入本页 parcelKeys");
        return result;
    }
}
