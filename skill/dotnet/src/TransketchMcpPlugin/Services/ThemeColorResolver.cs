using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TransketchMcpPlugin;

/// <summary>
/// 把 OOXML 颜色解析为可寻址的 RGB；主题色、tint/shade/lumMod/lumOff 的换算与参考实现保持一致
/// </summary>
/// <date>2026年11月16日</date>
internal static class ThemeColorResolver
{
    private static readonly Dictionary<string, string> SchemeAliases = new(StringComparer.Ordinal)
    {
        ["bg1"] = "lt1",
        ["bg2"] = "lt2",
        ["tx1"] = "dk1",
        ["tx2"] = "dk2"
    };

    /// <summary>
    /// 读取主题色表
    /// </summary>
    /// <param name="themeXml">theme1.xml 的根节点</param>
    public static Dictionary<string, string> ReadScheme(XElement themeXml)
    {
        var scheme = new Dictionary<string, string>(StringComparer.Ordinal);
        var colorScheme = themeXml.Descendants(PptxNames.A + "clrScheme").FirstOrDefault();
        if (colorScheme is null)
            return scheme;

        foreach (var entry in colorScheme.Elements())
        {
            var value = entry.Elements().FirstOrDefault();
            if (value is null)
                continue;

            var text = value.Name.LocalName == "srgbClr"
                ? value.Attribute("val")?.Value
                : value.Attribute("lastClr")?.Value;
            if (!string.IsNullOrEmpty(text))
                scheme[entry.Name.LocalName] = text;
        }

        return scheme;
    }

    /// <summary>
    /// 解析颜色元素；不支持的颜色返回带 unresolved 标记的原始 XML
    /// </summary>
    /// <param name="colorElement">srgbClr/schemeClr/sysClr 等颜色元素</param>
    /// <param name="scheme">主题色表</param>
    public static JsonNode? Resolve(XElement? colorElement, IReadOnlyDictionary<string, string> scheme)
    {
        if (colorElement is null)
            return null;

        var type = colorElement.Name.LocalName;
        var value = colorElement.Attribute("val")?.Value;
        var hex = type == "schemeClr"
            ? scheme.GetValueOrDefault(SchemeAliases.GetValueOrDefault(value ?? "", value ?? ""), "000000")
            : colorElement.Attribute("lastClr")?.Value ?? value;

        if (string.IsNullOrEmpty(hex) || hex.Length != 6)
        {
            return new JsonObject
            {
                ["raw"] = colorElement.ToString(SaveOptions.DisableFormatting),
                ["unresolved"] = true
            };
        }

        var red = int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        var green = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        var blue = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        var rgb = new[] { red, green, blue };
        var alpha = 1.0;

        foreach (var transform in colorElement.Elements())
        {
            var kind = transform.Name.LocalName;
            var raw = transform.Attribute("val")?.Value;
            var factor = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed / 100000.0
                : 1.0;

            switch (kind)
            {
                case "alpha":
                    alpha = factor;
                    break;
                case "tint":
                    rgb = [rgb[0] + (1 - rgb[0]) * factor, rgb[1] + (1 - rgb[1]) * factor, rgb[2] + (1 - rgb[2]) * factor];
                    break;
                case "shade":
                    rgb = [rgb[0] * factor, rgb[1] * factor, rgb[2] * factor];
                    break;
                case "lumMod":
                case "lumOff":
                    var (hue, luminance, saturation) = RgbToHls(rgb[0], rgb[1], rgb[2]);
                    luminance = kind == "lumMod" ? luminance * factor : luminance + factor;
                    rgb = HlsToRgb(hue, Math.Clamp(luminance, 0, 1), saturation);
                    break;
            }
        }

        return new JsonObject
        {
            ["color"] = "#" + string.Concat(rgb.Select(channel =>
                ((int)Math.Round(Math.Clamp(channel, 0, 1) * 255)).ToString("X2", CultureInfo.InvariantCulture))),
            ["opacity"] = alpha,
            ["scheme"] = type == "schemeClr" ? value : null
        };
    }

    /// <summary>
    /// 解析填充；noFill 视为全透明黑色
    /// </summary>
    /// <param name="element">p:spPr 或 a:ln</param>
    /// <param name="scheme">主题色表</param>
    public static JsonNode? ResolveFill(XElement? element, IReadOnlyDictionary<string, string> scheme)
    {
        if (element is null)
            return null;
        if (element.Element(PptxNames.A + "noFill") is not null)
            return new JsonObject { ["color"] = "#000000", ["opacity"] = 0 };

        var solid = element.Element(PptxNames.A + "solidFill");
        return solid is null ? null : Resolve(solid.Elements().FirstOrDefault(), scheme);
    }

    private static (double Hue, double Luminance, double Saturation) RgbToHls(double red, double green, double blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var sum = max + min;
        var range = max - min;
        var luminance = sum / 2.0;
        if (min == max)
            return (0.0, luminance, 0.0);

        var saturation = luminance <= 0.5 ? range / sum : range / (2.0 - max - min);
        var rc = (max - red) / range;
        var gc = (max - green) / range;
        var bc = (max - blue) / range;
        var hue = red >= max
            ? bc - gc
            : green >= max
                ? 2.0 + rc - bc
                : 4.0 + gc - rc;
        hue = hue / 6.0 % 1.0;
        if (hue < 0)
            hue += 1.0;
        return (hue, luminance, saturation);
    }

    private static double[] HlsToRgb(double hue, double luminance, double saturation)
    {
        if (saturation <= 0)
            return [luminance, luminance, luminance];

        var m2 = luminance <= 0.5 ? luminance * (1.0 + saturation) : luminance + saturation - luminance * saturation;
        var m1 = 2.0 * luminance - m2;
        return [Channel(m1, m2, hue + 1.0 / 3.0), Channel(m1, m2, hue), Channel(m1, m2, hue - 1.0 / 3.0)];
    }

    private static double Channel(double m1, double m2, double hue)
    {
        hue %= 1.0;
        if (hue < 0)
            hue += 1.0;
        if (hue < 1.0 / 6.0)
            return m1 + (m2 - m1) * hue * 6.0;
        if (hue < 0.5)
            return m2;
        if (hue < 2.0 / 3.0)
            return m1 + (m2 - m1) * (2.0 / 3.0 - hue) * 6.0;
        return m1;
    }
}
