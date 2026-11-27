# 发布验证

将技能 ZIP 解压到技能父目录，定位 `site-context-ppt/SKILL.md`。发布验证从解压副本的维护入口开始。示例中的路径均为本机绝对路径。

```powershell
$skillRoot = (Resolve-Path ./site-context-ppt).Path
dotnet build "$skillRoot/dotnet/tools/SiteCheck/SiteCheck.csproj" -c Release
$check = "$skillRoot/dotnet/tools/SiteCheck/bin/Release/net8.0/SiteCheck.dll"
dotnet $check $skillRoot template $templatePath $extractionDir
dotnet $check $skillRoot template $templatePath $extractionDir $bindingsPath $seedConfigPath $configPath
dotnet $check $skillRoot redline $dwgPath
dotnet $check $skillRoot redline $dwgPath $handle $geometryPath
dotnet $check $skillRoot topic $runDir $sourcePath $evidencePath $crs $dataset
$credentialJson | dotnet $check $skillRoot capture $runDir
dotnet $check $skillRoot compose $runDir $profilePath
dotnet $check $skillRoot validate $runDir $profilePath
```

维护机使用 .NET 8 SDK。测试凭据在进程内读取，以单行 JSON 经标准输入交给 capture。日常使用走宿主鉴权及凭据回调。按页面计划审核绑定、项目中心、专题证据、页面内容与 profile，然后执行对应步骤。将新运行目录与测试入口分开放置，保存解压包及渲染器 DLL 的 SHA-256、地图回执、验收结果和逐页预览。validate 对生成来源或资料完整性未通过返回 1，具体原因见 JSON。

按页检查 CAD 线稿对象数与原生编辑体验，保留页另核对依赖数。使用本次 compose 输出的文件、文件哈希与对应验收回执进行复核。
