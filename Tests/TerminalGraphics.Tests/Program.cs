using ProbablyStolenStockMarket.Features;

int passed = 0;
void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"预期 {expected}，实际 {actual}"); }
void Sequence(uint[] expected, uint[] actual) { if (!expected.SequenceEqual(actual)) throw new Exception("像素序列不匹配。"); }
void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("非法输入未被拒绝。"); }

Test("透明及半透明像素原样保留", () =>
{
    uint[] source = { 0x00010203, 0x7f112233, 0xff445566, 0 };
    Sequence(source, NearestImageSampler.Fit(source, 2, 2, 2, 2).Pixels);
});
Test("竖图左右透明补边", () =>
{
    uint[] source = { 1, 2, 3, 4, 5, 6, 7, 8 };
    var result = NearestImageSampler.Fit(source, 2, 4, 4, 4);
    Equal(2, result.DrawnWidth); Equal(4, result.DrawnHeight); Equal(1, result.OffsetX); Equal(0, result.OffsetY);
    Sequence(new uint[] { 0, 1, 2, 0, 0, 3, 4, 0, 0, 5, 6, 0, 0, 7, 8, 0 }, result.Pixels);
});
Test("横图上下透明补边", () =>
{
    var result = NearestImageSampler.Fit(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 4, 2, 4, 4);
    Equal(4, result.DrawnWidth); Equal(2, result.DrawnHeight); Equal(0, result.OffsetX); Equal(1, result.OffsetY);
    Sequence(new uint[] { 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0, 0 }, result.Pixels);
});
Test("放大使用最近邻无插值", () =>
{
    Sequence(new uint[] { 1, 1, 2, 2, 1, 1, 2, 2, 3, 3, 4, 4, 3, 3, 4, 4 }, NearestImageSampler.Fit(new uint[] { 1, 2, 3, 4 }, 2, 2, 4, 4).Pixels);
});
Test("缩小按像素中心选择原像素", () =>
{
    Sequence(new uint[] { 6, 8, 14, 16 }, NearestImageSampler.Fit(Enumerable.Range(1, 16).Select(i => (uint)i).ToArray(), 4, 4, 2, 2).Pixels);
});
Test("非方形采样不越界且不翻转行方向", () =>
{
    var result = NearestImageSampler.Fit(Enumerable.Range(1, 21).Select(i => (uint)i).ToArray(), 3, 7, 9, 4);
    Equal(1, result.DrawnWidth); Equal(4, result.DrawnHeight); Equal(4, result.OffsetX);
    Sequence(new uint[] { 2, 8, 14, 20 }, Enumerable.Range(0, 4).Select(y => result.Pixels[y * 9 + 4]).ToArray());
});
Test("原图比例对应1024乘1536资源", () =>
{
    var result = NearestImageSampler.Fit(new uint[1024 * 1536], 1024, 1536, 16, 16);
    Equal(10, result.DrawnWidth); Equal(16, result.DrawnHeight); Equal(3, result.OffsetX);
});
Test("单像素源及极细长图不越界", () =>
{
    Equal(1, NearestImageSampler.Fit(new uint[] { 99 }, 1, 1, 1, 4).DrawnHeight);
    Equal(1, NearestImageSampler.Fit(new uint[100], 1, 100, 20, 1).DrawnWidth);
});
Test("采样不修改源数组", () =>
{
    uint[] source = { 1, 2, 3, 4 }; NearestImageSampler.Fit(source, 2, 2, 3, 8); Sequence(new uint[] { 1, 2, 3, 4 }, source);
});
Test("非法尺寸及不符像素数量被拒绝", () =>
{
    Reject(() => NearestImageSampler.Fit(new uint[1], 0, 1, 1, 1));
    Reject(() => NearestImageSampler.Fit(new uint[1], 1, -1, 1, 1));
    Reject(() => NearestImageSampler.Fit(new uint[1], 1, 1, 0, 1));
    Reject(() => NearestImageSampler.Fit(new uint[1], 1, 1, 1, -1));
    Reject(() => NearestImageSampler.Fit(new uint[1], 2, 2, 1, 1));
    Reject(() => NearestImageSampler.Fit(new uint[1], 1, 1, int.MaxValue, 2));
    Reject(() => NearestImageSampler.Fit(null!, 1, 1, 1, 1));
});
Console.WriteLine($"Terminal graphics: {passed} tests passed.");
