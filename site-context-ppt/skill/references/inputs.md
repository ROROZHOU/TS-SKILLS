# 输入和红线解析

## 先检查项目资料

- 未提供红线且未提供坐标：先询问“项目叫什么、位于哪里？请提供红线图，或坐标及坐标系；暂时没有时可先提供具体地址或地块位置。”在取得可靠定位前，暂停地图获取与场地内容生成；可继续分析模板。
- 仅有地址或项目名称：先核实位置；同名或定位不唯一时请用户确认。不能把猜测位置当作已确认坐标。
- 仅有坐标：核实坐标系和来源，可制作有依据的周边分析；边界、面积、尺寸、退界等标为缺少红线资料，不捏造。
- 已有红线：先尝试从图纸解析坐标与说明，确有缺项再询问；不重复索取已有信息。未上传参考PPT时采用默认模板。

## 红线解析与验证

- DWG/DXF：记录源文件hash、解析器/版本、单位、图层、实体handle、坐标、闭合状态与bulge。保留原始实体和可复核中间数据。
- PDF：先分辨矢量/扫描，提取线条或渲染识别文字和控制点。PNG/扫描件通过尺度或坐标控制点标定；未标定不得输出精确面积、坐标和退距。
- 查找图纸坐标说明、坐标格网、项目资料及规划条件。坐标系不得只按数字范围猜测。CAD地方坐标转换至WGS84/GCJ02必须保存参数和独立验证误差；中心点匹配不构成测绘验证。
- 建立 project.json：name/address、coordinate(value/crs/source/accuracy)、cad_units、planning_metrics(值/单位/来源页/版本)、entities(role/handle/layer/geometry/crs/bulge)、validation、missing。
- 验证闭合、自交、单位、面积、顶点顺序、曲线及选中地块；不能将同图其他地块的退线套入基地。
- 单个定位点不能配准整张红线图。保留未知字段并报告缺少的坐标或比例依据。
- site.config的project.coord只接受地图采集器支持的 WGS84 或 GCJ02；地方坐标先完成有依据的转换，不能直接填入lng/lat。

## DWG 红线工具

1. 调用 `plugin_site_register_redline`，传绝对路径 dwgPath，可附 layer。工具仅列出模型空间内闭合的轻量多段线。按图纸核实地块及 handle；同图层可有多个候选。块内实体、其他实体类型或非水平法向量需先在 CAD 中核实并转换。
2. 传入选定的 handle 和尚不存在的 outputPath，可输出原始 CAD 顶点、bulge、图层、单位和文件哈希。该文件用于核对原图，尚无地图定位。
3. 需要底图叠加时，另传 registrationPath 指向 UTF-8 JSON。字段 crs 固定为 GCJ02，source 填实测或配准证据，maxErrorM 为项目允许的最大误差（米）。DWG 红线采用相似变换，fit 至少包含两对不重合的控制点，check 至少包含一对独立检查点。每对均为 `{"cad":[图纸X,图纸Y],"map":[GCJ02经度,GCJ02纬度]}`。在地块尺度用二维相似变换拟合平移、旋转、统一比例，检核残差超限时停止。弧段保留原始 bulge，叠加折线按 0.02 米弦高离散。核对控制点分布、闭合边界及自交，面积为地图配准估算值。
4. 配准成功的 outputPath 使用 `过程/输入/redline-outline.json`。将回执 details.project 的 lng、lat、coord 合并到项目配置，再采集地图；已核验的近距页启用 redline。只有原始 CAD 坐标或单个定位点时，保留未配准状态。

默认模板 P9、P10 使用第 2 步同一份 DWG 提取结果。未配准时将原始结果保存为 `过程/输入/cad-geometry.json`；已配准时组版也可直接读取含 cadLinework 的 redline-outline.json。一个项目保留一个当前版底图来源。旧提取文件缺少 cadLinework 时，通过红线解析工具向新路径重新提取。cadVertices 为选定的闭合红线，cadLinework 为模型空间 LINE、朝 +Z 的 LWPOLYLINE 线稿。块参照、文字、注记和其他实体尚不渲染。默认模板通过 profile.template.cadPageLayout 的 frame、keepIds、parcelStyleId 绑定图框、保留项和面样式。项目控制线需有独立依据；说明文字可写入 页面内容.json 对应页的 body。

图件专题使用另一种配准模型。调用 `plugin_site_prepare_topic` 并传 DRAWING 或 DRAWING_CAD 时，仿射变换的 fit 恰好需要三对非共线控制点，check 至少需要一对独立检查点；字段和坐标示例见[专题配准](planning-overlay.md)。
