namespace TransketchMcpPlugin;

/// <summary>
/// WGS84 到 GCJ-02 的坐标转换，沿用高德标准参数
/// </summary>
/// <date>2026年11月16日</date>
internal static class Gcj02Transform
{
    private const double A = 6378245.0;
    private const double Ee = 0.00669342162296594323;

    /// <summary>
    /// 把 WGS84 经纬度转换为 GCJ-02
    /// </summary>
    /// <param name="lng">经度</param>
    /// <param name="lat">纬度</param>
    public static (double Lng, double Lat) Wgs2Gcj(double lng, double lat)
    {
        var deltaLat = TransformLat(lng - 105.0, lat - 35.0);
        var deltaLng = TransformLng(lng - 105.0, lat - 35.0);
        var rad = lat / 180.0 * Math.PI;
        var magic = 1 - Ee * Math.Sin(rad) * Math.Sin(rad);
        var sqrt = Math.Sqrt(magic);
        deltaLat = deltaLat * 180.0 / (A * (1 - Ee) / (magic * sqrt) * Math.PI);
        deltaLng = deltaLng * 180.0 / (A / sqrt * Math.Cos(rad) * Math.PI);
        return (lng + deltaLng, lat + deltaLat);
    }

    private static double TransformLat(double x, double y)
    {
        var result = -100.0 + 2.0 * x + 3.0 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.Sqrt(Math.Abs(x));
        result += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        result += (20.0 * Math.Sin(y * Math.PI) + 40.0 * Math.Sin(y / 3.0 * Math.PI)) * 2.0 / 3.0;
        result += (160.0 * Math.Sin(y / 12.0 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30.0)) * 2.0 / 3.0;
        return result;
    }

    private static double TransformLng(double x, double y)
    {
        var result = 300.0 + x + 2.0 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.Sqrt(Math.Abs(x));
        result += (20.0 * Math.Sin(6.0 * x * Math.PI) + 20.0 * Math.Sin(2.0 * x * Math.PI)) * 2.0 / 3.0;
        result += (20.0 * Math.Sin(x * Math.PI) + 40.0 * Math.Sin(x / 3.0 * Math.PI)) * 2.0 / 3.0;
        result += (150.0 * Math.Sin(x / 12.0 * Math.PI) + 300.0 * Math.Sin(x / 30.0 * Math.PI)) * 2.0 / 3.0;
        return result;
    }
}
