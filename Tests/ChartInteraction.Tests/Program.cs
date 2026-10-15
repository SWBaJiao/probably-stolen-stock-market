using MelonModTemplate.UI;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("失败：" + name);
    passed++;
    Console.WriteLine("通过：" + name);
}
bool Near(double a, double b) => Math.Abs(a - b) < .000001;
bool Inside(HoverRect inner, HoverRect outer) => inner.Width >= 0 && inner.Height >= 0
    && inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width
    && inner.Y + inner.Height <= outer.Y + outer.Height;

Check(ChartHoverMath.NearestSampleIndex(10, 10, 100, 2) == 0 && ChartHoverMath.NearestSampleIndex(110, 10, 100, 2) == 1, "分时首尾真实采样均可命中");
Check(ChartHoverMath.NearestSampleIndex(59, 10, 100, 2) == 0 && ChartHoverMath.NearestSampleIndex(61, 10, 100, 2) == 1, "分时按横轴最近采样选择而不是按槽选择");
Check(new[] { 10d, 40, 110 }.All(x => ChartHoverMath.NearestSampleIndex(x, 10, 100, 1) == 0), "单个采样在绘图区内可以稳定查看");
Check(ChartHoverMath.NearestSampleIndex(9, 10, 100, 1) == -1 && ChartHoverMath.NearestSampleIndex(111, 10, 100, 1) == -1, "单点模式离开绘图区也会取消命中");
Check(Enumerable.Range(0, 1001).All(i => ChartHoverMath.NearestSampleIndex(10 + i * .5, 10, 500, 1001) == i), "超过历史上限的大量分时采样各自仍可按坐标选中");
Check(ChartHoverMath.CandleSlotIndex(10, 10, 140, 7) == 0 && ChartHoverMath.CandleSlotIndex(150, 10, 140, 7) == 6, "日 K 线左右端点映射首末日");
Check(Enumerable.Range(0, 14).All(i => ChartHoverMath.CandleSlotIndex(10 + (i + .5) * 10, 10, 140, 14) == i), "14 日日 K 线每个中心均映射正确游戏日");
Check(ChartHoverMath.CandleSlotIndex(29.9, 10, 140, 7) == 0 && ChartHoverMath.CandleSlotIndex(30.1, 10, 140, 7) == 1, "日 K 线槽位边界左右不串日");
Check(ChartHoverMath.CandleSlotIndex(-1, 0, 100, 30) == -1 && ChartHoverMath.CandleSlotIndex(101, 0, 100, 30) == -1, "日 K 绘图区外不能选择旧蜡烛");
foreach (var index in new Func<double, double, double, int, int>[] { ChartHoverMath.NearestSampleIndex, ChartHoverMath.CandleSlotIndex })
{
    Check(index(0, 0, 100, 0) == -1 && index(0, 0, 0, 1) == -1 && index(0, 0, -1, 1) == -1, "空数据或未完成布局不产生索引");
    Check(index(double.NaN, 0, 100, 3) == -1 && index(10, 0, double.PositiveInfinity, 3) == -1, "非法指针或布局数值被拒绝");
}
var viewport = new HoverRect(0, 0, 100, 100);
Check(ChartHoverMath.Contains(viewport, 0, 0) && ChartHoverMath.Contains(viewport, 100, 100) && !ChartHoverMath.Contains(viewport, -1, 50), "可见矩形边界包含端点并排除外部");
Check(ChartHoverMath.TryIntersect(viewport, new HoverRect(50, 50, 100, 100), out var clip)
    && Near(clip.X, 50) && Near(clip.Y, 50) && Near(clip.Width, 50) && Near(clip.Height, 50), "部分滚动裁切保留正确可见区域");
Check(!ChartHoverMath.TryIntersect(viewport, new HoverRect(101, 0, 100, 100), out _)
    && !ChartHoverMath.TryIntersect(viewport, new HoverRect(100, 0, 100, 100), out _), "完全裁出或仅边界接触时没有可悬停面积");
var window = new HoverRect(-160, -100, 320, 200);
foreach (var anchor in new[] { new HoverPoint(-160, -100), new HoverPoint(160, -100), new HoverPoint(-160, 100), new HoverPoint(160, 100), new HoverPoint(0, 0) })
    Check(Inside(ChartHoverMath.FitTooltip(anchor, 150, 80, window), window), "提示在窗口角落和中心均保持窗口内");
var large = ChartHoverMath.FitTooltip(new HoverPoint(150, 90), 1000, 1000, window);
Check(Inside(large, window) && large.Width < 1000 && large.Height < 1000, "超大提示按可见窗口尺寸收缩");
var normal = ChartHoverMath.FitTooltip(new HoverPoint(0, 0), 100, 50, new HoverRect(-500, -300, 1000, 600));
Check(Near(normal.Width, 100) && Near(normal.Height, 50) && !ChartHoverMath.Contains(normal, 0, 0), "空间足够时保留首选尺寸并避开鼠标位置");
Console.WriteLine($"图表交互边界检查通过：{passed} 项。");
