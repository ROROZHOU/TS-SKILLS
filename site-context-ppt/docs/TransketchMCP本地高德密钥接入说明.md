# Transketch MCP 开发交接：为本地地图工具提供高德凭据

## 发布时填写

| 面向用户的安装信息 | 由发布负责人填写 |
|---|---|
| 公司 Transketch MCP 安装包地址（下载链接或共享路径） | |

发布时将该地址同步到用户技能包的 `references/environment.md` 中“公司 MCP 安装地址”一行。留空时，技能引导用户向管理员获取安装包。此处填写供用户取得安装包的位置；公司网站登录页由宿主配置。

## 登录检查与责任分工

插件参数附件《插件开发指南》第 6 节将业务工具鉴权交给主程序。宿主《代码架构》第 5 章规定，`transketch_account_status` 查询当前 MCP 会话的公司账号状态，`transketch_account_login` 打开公司登录页。状态查询通过宿主的账号服务校验令牌。每次业务调用还会经过宿主统一拦截，该拦截使用本地登录状态。

用户技能在每次任务开始时先查询状态，已有成果复用也走此入口。宿主应对全部五种业务工具沿用统一鉴权，包括 `plugin_site_compose` 的 reuse 动作。上线联调覆盖仅安装技能、宿主已连接但插件缺失、未登录、登录进行中、校验失败、已登录及会话失效。确认未登录时直接调用业务工具返回 `isError`，已登录时才能执行。技能参数和对话均无需接收公司账号密码或令牌。

## 接入位置

目标平台为 Windows。宿主负责录入、加密和轮换凭据；插件新增工具 `plugin_capture_site_maps`。工具在本机启动 C# 渲染器，用 Edge 读取地图。红线、项目配置、卫星图片和 PPT 均可留在本机。高德与其他地图数据源仍需要网络。

| 环节 | 约定 |
| --- | --- |
| 保存位置 | `%LOCALAPPDATA%\TransketchMcp\amap.dpapi`，按运行宿主的 Windows 用户保存。 |
| 加密内容 | UTF-8 JSON 对象，含同一应用的 `key`（Web 端 JS API Key）和 `securityJsCode`（配套安全密钥）。 |
| 密钥传给谁 | 宿主实现 `PluginMcpTools.ReadAmapCredentials` 回调。插件每次采集调用一次，通过子进程重定向的标准输入传给随技能安装的渲染器。 |
| skill 可见内容 | 工具参数为项目运行目录、视口宽高、可选照片目录；结果含 `directory`、`pages` 和 `warnings`，最后一项列出受影响页及采集告警。模型和 skill 配置均无需接触高德凭据。 |

## 1. 在宿主设置界面保存凭据

增加两个遮罩输入框，分别收集 Web 端 Key 和安全密钥。下面代码放在宿主 Windows 设置模块；插件包保持附件约定的单一 MCP SDK 依赖。宿主若尚未引用 ProtectedData，在宿主 Windows 项目中引用与目标框架匹配的 `System.Security.Cryptography.ProtectedData` 包。

```csharp
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text;
using TransketchMcpPlugin;

static string VaultPath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "TransketchMcp", "amap.dpapi");

static void SaveAmap(string key, string securityJsCode)
{
    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(securityJsCode))
        throw new ArgumentException("请填写两项凭据。");
    var value = new JsonObject { ["key"] = key.Trim(), ["securityJsCode"] = securityJsCode.Trim() };
    var bytes = Encoding.UTF8.GetBytes(value.ToJsonString());
    var file = VaultPath();
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    var pending = file + "." + Guid.NewGuid().ToString("N");
    try
    {
        File.WriteAllBytes(pending, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        File.Move(pending, file, overwrite: true);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(bytes);
        File.Delete(pending);
    }
}
```

## 2. 注册凭据读取器和工具

在 MCP 宿主启动时设置回调，然后按附件现有方式注册工具类。每次调用都读取当前密文，轮换后下一次采集即使用新值。

```csharp
PluginMcpTools.ReadAmapCredentials = async cancellationToken =>
{
    var cipher = await File.ReadAllBytesAsync(VaultPath(), cancellationToken);
    var clear = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
    try
    {
        var value = JsonNode.Parse(clear)!.AsObject();
        return new AmapCredentials((string)value["key"]!, (string)value["securityJsCode"]!);
    }
    finally { CryptographicOperations.ZeroMemory(clear); }
};

// 在现有 MCP builder 链上保留一次注册
// .WithTools<PluginMcpTools>(PluginToolOptions.Options)
```

工具把单次请求写入匿名管道；渲染器在隔离的浏览器上下文中拦截本次页面的 `/amap.runtime.js` 请求，在内存中提供 `window.AMAP_CONFIG`。此处的本地 URL 由浏览器路由处理，无需启动 HTTP 监听。凭据不进入命令行、工具 schema、生成文件或返回值。避免开启记录完整请求 URL、管道内容或浏览器网络流量的日志。

## 3. 构建和部署

开发机运行 `dotnet pack skill/dotnet/src/TransketchMcpPlugin -c Release -o dist/packages`，宿主引用与技能配套的插件包。发布 skill 时，用 `dotnet publish skill/dotnet/src/MapRenderer -c Release -o skill/runtime/SiteMapRenderer` 更新完整 .NET 8 Windows 渲染器及 Playwright 驱动。用户安装完整 skill 包和 Edge。插件由宿主通过 stdio 提供，渲染器随 skill 分发。

注册工具之前将 `PluginMcpTools.InstalledSkillDirectory` 设为安装程序确认的 skill 根目录绝对路径，或设置**宿主进程**环境变量 `TRANSKETCH_SITE_CONTEXT_SKILL`。例如根目录为 `D:/CompanySkills/site-context-ppt` 时，第 4 步启动 `D:/CompanySkills/site-context-ppt/runtime/SiteMapRenderer/MapRenderer.exe`。第 2 步复用该程序的 DWG 模式，不读取高德凭据。模型和项目参数不可指定可执行文件。运行时目录应由可信安装程序部署且限制替换权限：将凭据传给用户可替换的程序会暴露凭据。本版本身不实施发布者签名验证。

第 3 步先由宿主的检索和下载工具取得原始资料；C# 工具 `plugin_site_prepare_topic` 读取已获取的数据，完成配准和格式统一，保留原始类别。第 5 步按目标页 topics 和 layerStyles 绘制专题。

## 4. 联调验收

- 确认宿主列出 `plugin_capture_site_maps`，工具 schema 中没有密钥字段。未设置回调、密文不存在或读错 Windows 账号时，应返回明确的配置错误。
- 用开发专用凭据和有经纬度的项目配置调用工具，检查卫星页的 JPG、同视口 JSON、署名和几何对齐。用另一项目目录验证密钥轮换。
- 确认项目目录和宿主日志中无测试密钥。DPAPI 提供当前用户的落盘保护；具有该用户权限的进程仍能解密或观察浏览器运行时凭据。向第三方分发时，使用可独立轮换和停用的凭据。

开发部署：直接输入 PDF 的可选功能需要本机可执行 pdftoppm（Poppler），加入 MCP 宿主进程的 PATH。PNG/JPEG 使用已有的 Edge。第三步工具名称不变。

准备模板回执中的 cadLayoutDraftPath，指向待审核的红线页样式草稿。
