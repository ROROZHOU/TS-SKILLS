using System.Text.Json.Nodes;
namespace TransketchMcpPlugin;

internal static class DeliveryReuse
{
    internal static McpComposeResult Execute(string action,string run,string profile,string? baseline,string? pages,CancellationToken cancellationToken,Func<McpComposeResult> build)
    {
        if (action is not ("compose" or "validate" or "reuse")) return build();
        var work=Path.Combine(Path.GetFullPath(run),"过程");
        run=Path.GetFullPath(run);
        var receipt=Path.Combine(work,"交付复用.json");
        using var deliveryLock=ArtifactReceipt.Lock(receipt,cancellationToken);
        var signature=new JsonArray("delivery-v7",Path.GetFullPath(run),ArtifactReceipt.Digest(profile,cancellationToken),pages,
            baseline==null ? null : ArtifactReceipt.Digest(baseline,cancellationToken));
        var settings=SiteContextProfile.Load(profile);
        signature.Add(settings.Template["cadPageLayout"]?.DeepClone());
        var delivery=Path.Combine(run,"成果",settings.OutputFileName);
        var report=Path.Combine(work,ProfileJson.Text(settings.Acceptance,"reportFileName"));
        // Include source material, rendered outputs, lineage and the completed validation report.
        string Fingerprint(bool validated)
        {
            var paths=Directory.EnumerateFiles(work,"*",SearchOption.AllDirectories)
                .Where(path=>path!=receipt && path!=receipt+".receipt" && (validated || path!=report)).Append(delivery).Distinct().Order(StringComparer.Ordinal);
            var hashes=new JsonObject();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hashes[Path.GetRelativePath(run,path)]=File.Exists(path) ? ArtifactReceipt.Digest(path,cancellationToken) : null;
            }
            return new JsonArray(signature.DeepClone(),hashes).ToJsonString();
        }
        if (File.Exists(receipt) && File.Exists(delivery) && File.Exists(report) && ArtifactReceipt.Matches(receipt,Fingerprint(true)))
        {
            var stored=SiteJson.LoadObject(receipt);
            if (action=="reuse") return new McpComposeResult { Action="reuse", Compose=stored["compose"]!.Deserialize(PluginJsonContext.Default.McpComposeResult)!.Compose,
                Validation=stored["validate"]!.Deserialize(PluginJsonContext.Default.McpComposeResult)!.Validation };
            if (stored[action] is JsonNode entry)
                return JsonSerializer.Deserialize(entry.ToJsonString(),PluginJsonContext.Default.McpComposeResult)!;
        }
        if (action=="reuse") return new McpComposeResult { Action="miss" };
        var composed=action=="validate" && File.Exists(receipt) && ArtifactReceipt.Matches(receipt,Fingerprint(false));
        var result=build();
        if (action=="compose" || composed && result.Validation?.GenerationAndProvenancePass==true && result.Validation.DataCompletenessPass)
        {
            var stored=action=="validate" && File.Exists(receipt) ? SiteJson.LoadObject(receipt) : new JsonObject();
            stored[action]=JsonSerializer.SerializeToNode(result,PluginJsonContext.Default.McpComposeResult);
            var fingerprint = Fingerprint(action=="validate");
            cancellationToken.ThrowIfCancellationRequested();
            SiteJson.SaveFile(receipt,stored);
            ArtifactReceipt.Save(receipt,fingerprint);
        }
        return result;
    }
}
