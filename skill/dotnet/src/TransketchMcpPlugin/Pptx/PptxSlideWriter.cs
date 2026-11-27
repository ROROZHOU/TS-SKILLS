using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// 在既有幻灯片上追加原生形状、文本框、自定义路径与图片；坐标单位为像素，内部换算 EMU
/// </summary>
/// <date>2026年11月16日</date>
internal sealed class PptxSlideWriter
{
    private const int EmuPerPx = 9525;
    private const int EmuPerPt = 12700;

    private readonly XElement _tree;
    private readonly string _fontFamily;
    private int _id = 10000;

    /// <summary>
    /// 从一个已加载的幻灯片 XML 建立写入器
    /// </summary>
    /// <param name="slide">幻灯片 XML 文档</param>
    /// <param name="fontFamily">文本框字体</param>
    public PptxSlideWriter(XDocument slide, string fontFamily)
    {
        _fontFamily = fontFamily;
        _tree = slide.Root?.Element(PptxNames.P + "cSld")?.Element(PptxNames.P + "spTree")
                ?? throw new SiteContextException("幻灯片缺少 p:cSld/p:spTree，无法写入原生对象");
    }

    /// <summary>
    /// 当前形状树
    /// </summary>
    public XElement Tree => _tree;

    /// <summary>
    /// 追加矩形或椭圆形状
    /// </summary>
    /// <param name="name">对象名</param>
    /// <param name="x">左上角 x，像素</param>
    /// <param name="y">左上角 y，像素</param>
    /// <param name="width">宽，像素</param>
    /// <param name="height">高，像素</param>
    /// <param name="color">填充色，六位十六进制；为空表示不填充</param>
    /// <param name="line">描边色，六位十六进制；为空表示无描边</param>
    /// <param name="geometry">预设几何，rect 或 ellipse</param>
    public XElement Shape(
        string name,
        double x,
        double y,
        double width,
        double height,
        string? color = null,
        string? line = null,
        string geometry = "rect")
    {
        _id++;
        var shape = new XElement(PptxNames.P + "sp");
        var nonVisual = new XElement(PptxNames.P + "nvSpPr");
        nonVisual.Add(new XElement(PptxNames.P + "cNvPr", new XAttribute("id", _id), new XAttribute("name", name)));
        nonVisual.Add(new XElement(PptxNames.P + "cNvSpPr"));
        nonVisual.Add(new XElement(PptxNames.P + "nvPr"));
        shape.Add(nonVisual);

        var properties = new XElement(PptxNames.P + "spPr");
        properties.Add(Transform(x, y, Math.Max(1, width * EmuPerPx), Math.Max(1, height * EmuPerPx)));
        var geometryElement = new XElement(PptxNames.A + "prstGeom", new XAttribute("prst", geometry));
        geometryElement.Add(new XElement(PptxNames.A + "avLst"));
        properties.Add(geometryElement);
        if (color is null)
        {
            properties.Add(new XElement(PptxNames.A + "noFill"));
        }
        else
        {
            var solid = new XElement(PptxNames.A + "solidFill");
            solid.Add(new XElement(PptxNames.A + "srgbClr", new XAttribute("val", color)));
            properties.Add(solid);
        }

        var stroke = new XElement(PptxNames.A + "ln", new XAttribute("w", 2 * EmuPerPt));
        if (line is null)
        {
            stroke.Add(new XElement(PptxNames.A + "noFill"));
        }
        else
        {
            var solid = new XElement(PptxNames.A + "solidFill");
            solid.Add(new XElement(PptxNames.A + "srgbClr", new XAttribute("val", line)));
            stroke.Add(solid);
        }

        properties.Add(stroke);
        shape.Add(properties);
        _tree.Add(shape);
        return shape;
    }

