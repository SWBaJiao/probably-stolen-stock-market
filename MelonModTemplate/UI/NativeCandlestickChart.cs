using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using MelonModTemplate.Market;
using UnityEngine;
using UnityEngine.UI;

namespace MelonModTemplate.UI;

// 用原生 UGUI Image 色块绘制日 K 线，不创建纹理，也不依赖 IMGUI、GL 或 Graphics 绘制。
public sealed class NativeCandlestickChart : IDisposable
{
    private const int Capacity = 30;
    private static readonly Color Rising = StockUiTheme.Rising;
    private static readonly Color Falling = StockUiTheme.Falling;
    private static readonly Color Neutral = StockUiTheme.Neutral;
    private static readonly Color Ink = StockUiTheme.Text;
    private static readonly Color Accent = StockUiTheme.Accent;
    private static readonly Color GridColor = StockUiTheme.Grid;
    private readonly RectTransform _parent;
    private readonly TMP_FontAsset _font;
    private readonly Material? _material;
    private readonly GameObject _root;
    private readonly RectTransform _rect;
    private readonly CandleNodes[] _pool = new CandleNodes[Capacity];
    private readonly GraphicNode[] _grid = new GraphicNode[5];
    private readonly GraphicNode[] _priceLabels = new GraphicNode[5];
    private readonly GraphicNode[] _dayLabels = new GraphicNode[7];
    private readonly GraphicNode _title, _legend, _note, _currentLabel, _currentLine, _bottomAxis;
    private CandleSample[] _previous = Array.Empty<CandleSample>();
    private decimal _previousPrice;
    private int _previousDays, _previousCurrentDay = int.MinValue;
    private float _previousWidth = -1, _previousHeight = -1;
    private bool _hasRendered, _disposed, _faulted;
    private ChartHoverOverlay? _hover;
    private HoverRect _hoverPlot;
    private bool _hoverReady, _hoverWarned;

