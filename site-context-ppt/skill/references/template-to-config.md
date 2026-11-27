# 模板到配置

用户上传模板先通过[适用性检查](pages.md#用户上传模板的适用性检查)，未通过时停止本流程。通过后，新模板运行 `plugin_site_prepare_template` ，结合原始OOXML、渲染及[逐页语义清单](pages.md)审核。无新模板使用默认模板入口提供的提取成果，先核对哈希。不同模板不能共用对象ID映射。

纯模板与非场地分析页按preserve原样保留，不生成其site.config页面图层或替换样式。以下配置转换仅适用于需要生成或修改的分析页。

已有图层和额外资源都按面、线、文字拆分style_refs，记录源页/对象ID以及面和文字是否属于不同组合对象。工具 `plugin_site_prepare_template` 根据已审核的style-bindings.json和resolved-styles.json，将提取值转换成逐页site.config。它不负责自动猜图层语义或配准外部地图。

每个绑定包含page、target、layer、style_refs、css_to_ppt_scale。target为layer、parcel或resource_style。style_refs中的fill、stroke、text、background分别指向模板对象，可跨页引用有依据的同类图例。缺项保留unresolved并补查母版/布局，不启用固定配色。仅当前任务明确提出新的样式覆盖时才记录覆盖来源。

地图图层样式字段为 fill.color/opacity，stroke.color/opacity/width/dash，label.show/fontFamily/size/color/opacity/box/align/bold/italic，以及点尺寸和道路分级variants。原始PPT样式、主题变换和未支持属性保留在_template中。标签底色独立提取，不能把地类填色当文字底色。

地图渲染尺寸以 CSS 像素记录，PPT 线宽和字号以磅（pt）记录。换算公式为：PPT 值（pt）= 渲染值（CSS px）× 裁切放大比例 × 0.75。比例来自实际图幅，不能使用其他图幅的缩放比例。单位换算后，图例沿用相同style对象。

配置包含ppt.pages.<页>.radius、crop、enabled、layers、parcelOutlineStyle、resourceFeatures、layerStyles。来源、配准和类别通过resourceFeatures.evidence关联。任何卫星页地理图层都需要地图采集器支持该图层的渲染和几何快照导出，缺少能力时记录缺项，并交给开发团队评估。

新模板需要人工复核继承、分组缩放、渐变、端点及自定义线型等复杂属性。提取器只解析其支持的属性，原始XML是复核依据。输出样式标unsupported或unresolved时不能宣称完整还原。

在项目运行目录指定提取输出目录，审核对象引用后填写 style-bindings.json，已有审核绑定时一次传入 templatePath、outputDir、bindingsPath、configPath、outputPath；新模板先省略后三项，审核后再调用同一工具复用提取结果。

地图绑定采用导出键，例如水/水域/水系对应 water，道路对应 road，地铁站对应 metro。无法对应的图层须审核，专题分类使用 resource_style。第 1 步确定样式依据，第 5 步绘制。

逐页审核卫星底图时，在 style-bindings.json 中设置 `"basemapStyles": {"5": "color", "8": "grayscale"}`。第 1 步将结果写入 `ppt.pages.<页码>.basemapStyle`。color 保留原始卫星色彩，grayscale 使用黑白。未绑定的页沿用项目的逐页选择，再回退至 basemap.gray。随包模板的卫星页默认黑白；这两个模式不模拟 PPT 双色调滤镜。

## 上传模板的红线底图页

1. 打开第一步回执 cadLayoutDraftPath 的草稿，对照上传 PPT 核对页码、地块面和图框。草稿将组内对象换算到页面坐标，尝试把指标文字框的中心匹配到唯一包含它的地块面。图例、背景和其他闭合面也可能成为候选。多框文字、曲线、重叠面和纯图片页需要复核。imagePagesForReview 列出未从文字判定为红线页的含图页面。纯图片缺少独立样式对象时记录缺项，或补充已核实的可编辑样式。
2. 草稿另存至项目 profile 同目录，例如 `红线页绑定.json`。在 cadPageLayout 中保留实际红线页和所需的 parcelStyles，核实 frame（每英寸 96 像素）和 keepIds（保留的顶层标题、图例等）。每个样式项包含该页的 parcelStyleId，有指标时还需要 indicatorTextStyleId；专题控制线沿用 topicStyles。设置 reviewed=true，在 profile.template 中指定 `"cadLayoutFile":"红线页绑定.json"`。组版核对绑定文件与 PPT 哈希。其他页面仍按该 PPT 的页面计划配置。
3. 在页面内容.json 顶层填写 cadParcels，每项有项目地块 key 和已核实的同一 DWG 闭合实体 handle。每个红线页默认绘制整份清单，子集用页内 parcelKeys 指定。一页有多套模板样式时，用该页 parcelSlots 绑定项目 key 和样式名。只有一套时可共用。指标数组仅填写实际有指标的地块，退线页可单独关联 cadTopics。

示例（将页码、样式名和实体编号替换为实际值）：

```json
{
  "cadParcels": [{"key":"东区","handle":"A1"},{"key":"西区","handle":"B2"}],
  "6": {
    "parcelSlots":{"东区":"shape-45","西区":"shape-62"},
    "indicators":[{"handle":"A1","name":"东区","values":{"容积率":"1.4"}}]
  },
  "8": {
    "parcelSlots":{"东区":"shape-13","西区":"shape-21"},
    "cadTopics":["建筑退线"]
  }
}
```

上例两页均绘制两块用地，只有第 6 页的东区绘制指标。只分析西区时，在该页填写 `"parcelKeys":["西区"]` 并仅保留西区的 parcelSlots。旧版内嵌 profile 可以继续使用原参数。
