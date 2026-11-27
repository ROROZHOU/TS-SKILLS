namespace TransketchMcpPlugin;

/// <summary>
/// 组版裁剪几何：多边形按矩形裁剪、折线按矩形分段
/// </summary>
/// <date>2026年11月16日</date>
internal static class ComposeGeometry
{
    /// <summary>
    /// 像素坐标点
    /// </summary>
    /// <param name="X">x</param>
    /// <param name="Y">y</param>
    public readonly record struct Point(double X, double Y);

    /// <summary>
    /// Sutherland–Hodgman 裁剪，矩形边界依次为左、右、上、下
    /// </summary>
    /// <param name="points">原始多边形</param>
    /// <param name="left">左边界</param>
    /// <param name="top">上边界</param>
    /// <param name="right">右边界</param>
    /// <param name="bottom">下边界</param>
    public static List<Point> ClipPolygon(
        IReadOnlyList<Point> points,
        double left,
        double top,
        double right,
        double bottom)
    {
        var current = points.ToList();
        foreach (var (axis, edge, sign) in new[] { (0, left, 1.0), (0, right, -1.0), (1, top, 1.0), (1, bottom, -1.0) })
        {
            if (current.Count == 0)
                return [];
            var result = new List<Point>();
            var previous = current[^1];
            foreach (var point in current)
            {
                var previousInside = sign * (Value(previous, axis) - edge) >= 0;
                var pointInside = sign * (Value(point, axis) - edge) >= 0;
                if (previousInside != pointInside)
                {
                    var t = (edge - Value(previous, axis)) / (Value(point, axis) - Value(previous, axis));
                    result.Add(new Point(
                        previous.X + t * (point.X - previous.X),
                        previous.Y + t * (point.Y - previous.Y)));
                }

                if (pointInside)
                    result.Add(point);
                previous = point;
            }

            current = result;
        }

        return current;
    }

    /// <summary>
    /// 折线逐段做 Liang–Barsky 裁剪，返回多个连续片段
    /// </summary>
    /// <param name="points">原始折线</param>
    /// <param name="left">左边界</param>
    /// <param name="top">上边界</param>
    /// <param name="right">右边界</param>
    /// <param name="bottom">下边界</param>
    public static List<List<Point>> ClipLine(
        IReadOnlyList<Point> points,
        double left,
        double top,
        double right,
        double bottom)
    {
        var chunks = new List<List<Point>>();
        var current = new List<Point>();
        for (var index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            double low = 0;
            double high = 1;
            foreach (var (p, q) in new[]
                     {
                         (-dx, start.X - left),
                         (dx, right - start.X),
                         (-dy, start.Y - top),
                         (dy, bottom - start.Y)
                     })
            {
                if (p == 0)
                {
                    if (q < 0)
                    {
                        high = -1;
                        break;
                    }
                }
                else if (p < 0)
                {
                    low = Math.Max(low, q / p);
                }
                else
                {
                    high = Math.Min(high, q / p);
                }
            }

            if (low > high)
            {
                if (current.Count > 0)
                {
                    chunks.Add(current);
                    current = [];
                }

                continue;
            }

            var from = new Point(start.X + low * dx, start.Y + low * dy);
            var to = new Point(start.X + high * dx, start.Y + high * dy);
            if (current.Count > 0 && Distance(current[^1], from) > 0.01)
            {
                chunks.Add(current);
                current = [];
            }

            if (current.Count == 0)
                current.Add(from);
            current.Add(to);
        }

        if (current.Count > 0)
            chunks.Add(current);
        return chunks;
    }

    /// <summary>
    /// 两点距离
    /// </summary>
    /// <param name="from">起点</param>
    /// <param name="to">终点</param>
    public static double Distance(Point from, Point to) =>
        Math.Sqrt((from.X - to.X) * (from.X - to.X) + (from.Y - to.Y) * (from.Y - to.Y));

    private static double Value(Point point, int axis) => axis == 0 ? point.X : point.Y;
}