    public NativeCandlestickChart(RectTransform parent, TMP_FontAsset font, Material? material = null)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _font = font ?? throw new ArgumentNullException(nameof(font));
        _material = material;
        _root = RectObject("DailyCandles", parent);
        _rect = GetRect(_root);
        _rect.anchorMin = Vector2.zero;
        _rect.anchorMax = Vector2.one;
        _rect.offsetMin = Vector2.zero;
        _rect.offsetMax = Vector2.zero;
        try
        {
            var background = _root.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            background.color = StockUiTheme.ChartBackground;
            background.raycastTarget = false;
            _title = Text("Title", TextAlignmentOptions.TopLeft, 13, Ink);
            _legend = Text("Legend", TextAlignmentOptions.TopRight, 11, Ink);
            _legend.Text!.text = "红涨  绿跌  灰横标：不完整";
            _note = Text("Note", TextAlignmentOptions.TopLeft, 11, StockUiTheme.Muted);
            _note.Text!.enableWordWrapping = true;
            _currentLabel = Text("CurrentPrice", TextAlignmentOptions.MidlineLeft, 11, Accent);
            _currentLine = Block("CurrentLine", Accent);
            _bottomAxis = Block("BottomAxis", GridColor);
            for (int i = 0; i < _grid.Length; i++)
            {
                _grid[i] = Block("PriceGrid." + i, GridColor);
                _priceLabels[i] = Text("PriceLabel." + i, TextAlignmentOptions.MidlineLeft, 11, StockUiTheme.Muted);
            }
            for (int i = 0; i < _dayLabels.Length; i++)
                _dayLabels[i] = Text("DayLabel." + i, TextAlignmentOptions.Midline, 11, StockUiTheme.Muted);
            for (int i = 0; i < _pool.Length; i++)
                _pool[i] = new CandleNodes(Block("Wick." + i, Neutral), Block("Body." + i, Neutral), Block("SampleClose." + i, Neutral));
            HidePlot();
        }
        catch
        {
            UnityEngine.Object.Destroy(_root);
            throw;
        }
    }

    public void Render(IReadOnlyList<DailyCandle> candles, decimal currentPrice, int visibleDays = 30, int? currentDay = null)
    {
        if (_disposed || _faulted) return;
        try
        {
            // 只展示真实记录，不为旧档补齐不存在的开、高、低或空缺游戏日。
            var count = visibleDays switch { 7 => 7, 14 => 14, _ => 30 };
            var selected = (candles ?? Array.Empty<DailyCandle>()).Where(c => c != null && c.Close > 0)
                .OrderBy(c => c.Day).TakeLast(count)
                .Select(c => new CandleSample(c.Day, c.Open, c.High, c.Low, c.Close, c.IsPartial)).ToArray();
            var width = _parent.rect.width;
            var height = _parent.rect.height;
            var day = currentDay ?? int.MinValue;
            if (_hasRendered && Math.Abs(width - _previousWidth) < .5f && Math.Abs(height - _previousHeight) < .5f
                && count == _previousDays && day == _previousCurrentDay && currentPrice == _previousPrice && selected.SequenceEqual(_previous)) return;

            HideHover(); _hoverReady = false;

            _title.Text!.text = $"日 K 线 · 最近 {selected.Length} 个有记录的游戏日";
            Place(_title, 8, Math.Max(0, height - 23), Math.Max(80, width - (width < 430 ? 16 : 200)), 22);
            Place(_legend, Math.Max(8, width - 202), Math.Max(0, height - 22), 194, 20);
            Visible(_legend, width >= 430);
            Place(_note, 8, 0, Math.Max(80, width - 16), width < 430 ? 43 : 31);
            if (width < 180 || height < 120)
            {
                HidePlot();
                _note.Text!.text = "图表等待窗口布局完成。";
            }
            else if (selected.Length == 0)
            {
                HidePlot();
                _note.Text!.text = "暂无日行情记录；首次接入只记录可确认的收盘采样，不补造历史 K 线。";
            }
            else Draw(selected, currentPrice, currentDay, width, height);

            _previous = selected; _previousPrice = currentPrice; _previousDays = count;
            _previousCurrentDay = day; _previousWidth = width; _previousHeight = height; _hasRendered = true;
        }
        catch (Exception ex)
        {
            // 原生图形出现异常时停用本图，避免每次轮询继续触发错误；交易面板仍可使用。
            _faulted = true;
            MelonLogger.Warning("[原生日K线] 绘制失败，本窗口不再重试：" + Environment.NewLine + ex);
            try { HidePlot(); _note.Text!.text = "日 K 线暂不可用，请查看加载日志。"; } catch { }
        }
    }

    private void Draw(CandleSample[] candles, decimal currentPrice, int? currentDay, float width, float height)
    {
        const float left = 10, right = 79, top = 31;
        var bottom = width < 430 ? 67f : 55f;
        float plotWidth = width - left - right, plotHeight = height - bottom - top;
        decimal min = candles.Min(c => c.IsComplete ? c.Low!.Value : c.Close);
        decimal max = candles.Max(c => c.IsComplete ? c.High!.Value : c.Close);
        if (currentPrice > 0) { min = Math.Min(min, currentPrice); max = Math.Max(max, currentPrice); }
        decimal range = max - min;
        decimal padding = range > 0 ? range * .08m : Math.Max(max * .015m, .01m);
        min = Math.Max(0, min - padding); max += padding;
        if (max <= min) max = min + .01m;
        float Y(decimal price) => bottom + (float)((price - min) / (max - min)) * plotHeight;

        for (int i = 0; i < _grid.Length; i++)
        {
            var fraction = i / (decimal)(_grid.Length - 1);
            var value = min + (max - min) * fraction;
            var y = bottom + (float)fraction * plotHeight;
            Place(_grid[i], left, y, plotWidth, 1);
            Place(_priceLabels[i], left + plotWidth + 6, y - 9, right - 8, 18);
            _priceLabels[i].Text!.text = Price(value);
            Visible(_grid[i], true);
            Visible(_priceLabels[i], currentPrice <= 0 || Math.Abs(y - Y(currentPrice)) > 12);
        }
        Place(_bottomAxis, left, bottom - 1, plotWidth, 1); Visible(_bottomAxis, true);

        // 槽位依实际记录数量分布，所有周期共用整个绘图区。
        var slotWidth = plotWidth / candles.Length;
        var bodyWidth = Math.Clamp(slotWidth * .56f, 1.5f, 22f);
        for (int i = 0; i < _pool.Length; i++)
        {
            var node = _pool[i];
            if (i >= candles.Length) { node.Hide(); continue; }
            var candle = candles[i];
            var center = left + (i + .5f) * slotWidth;
            if (!candle.IsComplete)
            {
                Visible(node.Wick, false); Visible(node.Body, false);
                node.Sample.Image!.color = Neutral;
                Place(node.Sample, center - Math.Min(bodyWidth, 12) / 2, Y(candle.Close) - 1, Math.Min(bodyWidth, 12), 2);
                Visible(node.Sample, true);
                continue;
            }
            var open = candle.Open!.Value;
            var color = candle.Close > open ? Rising : candle.Close < open ? Falling : Neutral;
            var openY = Y(open); var closeY = Y(candle.Close);
            var lowY = Y(candle.Low!.Value); var highY = Y(candle.High!.Value);
            node.Wick.Image!.color = color; node.Body.Image!.color = color;
            Place(node.Wick, center - .75f, lowY, 1.5f, Math.Max(1, highY - lowY));
            Place(node.Body, center - bodyWidth / 2, Math.Min(openY, closeY) - (openY == closeY ? 1 : 0), bodyWidth, Math.Max(2, Math.Abs(closeY - openY)));
            Visible(node.Sample, false); Visible(node.Wick, true); Visible(node.Body, true);
        }

        var tickCount = Math.Min(_dayLabels.Length, candles.Length);
        for (int i = 0; i < _dayLabels.Length; i++)
        {
            if (i >= tickCount) { Visible(_dayLabels[i], false); continue; }
            var index = tickCount == 1 ? 0 : (int)Math.Round((double)i * (candles.Length - 1) / (tickCount - 1));
            var center = left + (index + .5f) * slotWidth;
            Place(_dayLabels[i], Math.Clamp(center - 25, left, left + plotWidth - 50), bottom - 22, 50, 20);
            _dayLabels[i].Text!.text = "第" + candles[index].Day + "日";
            Visible(_dayLabels[i], true);
        }

        if (currentPrice > 0)
        {
            var y = Y(currentPrice);
            Place(_currentLine, left, y, plotWidth, 1);
            Place(_currentLabel, left + plotWidth + 6, Math.Clamp(y + 1, bottom, bottom + plotHeight - 18), right - 8, 18);
            _currentLabel.Text!.text = Price(currentPrice) + " 现";
            Visible(_currentLine, true); Visible(_currentLabel, true);
        }
        else { Visible(_currentLine, false); Visible(_currentLabel, false); }

        var hasPartial = candles.Any(c => !c.IsComplete);
        var live = currentDay.HasValue && candles.Any(c => c.Day == currentDay.Value);
        var messages = new List<string>();
        if (live) messages.Add("当日未收盘，反映每日结算及事件变价");
        if (hasPartial) messages.Add("灰色收盘标记：旧档或首次接入采样，开/高/低未知");
        if (!live && !hasPartial) messages.Add("实体为开盘至收盘，影线为当日最低至最高");
        var latest = candles[^1];
        _note.Text!.text = $"第 {latest.Day} 日  开 {KnownPrice(latest.Open)}  高 {KnownPrice(latest.High)}  低 {KnownPrice(latest.Low)}  收 {Price(latest.Close)}\n"
            + string.Join("；", messages) + "。";
        _hoverPlot = new HoverRect(left, bottom, plotWidth, plotHeight);
        _hoverReady = true;
    }

    private GraphicNode Block(string name, Color color)
    {
        var go = RectObject(name, _rect);
        var image = go.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
        image.raycastTarget = false; image.color = color; image.type = Image.Type.Simple;
        if (_material != null) image.material = _material;
        return new GraphicNode(go, GetRect(go), image, null);
    }

    private GraphicNode Text(string name, TextAlignmentOptions alignment, float size, Color color)
    {
        var go = RectObject(name, _rect);
        var text = go.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
        text.font = _font; text.fontSize = size; text.color = color; text.alignment = alignment;
        text.raycastTarget = false; text.richText = false; text.enableWordWrapping = false;
        return new GraphicNode(go, GetRect(go), null, text);
    }

    private static GameObject RectObject(string name, Transform parent)
    {
        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() });
        var go = new GameObject("StockMarket.Chart." + name, types);
        go.transform.SetParent(parent, false);
        return go;
    }

    private static RectTransform GetRect(GameObject go) => go.GetComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();

    private static void Place(GraphicNode node, float x, float y, float width, float height)
    {
        node.Rect.anchorMin = Vector2.zero; node.Rect.anchorMax = Vector2.zero; node.Rect.pivot = Vector2.zero;
        node.Rect.anchoredPosition = new Vector2(x, y);
        node.Rect.sizeDelta = new Vector2(Math.Max(0, width), Math.Max(0, height));
    }

    private static void Visible(GraphicNode node, bool visible)
    {
        if (node.Root.activeSelf != visible) node.Root.SetActive(visible);
    }

    private void HidePlot()
    {
        _hoverReady = false; HideHover();
        foreach (var node in _pool) node?.Hide();
        foreach (var node in _grid) if (node != null) Visible(node, false);
        foreach (var node in _priceLabels) if (node != null) Visible(node, false);
        foreach (var node in _dayLabels) if (node != null) Visible(node, false);
        if (_currentLine != null) Visible(_currentLine, false);
        if (_currentLabel != null) Visible(_currentLabel, false);
        if (_bottomAxis != null) Visible(_bottomAxis, false);
    }

    private static string Price(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string KnownPrice(decimal? value) => value.HasValue ? Price(value.Value) : "—";

    public void SetVisible(bool visible)
    {
        if (_disposed) return;
        if (!visible) HideHover();
        if (_root.activeSelf == visible) return;
        _root.SetActive(visible);
        if (visible) { _hasRendered = false; _hoverReady = false; }
    }

    public void SetHoverBounds(RectTransform windowRoot)
    {
        if (_disposed || _hover?.WindowPointer == windowRoot.Pointer) return;
        _hover?.Release(this); _hover = null;
        try { _hover = ChartHoverOverlay.Acquire(windowRoot, _font); }
        catch (Exception ex) { HoverWarning(ex); }
    }

    public void HideHover() => _hover?.Hide(this);

    public void TickHover()
    {
        if (_disposed || _hover == null) return;
        try
        {
            if (!_hoverReady || !_hasRendered || !_root.activeInHierarchy || _previous.Length == 0
                || Math.Abs(_parent.rect.width - _previousWidth) >= .5f || Math.Abs(_parent.rect.height - _previousHeight) >= .5f
                || !_hover.TryPointer(_rect, _hoverPlot, out var point, out var screen, out var camera))
            { HideHover(); return; }
            var index = ChartHoverMath.CandleSlotIndex(point.x, _hoverPlot.X, _hoverPlot.Width, _previous.Length);
            if (index < 0) { HideHover(); return; }
            var candle = _previous[index];
            var text = $"第 {candle.Day} 日" + (candle.Day == _previousCurrentDay ? " · 当日未收盘" : "")
                + $"\n开 {KnownPrice(candle.Open)}    高 {KnownPrice(candle.High)}\n低 {KnownPrice(candle.Low)}    收 {Price(candle.Close)}";
            if (!candle.IsComplete) text += "\n旧档或首次接入记录不完整；未知开高低显示 —。";
            var guideX = (float)(_hoverPlot.X + (index + .5) * _hoverPlot.Width / _previous.Length);
            _hover.Show(this, _rect, _hoverPlot, guideX, text, screen, camera);
        }
        catch (Exception ex) { _hoverReady = false; try { HideHover(); } catch { } HoverWarning(ex); }
    }

    private void HoverWarning(Exception ex)
    {
        if (_hoverWarned) return;
        _hoverWarned = true;
        MelonLogger.Warning("[日K线悬停] 本窗口悬停暂不可用：" + ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _hover?.Release(this); } catch (Exception ex) { HoverWarning(ex); }
        _hover = null;
        // 只销毁本图的对象树；字体与材质属于游戏，不能释放。
        try { UnityEngine.Object.Destroy(_root); }
        catch (Exception ex) { MelonLogger.Warning("[原生日K线] 图表对象清理失败：" + ex); }
    }

    private sealed record CandleSample(int Day, decimal? Open, decimal? High, decimal? Low, decimal Close, bool IsPartial)
    {
        public bool IsComplete => !IsPartial && Open.HasValue && High.HasValue && Low.HasValue && Open.Value > 0 && Low.Value > 0
            && High.Value >= Math.Max(Open.Value, Close) && Low.Value <= Math.Min(Open.Value, Close);
    }

    private sealed record GraphicNode(GameObject Root, RectTransform Rect, Image? Image, TextMeshProUGUI? Text);

    private sealed record CandleNodes(GraphicNode Wick, GraphicNode Body, GraphicNode Sample)
    {
        public void Hide() { Visible(Wick, false); Visible(Body, false); Visible(Sample, false); }
    }
}