    /// <summary>
    /// 追加文本框；换行按 \n 拆成多个段落
    /// </summary>
    /// <param name="name">对象名</param>
    /// <param name="text">文本</param>
    /// <param name="x">左上角 x，像素</param>
    /// <param name="y">左上角 y，像素</param>
    /// <param name="width">宽，像素</param>
    /// <param name="height">高，像素</param>
    /// <param name="size">字号，磅</param>
    /// <param name="color">字色，六位十六进制</param>
    public XElement Text(
        string name,
        string text,
        double x,
        double y,
        double width,
        double height,
        double size,
        string color)
    {
        var shape = Shape(name, x, y, width, height);
        var body = new XElement(PptxNames.P + "txBody");
        body.Add(new XElement(
            PptxNames.A + "bodyPr",
            new XAttribute("wrap", "square"),
            new XAttribute("lIns", 0),
            new XAttribute("tIns", 0),
            new XAttribute("rIns", 0),
            new XAttribute("bIns", 0)));
        body.Add(new XElement(PptxNames.A + "lstStyle"));

        foreach (var line in text.Split('\n'))
        {
            var paragraph = new XElement(PptxNames.A + "p");
            var run = new XElement(PptxNames.A + "r");
            var properties = new XElement(
                PptxNames.A + "rPr",
                new XAttribute("lang", "zh-CN"),
                new XAttribute("sz", Round(size * 100)));
            var fill = new XElement(PptxNames.A + "solidFill");
            fill.Add(new XElement(PptxNames.A + "srgbClr", new XAttribute("val", color)));
            properties.Add(fill);
            properties.Add(new XElement(PptxNames.A + "latin", new XAttribute("typeface", _fontFamily)));
            properties.Add(new XElement(PptxNames.A + "ea", new XAttribute("typeface", _fontFamily)));
            run.Add(properties);
            var content = new XElement(PptxNames.A + "t");
            if (line.Length > 0)
                content.Value = line;
            run.Add(content);
            paragraph.Add(run);
            body.Add(paragraph);
        }

        shape.Add(body);
        return shape;
    }

    /// <summary>
    /// 追加以折线或闭合多边形为几何的原生对象
    /// </summary>
    /// <param name="name">对象名</param>
    /// <param name="points">像素坐标点列</param>
    /// <param name="color">描边色，六位十六进制</param>
    /// <param name="width">描边宽，像素</param>
    /// <param name="fill">填充色，六位十六进制；为空表示不填充</param>
    /// <param name="closed">是否闭合</param>
    public XElement Path(
        string name,
        IReadOnlyList<(double X, double Y)> points,
        string color,
        double width,
        string? fill,
        bool closed, List<List<(double X, double Y)>>? holes = null)
    {
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var spanX = points.Max(point => point.X) - minX;
        var spanY = points.Max(point => point.Y) - minY;
        var shape = Shape(name, minX, minY, spanX, spanY, fill, color);
        var properties = shape.Element(PptxNames.P + "spPr")
                         ?? throw new SiteContextException("形状缺少 p:spPr");
        properties.Element(PptxNames.A + "prstGeom")?.Remove();

        var geometry = new XElement(PptxNames.A + "custGeom");
        foreach (var listName in new[] { "avLst", "gdLst", "ahLst", "cxnLst" })
            geometry.Add(new XElement(PptxNames.A + listName));
        geometry.Add(new XElement(
            PptxNames.A + "rect",
            new XAttribute("l", 0),
            new XAttribute("t", 0),
            new XAttribute("r", "r"),
            new XAttribute("b", "b")));

        var pathList = new XElement(PptxNames.A + "pathLst");
        var path = new XElement(
            PptxNames.A + "path",
            new XAttribute("w", Math.Max(1, Round(spanX * EmuPerPx))),
            new XAttribute("h", Math.Max(1, Round(spanY * EmuPerPx))),
            new XAttribute("fill", fill is null ? "none" : "norm"));
        void Subpath(IReadOnlyList<(double X,double Y)> vertices)
        {
        for (var index = 0; index < vertices.Count; index++)
        {
            var point = vertices[index];
            var element = new XElement(PptxNames.A + (index == 0 ? "moveTo" : "lnTo"));
            element.Add(new XElement(
                PptxNames.A + "pt",
                new XAttribute("x", Round((point.X - minX) * EmuPerPx)),
                new XAttribute("y", Round((point.Y - minY) * EmuPerPx))));
            path.Add(element);
        }

        if (closed)
            path.Add(new XElement(PptxNames.A + "close"));
        }
        Subpath(points);
        static double Winding(IReadOnlyList<(double X,double Y)> r) =>
            Enumerable.Range(0,r.Count).Sum(i=>r[i].X*r[(i+1)%r.Count].Y-r[(i+1)%r.Count].X*r[i].Y);
        foreach (var hole in holes ?? [])
        {
            if (Math.Sign(Winding(hole)) == Math.Sign(Winding(points))) hole.Reverse();
            Subpath(hole);
        }
        pathList.Add(path);
        geometry.Add(pathList);
        properties.Elements().First().AddAfterSelf(geometry);
        properties.Element(PptxNames.A + "ln")
            ?.SetAttributeValue("w", Round(width * EmuPerPt));
        return shape;
    }

