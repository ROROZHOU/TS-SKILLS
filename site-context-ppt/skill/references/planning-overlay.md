# 规划及专题叠加

由[额外资源分支](pages.md#额外资源)进入本流程；互联网检索须已获得该资源范围的授权，用户提供的图件可直接核验。资源事实和几何取自当前项目对应资料，轮廓/填色/文字的视觉样式取自参考PPT，建立两条独立来源链。

## 搜索与适用性

先确定项目所在市、区/县、街道/乡镇。搜索当地自然资源和规划局、政府公示及其他可核实原始发布站点中的国土空间总体规划、详细规划、控制性详细规划和土地/建设用地规划图。

检索组合为“当前行政区名 + 国土空间规划 + 用地规划图/土地利用规划图/用途分区”，需要街坊粒度时继续查“当前街道/乡镇名 + 详细规划/控规 + 用地布局图/批后公布”。记录真实检索过程，不把这些词当成固定网站地址。

核对图件是否覆盖基地及所需周边范围、比例尺能否支撑当前图幅、是否草案/批后、规划期限和分类体系。市县总规只有粗分区时不能据此绘制地块级地类，需继续查街道/乡镇详规。规划条件附图只能支持其实际覆盖范围，不能替代周边完整规划用地图。

## 解析、配准和矢量化

1. 保存原文件、页码/图片区域、图例、发布信息、下载时间与校验值。PDF矢量优先提取路径和文本；扫描PDF/图片先识别文字与图例，再依颜色区域提取轮廓并人工核对。不可仅按颜色跨图件猜类别。
2. 用已知坐标、网格或稳定交叉点配准，记录至少足够确定变换的控制点、变换模型、目标坐标系、残差和人工检查。地理坐标的原始矢量记录原CRS及转换过程。未通过校核的几何不得标verified。
3. 按用途类别输出Polygon/MultiPolygon，保留孔洞、飞地及相邻边界。道路/控制线输出LineString，文字单独保留锚点和类别关系。简化不能改变拓扑或吞并小地块，误差要小于目标图幅可辨识尺度。
4. 记录原始类别和几何类型。组版时按目标页映射地类和样式。
5. 获取原件时优先用发布方提供的矢量下载或要素服务。将实际下载文件及发布页保存到本项目，记录来源、标题、版本、覆盖范围与原件哈希。同一数据集可以同时包含点、线、面。
6. 原生输入支持 GeoJSON 的 Point/MultiPoint、LineString/MultiLineString、Polygon/MultiPolygon（含孔洞）；KML 2.2 的 Placemark 或 MultiGeometry（类别来自 ExtendedData）；UTF-8 CSV 点表含 lng、lat 和类别列。Shapefile、地理数据库等在保留 CRS 和类别属性后导出 GeoJSON。PDF/SVG/扫描图先从所选页提取或描摹点锚、线中心或闭合面环，形成 GeoJSON 形状结构；用图例、比例和实物位置复核。轮廓提取及地类判读有歧义时须标记待核，不能提交为 verified。拍照透视、拼接或局部变形严重时须先纠正或分区配准。

## 第 3 步工具调用

传入运行目录和已落盘的源文件，例如：

```json
{"runDirectory":"D:/项目/场地分析01","sourcePath":"D:/项目/场地分析01/过程/输入/控规.geojson","evidencePath":"D:/项目/场地分析01/过程/输入/控规来源.json","crs":"GCJ02","dataset":"控规用地","classField":"landCode"}
```

证据文件含 status、source_url、title、registration。status 填 verified 前须完成核实。原始类别和来源随数据集保存。地理坐标接受 WGS84、GCJ02；KML 使用 WGS84。源文件采用原图纸或像素坐标时传 DRAWING；registration.fit 需要三对非共线控制点，registration.check 至少一对独立检核点，每对形如 `{"drawing":[x,y],"map":[GCJ02经度,GCJ02纬度]}`，registration.maxErrorM 为允许误差（米）。保留原图文件及其页码、图例、原图哈希与检核证据。

成功产物为 `过程/专题/<dataset>.json`，内容、配准、坐标系及类别字段相同时校验后复用。第 5 步按 `ppt.pages.<页>.topics` 选择数据集。例如 `{"控规用地":{"R2":"residential"}}` 将此页 R2 映射到 layerStyles.residential。空映射按原类别键匹配。组版检查几何类型和模板样式来源，再用底图四角将 GCJ02 投影到 PPT。旧版按页专题仅与原模板配套使用。同一专题已随旧快照采集时，从两条输入中选择一条以避免重复绘制。

## 缺项

找不到适用图件、缺图例、只有粗尺度总规或配准误差过大时，记录检索与核查结果。允许保留已核实文字及底图定位，同时将规划图层标为待补。不得拿CAD代征绿地/道路填色、其他区域规划或专项图冒充当前周边国土空间用地规划。

## 从规划条件图取线

PNG、JPEG 或 PDF 可直接作为第三步 sourcePath。核对图例和指标表，区分用地范围线、道路红线和建筑后退控制线。先查看原图，在目标要素上按走向标记引导点，转角、曲线及容易混淆的位置加密。工具沿引导线每约 6 像素采样，在指定半径内搜索实图上与 RGB 匹配的像素。找不到时停止，调整依据后重试。黑白线密集、相交线或虚线需要逐段核对；单个面仅提取一条外环，复杂孔洞用核实后的 GeoJSON 输入。

证据 JSON 保留 title、status=verified、source_url、registration 和 tracing。tracing 的 width、height 是实际取点图像尺寸，原点在左上，x 向右、y 向下。PDF 额外提供从 1 开始的 page 和 longEdge（长边像素，默认 3000）；按同一页、同一尺寸取点。tracing.features 是要素数组，每项包含 class、name、kind（Point、LineString 或 Polygon）、rgb（三个 0–255 整数）、radius（1–32 像素）及 guide（按顺序排列的像素点对）。闭合线应在 guide 末尾重复起点。

用于 CAD 页时，crs 传 DRAWING_CAD。registration.cadSha256 填目标 DWG 内容哈希，fit 含三对非共线的 drawing/cad 坐标，check 至少一对独立同名点，maxErrorM 为项目容许误差。目标 CAD 坐标需以米计。工具在 过程/CAD专题/ 生成已配准的点、线、面和最大检核残差。用于卫星页时，传 DRAWING，在对应位置使用 map（GCJ02）并沿用卫星页专题流程。

CAD 页的 页面内容.json 以 cadTopics 数组列出要出图的数据集名。profile.template.cadPageLayout.<页>.topicStyles 按类别绑定模板对象 styleId 和需要显示的 legendIds。第 5 步按当前 DWG 的变换绘制原生几何。规划指标逐项填写 indicators 对象（指标名称对应字符串值），body 填简短来源；按原图对照指标数量、单位、退线位置和图例。来源缺失或配准失败写入对应页 remaining_gaps。

P9 单地块仍使用 indicators 对象，可用 parcelName 填写核实后的地块名。多地块使用数组，例如 `"indicators":[{"handle":"A1","name":"地块一","values":{"容积率":"1.4"}},{"handle":"A2","name":"地块二","values":{"容积率":"2.0"}}]`。每个 handle 来自同一 DWG 的已选红线或 cadLinework 中经核实的闭合地块。profile 的 indicatorTextStyleId 引用同页模板中的指标文本对象，第一段用于地块名、第二段用于指标内容。组版按本项目地块几何定位，图框容纳不了时扩大图框或分地块制页。
