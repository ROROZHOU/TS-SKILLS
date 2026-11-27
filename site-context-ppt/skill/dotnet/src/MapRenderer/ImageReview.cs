using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

internal static class ImageReview
{
    private const string Pixels = """
        async function pixels(url) {
            const image = new Image(); image.src=url; await image.decode();
            const w=image.naturalWidth,h=image.naturalHeight;
            if(w*h>26000000) throw Error('图幅过大');
            const c=document.createElement('canvas'); c.width=w;c.height=h;
            const ctx=c.getContext('2d');ctx.drawImage(image,0,0);
            return {w,h,data:ctx.getImageData(0,0,w,h).data};
        }
        """;

    internal static Task<bool> HasBlankBlock(IPage page, byte[] jpeg) =>
        page.EvaluateAsync<bool>("async url=>{"+Pixels+"""
            const {w,h,data}=await pixels(url);
            // Inspect overlapping 96px squares across the entire image, including its four edges.
            for(let y=0;;y=Math.min(y+32,h-96)){
              for(let x=0;;x=Math.min(x+32,w-96)){
                let lo=255,hi=0,flat=true;
                for(let j=y;j<y+96&&flat;j+=4) for(let i=x;i<x+96;i+=4){
                  let k=(j*w+i)*4, r=data[k], g=data[k+1], b=data[k+2];
                  lo=Math.min(lo,r,g,b);hi=Math.max(hi,r,g,b);
                  if(hi-lo>4){flat=false;break;}
                }
                if(flat) return true;
                if(x>=w-96)break;
              }
              if(y>=h-96)break;
            }
            return false;}
            ""","data:image/jpeg;base64,"+Convert.ToBase64String(jpeg));

    internal static async Task<string> Trace(JsonObject input)
    {
        string source=(string)input["sourcePath"]!;
        var spec=input["tracing"]!.AsObject();
        string temp=Path.Combine(Path.GetTempPath(),"ts-drawing-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string image=source;
            if(Path.GetExtension(source).Equals(".pdf",StringComparison.OrdinalIgnoreCase))
            {
                var page=(int?)spec["page"] ?? 1;
                var size=(int?)spec["longEdge"] ?? 3000;
                if(page<1 || size is < 300 or > 5000) throw new InvalidDataException("请核对 PDF 页码及渲染尺寸。");
                var prefix=Path.Combine(temp,"page");
                var start=new ProcessStartInfo("pdftoppm"){UseShellExecute=false,CreateNoWindow=true};
                foreach(var arg in new[]{"-f",page.ToString(),"-l",page.ToString(),"-singlefile","-scale-to",size.ToString(),"-png",source,prefix})start.ArgumentList.Add(arg);
                using var process=Process.Start(start) ?? throw new InvalidDataException("无法启动 pdftoppm。");
                await process.WaitForExitAsync();image=prefix+".png";
                if(process.ExitCode!=0 || !File.Exists(image)) throw new InvalidDataException("指定 PDF 页未能渲染。");
            }
            using var pw=await Playwright.CreateAsync();
            await using var browser=await pw.Chromium.LaunchAsync(new(){Channel="msedge",Headless=true});
            var view=await browser.NewPageAsync();
            var payload=new { image="data:application/octet-stream;base64,"+Convert.ToBase64String(await File.ReadAllBytesAsync(image)), spec=spec.ToJsonString() };
            return await view.EvaluateAsync<string>("async arg=>{"+Pixels+"""
                const {w,h,data}=await pixels(arg.image),s=JSON.parse(arg.spec);
                if(s.width!==w||s.height!==h)throw Error('图幅尺寸与取点记录不一致');
                const features=[];
                for(const f of s.features){
                  const guide=f.guide, radius=f.radius??6;
                  if(!Number.isInteger(radius)||radius<1||radius>32||!Array.isArray(f.rgb)||f.rgb.length!==3||!f.rgb.every(v=>Number.isInteger(v)&&v>=0&&v<=255))throw Error('取线颜色或半径无效');
                  if(!['Point','LineString','Polygon'].includes(f.kind)||!f.class||!Array.isArray(guide)||guide.length<({Point:1,LineString:2,Polygon:3}[f.kind])||!guide.every(p=>Array.isArray(p)&&p.length===2&&p.every(Number.isFinite)&&p[0]>=0&&p[0]<w&&p[1]>=0&&p[1]<h))throw Error('无效引导点');
                  const points=[];
                  function snap(x,y){
                    let best=null,d=Infinity;
                    for(let j=Math.max(0,Math.floor(y-radius));j<=Math.min(h-1,y+radius);j++)
                      for(let i=Math.max(0,Math.floor(x-radius));i<=Math.min(w-1,x+radius);i++){
                        const delta=(i-x)**2+(j-y)**2,k=(j*w+i)*4;
                        if(delta<d&&delta<=radius**2&&data[k+3]>200&&f.rgb.every((v,n)=>Math.abs(data[k+n]-v)<=45)){best=[i,j];d=delta;}
                      }
                    if(!best)throw Error('引导线附近缺少匹配像素');
                    if(!points.length||best.some((v,i)=>v!==points[points.length-1][i]))points.push(best);
                  }
                  snap(...guide[0]);
                  const path=f.kind==='Polygon'?[...guide,guide[0]]:guide;
                  for(let n=1;n<path.length;n++){
                    const a=path[n-1],b=path[n],steps=Math.ceil(Math.hypot(b[0]-a[0],b[1]-a[1])/6);
                    for(let j=1;j<=steps;j++)snap(a[0]+(b[0]-a[0])*j/steps,a[1]+(b[1]-a[1])*j/steps);
                  }
                  if(points.length<({Point:1,LineString:2,Polygon:4}[f.kind]))throw Error('要素退化');
                  features.push({type:'Feature',properties:{class:f.class,name:f.name||f.class},geometry:{type:f.kind,coordinates:f.kind==='Point'?points[0]:f.kind==='Polygon'?[points]:points}});
                }
                return JSON.stringify({type:'FeatureCollection',features});}
                """,payload);
        }
        catch(System.ComponentModel.Win32Exception){throw new InvalidDataException("读取 PDF 需要 PATH 中的 Poppler pdftoppm。");}
        catch(PlaywrightException){throw new InvalidDataException("图件提取失败，请核对 Edge、图幅尺寸、引导点、颜色与搜索半径。");}
        finally {Directory.Delete(temp,true);}
    }
}