    /// <summary>
    /// 追加图片；可用 srcRect 在 PPT 内裁切，避免重新编码图像
    /// </summary>
    /// <param name="relationshipId">图片关系 id</param>
    /// <param name="x">左上角 x，像素</param>
    /// <param name="y">左上角 y，像素</param>
    /// <param name="width">宽，像素</param>
    /// <param name="height">高，像素</param>
    /// <param name="sourceRect">裁切比例，各值取 0~1，依次为左、上、右、下</param>
    public XElement Picture(
        string relationshipId,
        double x,
        double y,
        double width,
        double height,
        (double Left, double Top, double Right, double Bottom)? sourceRect = null)
    {
        _id++;
        var picture = new XElement(PptxNames.P + "pic");
        var nonVisual = new XElement(PptxNames.P + "nvPicPr");
        nonVisual.Add(new XElement(
            PptxNames.P + "cNvPr",
            new XAttribute("id", _id),
            new XAttribute("name", "basemap")));
        nonVisual.Add(new XElement(PptxNames.P + "cNvPicPr"));
        nonVisual.Add(new XElement(PptxNames.P + "nvPr"));
        picture.Add(nonVisual);

        var blipFill = new XElement(PptxNames.P + "blipFill");
        blipFill.Add(new XElement(
            PptxNames.A + "blip",
            new XAttribute(PptxNames.R + "embed", relationshipId)));
        if (sourceRect is { } rect)
        {
            blipFill.Add(new XElement(
                PptxNames.A + "srcRect",
                Attribute("l", rect.Left),
                Attribute("t", rect.Top),
                Attribute("r", rect.Right),
                Attribute("b", rect.Bottom)));
        }

        var stretch = new XElement(PptxNames.A + "stretch");
        stretch.Add(new XElement(PptxNames.A + "fillRect"));
        blipFill.Add(stretch);
        picture.Add(blipFill);

        var properties = new XElement(PptxNames.P + "spPr");
        properties.Add(Transform(x, y, width * EmuPerPx, height * EmuPerPx));
        var geometry = new XElement(PptxNames.A + "prstGeom", new XAttribute("prst", "rect"));
        geometry.Add(new XElement(PptxNames.A + "avLst"));
        properties.Add(geometry);
        picture.Add(properties);
        _tree.Add(picture);
        return picture;
    }

    /// <summary>
    /// 生成 a:xfrm 节点
    /// </summary>
    /// <param name="x">左上角 x，像素</param>
    /// <param name="y">左上角 y，像素</param>
    /// <param name="width">宽，EMU</param>
    /// <param name="height">高，EMU</param>
    private static XElement Transform(double x, double y, double width, double height)
    {
        var transform = new XElement(PptxNames.A + "xfrm");
        transform.Add(new XElement(
            PptxNames.A + "off",
            new XAttribute("x", Round(x * EmuPerPx)),
            new XAttribute("y", Round(y * EmuPerPx))));
        transform.Add(new XElement(
            PptxNames.A + "ext",
            new XAttribute("cx", Round(Math.Max(1, width))),
            new XAttribute("cy", Round(Math.Max(1, height)))));
        return transform;
    }

    /// <summary>
    /// srcRect 的千分比属性，取值 0~1
    /// </summary>
    /// <param name="name">属性名</param>
    /// <param name="value">比例</param>
    private static XAttribute Attribute(string name, double value) =>
        new(name, (long)Math.Round(value * 100000, MidpointRounding.ToEven));

    /// <summary>
    /// 与原实现一致的取整，避免与模板既有数值出现 1 单位偏差
    /// </summary>
    /// <param name="value">待取整数值</param>
    private static long Round(double value) => (long)Math.Round(value, MidpointRounding.ToEven);
}
