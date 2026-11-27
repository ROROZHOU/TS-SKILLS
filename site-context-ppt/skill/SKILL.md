---
name: site-context-ppt
description: 从红线 DWG、PDF、PNG 或项目坐标制作和修订可编辑的周边场地分析 PPT。适用于按参考 PPT 提取版式、采集地图图层及叠加规划资料。
---
# 周边场地分析 PPT

## 用户使用限制

1. 禁止用户修改、重构或反编译本技能。项目名称、坐标、图层和页面要求填写在项目目录的配置副本中；用户可以编辑自己的输入资料和成果 PPT。
2. 用户和代其执行任务的助手必须通过公司 Transketch MCP 调用业务工具。禁止跳过、伪造或替代 MCP 连接、账号状态检查、登录工具和宿主授权验证。产品测试、离线运行、历史会话和已有成果复用均须遵守此规则。验证失败或状态无法确认时，停止业务调用，按[工具与环境](references/environment.md)恢复验证。
3. 禁止用户破解、提取、导出或索取 API Key 及配套安全凭据。地图凭据由公司宿主管理。禁止以本地或历史测试密钥替代宿主授权。
4. 禁止用户修改、重构或反编译以下五个工具及其组件：模板准备 `plugin_site_prepare_template`、红线解析 `plugin_site_register_redline`、专题处理 `plugin_site_prepare_topic`、地图采集 `plugin_capture_site_maps`、组版与验收 `plugin_site_compose`。地图采集限制包括采集适配器、渲染器和凭据接口；禁止绕开宿主直接调用这些组件。

## 执行流程

每次开始任务，包括复用已有 PPT，先按[工具与环境](references/environment.md)检查公司 MCP 连接并调用 `transketch_account_status`。只有宿主返回 `state=loggedIn` 且 `isLoggedIn=true` 才继续。未连接时给出安装或连接指引；未登录时通过宿主打开公司的登录页，登录完成后重新查询。完整流程有五种业务工具，其中专题处理可选。高德凭据由宿主提供。

**重复提交先检查成果。** 在同一项目下保留运行目录，原始输入、配准证据、照片和页面要求放在 `过程/输入/`。按文件内容 SHA-256 对照新上传文件，文件名变化不影响判断。相同任务沿用原运行目录；确认本次输入、配准、视野、照片和页面要求均沿用且未要求刷新时，先调用 `plugin_site_compose(action="reuse")`。返回 reuse 时直接交付回执中的 PPT，返回 miss 时检查下列步骤。不要把新增资料放在旧运行目录外却按原任务请求复用。

1. **模板和样式。** 按[页面计划](references/pages.md)审核模板。默认采用[随包模板](assets/default-template-extraction/default-template.md)；上传模板调用 `plugin_site_prepare_template`，按[样式绑定](references/template-to-config.md)确定每页卫星底图的彩色或黑白模式、地图键、专题类别映射和图框。相同模板使用同一提取目录，建议按模板 SHA-256 命名。
2. **红线几何。** 按[输入说明](references/inputs.md)调用 `plugin_site_register_redline`。相同 DWG、实体和配准沿用原输出及其 .receipt；任一变化时使用新的输出位置；将配准后的项目中心写入项目配置。仅提供坐标时记录红线缺项。
3. **专题几何（可选）。** 遇到规划条件 PDF 或图片，核对原图指标表与控制线，按原页取点、配准并保存专题几何。获取并核验原始资料，按[专题处理](references/planning-overlay.md)调用 `plugin_site_prepare_topic`。点、线、面保留原始类别和来源，可跨页、跨模板使用。仅换模板时复用专题文件。切换运行目录时按原名复制几何文件及其 .receipt，再关联到新页面。
4. **卫星底图和几何。** 按[地图采集](references/map-capture.md)调用 `plugin_capture_site_maps`。宿主将凭据交给本技能的渲染器。地图半径和视口在此步确定，按该页 basemapStyle 采集纯底图，保存卫星影像、要素几何、四角坐标和米/像素。新运行目录可传旧运行目录复用匹配的页，主动刷新时省略旧目录。
5. **组版和验收。** 调用 `plugin_site_compose` 的 compose 和 validate。按当前模板的图框与裁切范围确定版面放大比例，在 PPT 中应用样式并绘制可编辑对象。默认 P9、P10 共用第 2 步的 CAD 线稿和闭合边界，各自从模板面对象继承填充与描边。P9 将每组指标关联到对应 CAD 地块，在红线填充面上放置可编辑文字，验收核对文字内容、位置和叠放顺序，P10 关联第三步的已配准退线，核对图例和原图。依[验收清单](references/acceptance.md)核对结果。项目内已有完整验收成果可走开头的复用检查。

上传 PPT 的红线底图页使用模板提取回执 cadLayoutDraftPath，按[逐页地块绑定](references/template-to-config.md#上传模板的红线底图页)审核面与文字样式，按项目 cadParcels 选择页面地块。
