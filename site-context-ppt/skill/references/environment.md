# 本机工具与环境

公司 MCP 安装地址：

每次任务先检查客户端可用的 Transketch MCP 工具，再按下表处理。此检查也适用于仅复用已有成果的请求。

| 情况 | 技能的处理 |
|---|---|
| 未发现公司 MCP 工具 | 提示“此技能需要公司 Transketch MCP。请先安装并在当前客户端连接；已安装的用户请检查连接。”上方安装地址已填写时一并显示，留空时补充“请向公司管理员获取安装包”。等待连接后重查。 |
| 能看到公司工具，但缺少 `transketch_account_status` 或所需业务工具 | 指明缺少的工具，请管理员检查宿主版本、插件注册和客户端连接。 |
| 状态工具可用 | 调用 `transketch_account_status`。仅在 `state=loggedIn` 且 `isLoggedIn=true` 时进入业务流程。 |
| `loggedOut` | 调用 `transketch_account_login` 打开公司登录页；由用户在浏览器完成登录，再调用状态工具确认。若登录工具缺失，报告宿主配置问题。 |
| `loggingIn` | 提示完成已打开的登录流程，随后复查状态，避免重复打开登录页。 |
| `unknown`、连接失败或结果无法识别 | 提示无法验证公司账号，检查网络或 MCP 连接后重查。业务调用中若收到宿主鉴权错误，回到状态检查。 |

工具参数里的文件和目录都使用本机绝对路径；模板、快照及底图通过文件传递。

| 工具 | 输入 | 产物 |
|---|---|---|
| `plugin_site_register_redline` | dwgPath、可选 layer、handle、outputPath、registrationPath | 候选红线、原始边界或已配准边界及项目中心 |
| `plugin_site_prepare_template` | templatePath、outputDir、可选 force；编译时同时提供 bindingsPath、configPath、outputPath | 提取模板、样式并按审核绑定生成配置 |
| `plugin_site_prepare_topic` | runDirectory、sourcePath、evidencePath、crs、dataset、可选 classField | 生成供多页和多模板复用的点、线、面文件 |
| `plugin_capture_site_maps` | request 对象，见地图采集流程 | 各卫星页底图和几何快照 |
| `plugin_site_compose` | action、runDir、profilePath、可选 baselinePptxPath、pages | reuse 查询已验收交付，compose 组版，validate 验收 |

将用户安装包解压到客户端识别的技能父目录，保留顶层 `site-context-ppt/` 和其下的 `runtime/SiteMapRenderer/`。地图渲染器随技能一同解压。公司管理员在宿主侧部署插件、.NET 运行时和 Windows Edge，登记管理员控制的 `site-context-ppt` 目录并配置凭据读取器。客户端与宿主分机时，宿主机器也需部署同版技能。连接公司 MCP 后按上表验证登录状态。

需要直接提取 PDF 页时，由管理员配置 Poppler 并确保 MCP 进程可调用 pdftoppm。已导出的 PNG/JPEG 可交给对应业务工具处理。
在 Windows x64 终端运行 `dotnet --list-runtimes`，检查 Microsoft.NETCore.App 8.0 或更高版本。渲染器以 .NET 8 为目标，允许使用已安装的较高主版本。源代码构建使用 .NET 8 SDK。
