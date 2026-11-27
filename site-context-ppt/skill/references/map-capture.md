# 地图采集与 PPTX 组版

## 项目运行目录

在技能外为每个项目创建一个运行目录。先建 `过程/输入/` 并准备项目配置。由 C# 地图工具 新建 `过程/地图/`，组版时新建 `成果/`。

```text
<run>/
  过程/
    输入/
      周边场地分析模板.pptx
      项目配置.json             # MCP 工具读取的项目配置
      cad-geometry.json          # 默认红线页的 DWG 提取结果（已配准文件含线稿时可省略）
      redline-outline.json       # 启用用地红线叠加时需要
      page-plan.json             # 上传模板的逐页计划
    专题/<数据集名>.json          # 第 3 步可选产物
    页面内容.json
    地图/
      site.config.json
      P<页码>.json
      P<页码>-screen.jpg
  成果/
    周边场地分析.pptx
```

## 生成与采集

1. 复制 `assets/webapp/site.config.json` 到项目输入区，填写本项目名称、坐标与 WGS84/GCJ02。按当前模板校对样式绑定、启用图层、视野与裁切。默认布局地图框为 1664×884，例如 1280×720 的视口可使用 `[0,0,1280,680]` 裁切。其他模板按实际图框比例设置。
2. 调用本地 Transketch MCP 的 `plugin_capture_site_maps`，示例参数如下。运行目录须为绝对路径。可选 `photosDirectory` 指向含有经核实的北东南西照片（文件名 1–4）的本机目录。凭据缺失时提示用户通过宿主管理界面配置。
```json
{"request":{"runDirectory":"D:/项目/场地分析01","width":1280,"height":720}}
```
3. 工具在本机 Edge 中逐页生成同视口的JPG 底图和 JSON 几何快照，回执包含 `directory`、`pages` 和 `warnings`。逐页检查快照中的 `captureWarnings`；道路升级数据超时、失败或为空时，保留当时已有的几何并计入资料缺项。新采集使用尚无 `过程/地图` 的运行目录；已有完整采集可直接组版。核对底图、版权、审图号、启用图层和缺项后继续。经核实的 `verifiedPOIs` 和 `verifiedLabels` 可补充地图数据；专题文件由第 5 步单独读取。后补资料时保留原图和快照，直接重新组版、验收。更换运行目录时可传 request.reuseRunDirectory，按页核对采集条件及底图和快照的内容校验；只采集不匹配的页。模板填色、字号、PPT 裁切与图框变化由组版处理。

## 组版与验收

第 4 步的 mpp 表示每个屏幕像素对应的地面米数。第 5 步计算图框宽度 / 裁切宽度和图框高度 / 裁切高度，要求二者在容差内相等。例如 1664 / 1280 = 884 / 680 = 1.3。几何按此倍率布局；模板配置的 CSS 字号换算到 PPT 磅时再乘 0.75。

`页面内容.json` 以数字页码为键。每个卫星页的 `sub`、`body`、`figure`、`caption` 和 `attribution` 均为 JSON 字符串。用 `"figure": "基地周边水系"` 填写图名。署名填写实际底图、审图号、相关数据源和采集日期。按需提供 `sources`（来源对象）、`remaining_gaps`（缺项数组）、`legendLabels`（图层或分类键到图例名称）、`sideText` 或 `sideImage`（含 `file`、`caption`、`source`，图片路径相对于 `过程/`）。用地红线仅用于经核验配准的近距卫星页。启用时提供 `redline-outline.json`，含米制平面 `points` 和 `metadata.handle`、`metadata.centroid`，并记录原件及配准依据。

调用 `plugin_site_compose`，action 先用 compose，再用 validate。两次的 runDir 和 profilePath 一致。默认模板使用 [组版 profile](../assets/default-template-extraction/site-context-profile.json)。上传模板使用本项目审核过的 profile，核对页号、图框、视口、图例及允许直接叠加红线的半径。默认红线页读取本项目 CAD 线稿和边界，使用模板绑定的红线对象样式。旧版 profile 可指定本项目的 CAD 基准页；缺资料时先处理红线页待补状态，并记入交付缺项。指定页修订同时传 baselinePptxPath 和逗号分隔的 pages。

检查 `过程/图层对象来源.json` 和 `过程/验收结果.json`，分别核对生成溯源与数据完整性，再按[验收清单](acceptance.md)检查逐页渲染。

第 4 步在截图前应用该卫星页的 basemapStyle。改动模式后，在新运行目录采集，并指定原运行目录复用仍匹配的页。底图不含照片卡片；交互预览仅给已有照片的方向生成卡片，候选视点可独立导出。

保存每页前检查截图上的大块连续近纯灰区域；有疑似缺块时等待并重拍。连续四次仍异常，工具返回页码并放弃本次地图输出。0.8 以前的采集回执需要重新采集。
