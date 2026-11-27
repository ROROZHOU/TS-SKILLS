using System.Text.Json.Nodes;
using Microsoft.Playwright;
public sealed record MapRequest(string RunDirectory, int Width = 1280, int Height = 720, string? PhotosDirectory = null, string? ReuseRunDirectory = null);
public sealed record MapResult(string Directory, int[] Pages, string[] Warnings);

internal static class Renderer
{
    public static async Task<MapResult> CaptureSiteMaps(MapRequest request, JsonObject credentials, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("地图采集需要 Windows。");
        if (request.Width is < 240 or > 4096 || request.Height is < 240 or > 4096 ||
            !Path.IsPathFullyQualified(request.RunDirectory))
            throw new InvalidDataException("请提供绝对运行目录和 240–4096 像素的视口。");
        var work = Path.Combine(Path.GetFullPath(request.RunDirectory), "过程");
        var target = Path.Combine(work, "地图");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(work, "输入", "项目配置.json"), cancellationToken))!.AsObject();
        var project = config["project"]?.AsObject() ?? throw new InvalidDataException("缺少 project");
        var missing = new[] { "lng", "lat" }.Where(key => project[key] is null).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("缺少 project." + string.Join("/", missing) + "，请填写已核实的项目经纬度。");
        foreach (var coord in new[] { "lng", "lat" })
            if (project[coord] is JsonValue v && v.TryGetValue<string>(out var raw) &&
                double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                project[coord] = number;
        if (config["layers"] is JsonObject layers && (bool?)layers["bldg"]?["on"] == false) layers.Remove("bldg");
        var schema = await NJsonSchema.JsonSchema.FromJsonAsync(Source("site.config.schema.json"), cancellationToken);
        var errors = schema.Validate(config.ToJsonString());
        if (errors.Count != 0) throw new InvalidDataException("项目配置无效：" + string.Join(", ", errors.Take(8).Select(e => e.Path)));
        var pages = config["ppt"]?["pages"]?.AsObject() ?? throw new InvalidDataException("缺少 ppt.pages。");
        var selected = new List<int>();
        foreach (var (key, profile) in pages)
        {
            if (profile?["layers"] is JsonObject pageLayers) pageLayers.Remove("bldg");
            if ((string?)profile?["action"] == "preserve" || (string?)profile?["type"] is "纯模板" or "非场地分析" or "红线底图") continue;
            if (!int.TryParse(key, out var n) || n <= 0 || n.ToString() != key ||
                (string?)profile?["type"] is not (null or "卫星底图") ||
                profile?["crop"] is not JsonArray crop || crop.Count != 4 ||
                (int)crop[0]! < 0 || (int)crop[1]! < 0 || (int)crop[2]! <= (int)crop[0]! || (int)crop[3]! <= (int)crop[1]! ||
                (int)crop[2]! > request.Width || (int)crop[3]! > request.Height)
                throw new InvalidDataException($"页 {key} 的类型或裁切框无效。");
            selected.Add(n);
        }
        if (selected.Count == 0) throw new InvalidDataException("未配置卫星页。");
        selected.Sort();
        var specs = new JsonObject();
        foreach (var n in selected) specs[n.ToString()]=CaptureSpec.ForPage(config,n);
        const int captureVersion = 6;
        var receipt=new JsonObject { ["version"]=captureVersion, ["width"]=request.Width, ["height"]=request.Height, ["pages"]=specs };
        if (request.PhotosDirectory is string photos)
        {
            if (!Path.IsPathFullyQualified(photos) || !Directory.Exists(photos)) throw new InvalidDataException("照片目录无效。");
            var hashes=new JsonObject();
            for (int i=1;i<=4;i++) foreach (var ext in new[] {"jpg","jpeg","png","webp"})
            {
                var name=i+"."+ext; var path=Path.Combine(photos,name);
                if (File.Exists(path)) { hashes[name]=ArtifactReceipt.Digest(path); break; }
            }
            receipt["photos"]=hashes;
        }
        string Signature(int n) => new JsonArray(captureVersion,request.Width,request.Height,receipt["photos"]?.DeepClone(),specs[n.ToString()]!.DeepClone()).ToJsonString();
        MapResult Result() => new(target, selected.ToArray(), selected.SelectMany(n =>
            (JsonNode.Parse(File.ReadAllText(Path.Combine(target, $"P{n}.json")))?["captureWarnings"] as JsonArray ?? [])
            .Select(w => $"P{n} {(string?)w}")).ToArray());
        bool PageMatches(string dir,int n) => ArtifactReceipt.Matches(Path.Combine(dir,$"P{n}.json"),Signature(n)) &&
            ArtifactReceipt.Matches(Path.Combine(dir,$"P{n}-screen.jpg"),Signature(n));
        if (Directory.Exists(target))
        {
            if (selected.All(n=>PageMatches(target,n)) && File.Exists(Path.Combine(target,"site.config.json"))) return Result();
            throw new InvalidDataException("已有地图与采集条件或文件校验不符，请在新运行目录采集。");
        }
        var runtime = "window.AMAP_CONFIG=" + credentials.ToJsonString() + ";";
        var pending = Path.Combine(work, ".map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pending);
        try
        {
            var needed=new List<int>();
            string? prior=null;
            if (request.ReuseRunDirectory is string previous)
            {
                if (!Path.IsPathFullyQualified(previous)) throw new InvalidDataException("历史运行目录须为绝对路径。");
                prior=Path.Combine(previous,"过程","地图");
            }
            foreach (int n in selected)
            {
                if (prior==null || !PageMatches(prior,n)) { needed.Add(n); continue; }
                foreach (var name in new[] {$"P{n}.json",$"P{n}-screen.jpg"}) File.Copy(Path.Combine(prior,name),Path.Combine(pending,name));
            }
            if (needed.Count>0) await Render(config, runtime, needed, pending, request, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(pending, "site.config.json"), receipt.ToJsonString(), cancellationToken);
            foreach (int n in selected) foreach (var name in new[] {$"P{n}.json",$"P{n}-screen.jpg"})
                ArtifactReceipt.Save(Path.Combine(pending,name),Signature(n));
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(pending, target);
            return Result();
        }
        finally { if (Directory.Exists(pending)) Directory.Delete(pending, true); }
    }

    private static Stream Resource(string name) => typeof(Renderer).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("Missing map resource " + name);
    private static string Source(string name)
    {
        using var reader = new StreamReader(Resource(name));
        return reader.ReadToEnd();
    }

    private static async Task Render(JsonObject config, string runtime, List<int> pages, string output, MapRequest request, CancellationToken token)
    {
        var photos = new JsonObject();
        if (request.PhotosDirectory is string directory)
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
                throw new InvalidDataException("照片目录须为存在的绝对路径。");
            for (int i = 1; i <= 4; i++)
            {
                foreach (var extension in new[] { "jpg", "jpeg", "png", "webp" })
                {
                    var path = Path.Combine(directory, $"{i}.{extension}");
                    if (!File.Exists(path)) continue;
                    var data = await File.ReadAllBytesAsync(path, token);
                    photos[i.ToString()] = $"data:image/{(extension == "jpg" ? "jpeg" : extension)};base64,{Convert.ToBase64String(data)}";
                    break;
                }
            }
        }
        var html = Source("周边场地分析_模板.html").Replace("<!-- SITE_CONFIG -->", "<script>window.SITE_PHOTOS=" + photos.ToJsonString() + ";window.SITE_CONFIG=" + config.ToJsonString() + ";</script>")
            .Replace("<script src=\"site_builder.js\"></script>", "<script>" + Source("site_builder.js") + "</script>")
            .Replace("  window.__ovState = function () {", Source("ppt-adapter.js") + "\n  window.__ovState = function () {");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = request.Width, Height = request.Height }, DeviceScaleFactor = 1, ServiceWorkers = ServiceWorkerPolicy.Block });
        const string origin = "http://127.0.0.1";
        await context.RouteAsync(origin + "/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path is "/map" or "/amap.runtime.js")
                await route.FulfillAsync(new() { Body = path == "/map" ? html : runtime, ContentType = path == "/map" ? "text/html; charset=utf-8" : "application/javascript" });
            else await route.AbortAsync();
        });
        var page = await context.NewPageAsync();
        string stage = "加载地图";
        try
        {
        await page.GotoAsync(origin + "/map", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        try { await page.WaitForFunctionAsync("() => Boolean(window.__lastBuild || window.__buildErr)", null, new() { Timeout = 360000 }).WaitAsync(token); }
        catch (TimeoutException) { throw new InvalidDataException("等待地图图层构建超时。"); }
        if (await page.EvaluateAsync<bool>("() => !!window.__buildErr")) throw new InvalidDataException("地图构建失败，请检查凭据和网络。");
        stage = "等待道路和区域数据";
        // Only wait for upgrades requested by these pages. The initial build can already
        // contain fallback road geometry. Record which upgrades did not finish.
        var requested = pages.SelectMany(n => config["ppt"]!["pages"]![n.ToString()]!["enabled"] as JsonArray ?? [])
            .Select(v => (string?)v).Where(v => v is "road" or "express").Distinct().ToArray();
        var warnings = await page.EvaluateAsync<string[]>("""
            async layers => Promise.all(layers.map(async layer => {
                const pending = window.__lastBuild[layer === 'road' ? 'R' : 'E'];
                if (!pending) return '';
                let timer;
                try {
                    const result = await Promise.race([pending, new Promise((_, reject) => {
                        timer = setTimeout(() => reject('timeout'), 60000);
                    })]);
                    const items = layer === 'road' ? result?.lines : result;
                    return items?.length ? '' : layer + ' 升级数据为空';
                } catch (error) {
                    return layer + (error === 'timeout' ? ' 升级数据等待超时' : ' 升级数据请求失败');
                } finally { clearTimeout(timer); }
            }))
            """, requested).WaitAsync(token);
        foreach (int n in pages)
        {
            stage = $"P{n} 应用底图模式";
            await page.EvaluateAsync("n => {PPT_EXPORT.basemapOnly(false); PPT_EXPORT.selectPage(n); PPT_EXPORT.basemapOnly(true);}", n);
            await page.EvaluateAsync("() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done)))");
            stage = $"P{n} 加载卫星瓦片";
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 90000 }).WaitAsync(token);
            stage = $"P{n} 几何快照";
            await page.WaitForFunctionAsync("() => {try{return PPT_EXPORT.snapshot().mpp>0}catch{return false}}").WaitAsync(token);
            var snapshot = await page.EvaluateAsync<string>("() => {return JSON.stringify(PPT_EXPORT.snapshot())}");
            var geometry=JsonNode.Parse(snapshot)!.AsObject();
            var enabled = (config["ppt"]!["pages"]![n.ToString()]!["enabled"] as JsonArray ?? [])
                .Select(v => (string?)v).ToHashSet();
            geometry["captureWarnings"] = new JsonArray(warnings.Where(w => w.Length > 0 && enabled.Contains(w.Split(' ')[0]))
                .Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
            geometry["captureSpec"]=CaptureSpec.ForPage(config,n);
            stage = $"P{n} 检查底图影像";
            var imagePath = Path.Combine(output, $"P{n}-screen.jpg");
            for (int attempt = 1; ; attempt++)
            {
                var bytes = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 95 });
                bool missing = await ImageReview.HasBlankBlock(page, bytes);
                if (!missing)
                {
                    await File.WriteAllBytesAsync(imagePath, bytes, token);
                    break;
                }
                if (attempt == 4) throw new InvalidDataException($"P{n} 卫星影像持续出现大块纯色区域，未保存本次采集。");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
            }
            await File.WriteAllTextAsync(Path.Combine(output, $"P{n}.json"), geometry.ToJsonString(), token);
        }
        }
        catch (Exception error) when (error is not (InvalidDataException or OperationCanceledException))
        {
            throw new InvalidDataException($"{stage}失败（{error.GetType().Name}）。");
        }
    }
}



