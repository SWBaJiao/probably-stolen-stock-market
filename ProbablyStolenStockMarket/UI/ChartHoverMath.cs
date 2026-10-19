using System;

namespace ProbablyStolenStockMarket.UI;

internal readonly record struct HoverPoint(double X, double Y);
internal readonly record struct HoverRect(double X, double Y, double Width, double Height);

// 与 Unity 无关的索引选择和可见边界计算，坐标原点统一为左下。
internal static class ChartHoverMath
{
    public static int NearestSampleIndex(double x, double left, double width, int count)
    {
        if (!ValidAxis(x, left, width, count)) return -1;
        if (count == 1) return 0;
        return Math.Clamp((int)Math.Floor((x - left) / width * (count - 1) + .5), 0, count - 1);
    }

    public static int CandleSlotIndex(double x, double left, double width, int count)
    {
        if (!ValidAxis(x, left, width, count)) return -1;
        return Math.Clamp((int)Math.Floor((x - left) / width * count), 0, count - 1);
    }

    public static bool Contains(HoverRect rect, double x, double y) => ValidRect(rect) && Finite(x) && Finite(y)
        && x >= rect.X && x <= rect.X + rect.Width && y >= rect.Y && y <= rect.Y + rect.Height;

    public static bool TryIntersect(HoverRect a, HoverRect b, out HoverRect intersection)
    {
        intersection = default;
        if (!ValidRect(a) || !ValidRect(b)) return false;
        var left = Math.Max(a.X, b.X); var bottom = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width); var top = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || top <= bottom) return false;
        intersection = new HoverRect(left, bottom, right - left, top - bottom);
        return true;
    }

    public static HoverRect FitTooltip(HoverPoint anchor, double preferredWidth, double preferredHeight, HoverRect bounds, double gap = 12, double margin = 6)
    {
        if (!ValidRect(bounds) || !Finite(anchor.X) || !Finite(anchor.Y) || !Finite(preferredWidth) || !Finite(preferredHeight)
            || preferredWidth <= 0 || preferredHeight <= 0 || !Finite(gap) || !Finite(margin) || gap < 0 || margin < 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "提示边界和尺寸必须是有效正数。");
        var mx = Math.Min(margin, bounds.Width / 4); var my = Math.Min(margin, bounds.Height / 4);
        var width = Math.Min(preferredWidth, bounds.Width - 2 * mx);
        var height = Math.Min(preferredHeight, bounds.Height - 2 * my);
        var x = anchor.X + gap; var y = anchor.Y + gap;
        if (x + width > bounds.X + bounds.Width - mx) x = anchor.X - gap - width;
        if (y + height > bounds.Y + bounds.Height - my) y = anchor.Y - gap - height;
        x = Math.Clamp(x, bounds.X + mx, bounds.X + bounds.Width - mx - width);
        y = Math.Clamp(y, bounds.Y + my, bounds.Y + bounds.Height - my - height);
        return new HoverRect(x, y, width, height);
    }

    private static bool ValidAxis(double x, double left, double width, int count) => count > 0 && Finite(x) && Finite(left) && Finite(width)
        && width > 0 && Finite(left + width) && x >= left && x <= left + width;
    private static bool ValidRect(HoverRect rect) => Finite(rect.X) && Finite(rect.Y) && Finite(rect.Width) && Finite(rect.Height)
        && rect.Width > 0 && rect.Height > 0 && Finite(rect.X + rect.Width) && Finite(rect.Y + rect.Height);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
