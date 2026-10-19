using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using ProbablyStolenStockMarket.Market;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.UI;

// 横轴只表示真实采样顺序，不把游戏内的交易或事件伪装成分钟行情。
public sealed class NativeIntradayChart : IDisposable
{
    private static readonly Color Ink = StockUiTheme.Text;
    private static readonly Color Neutral = StockUiTheme.Neutral;
    private static readonly Color CurveColor = StockUiTheme.Accent;
    private static readonly Color CustomerColor = StockUiTheme.Warning;
    private static readonly Color EventColor = StockUiTheme.Event;
    private static readonly Color GridColor = StockUiTheme.Grid;
    private readonly RectTransform _parent;
    private readonly TMP_FontAsset _font;
    private readonly GameObject _root;
    private readonly RectTransform _rect;
    private readonly List<Node> _segments = new();
    private readonly List<Node> _markers = new();
    private readonly Node[] _grid = new Node[5], _priceLabels = new Node[5], _xLabels = new Node[5];
    private readonly Node _title, _legend, _summary, _note, _currentLabel, _currentLine;
    private Sample[] _previous = Array.Empty<Sample>();
    private decimal _previousPrice;
    private int _previousDay = int.MinValue;
    private bool _previousPartial, _hasRendered, _disposed, _faulted;
    private float _previousWidth = -1, _previousHeight = -1;
    private ChartHoverOverlay? _hover;
    private HoverRect _hoverPlot;
    private bool _hoverReady, _hoverWarned;

    public NativeIntradayChart(RectTransform parent, TMP_FontAsset font)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _font = font ?? throw new ArgumentNullException(nameof(font));
        _root = RectObject("Intraday", parent);
        _rect = RectOf(_root);
        _rect.anchorMin = Vector2.zero; _rect.anchorMax = Vector2.one;
        _rect.offsetMin = Vector2.zero; _rect.offsetMax = Vector2.zero;
        try
        {
            var background = _root.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            background.color = StockUiTheme.ChartBackground;
            background.raycastTarget = false;
            _title = Text("Title", 13, TextAlignmentOptions.TopLeft, Ink);
            _legend = Text("Legend", 11, TextAlignmentOptions.TopRight, Ink);
            _legend.Text!.text = "青：走势/日结  橙：成交记录  紫：事件/异动";
            _summary = Text("Summary", 11, TextAlignmentOptions.TopLeft, Ink);
            _note = Text("Note", 10, TextAlignmentOptions.TopLeft, StockUiTheme.Muted);
            _note.Text!.enableWordWrapping = true;
            _currentLine = Block("CurrentLine", CurveColor);
            _currentLabel = Text("CurrentPrice", 11, TextAlignmentOptions.MidlineLeft, CurveColor);
            for (int i = 0; i < _grid.Length; i++)
            {
                _grid[i] = Block("Grid." + i, GridColor);
                _priceLabels[i] = Text("Price." + i, 11, TextAlignmentOptions.MidlineLeft, StockUiTheme.Muted);
                _xLabels[i] = Text("Sequence." + i, 11, TextAlignmentOptions.Midline, StockUiTheme.Muted);
            }
            EnsurePool(32);
            HidePlot();
        }
        catch
        {
            UnityEngine.Object.Destroy(_root);
            throw;
        }
    }

    public void SetVisible(bool visible)
    {
        if (_disposed) return;
        if (!visible) HideHover();
        if (_root.activeSelf == visible) return;
        _root.SetActive(visible);
        if (visible) { _hasRendered = false; _hoverReady = false; }
    }

    public void Render(IReadOnlyList<PricePoint> points, decimal currentPrice, int currentDay, bool isPartial)
    {
        if (_disposed || _faulted) return;
        try
        {
            // 每个已保存样本都保留，包括同价成交；不排序、不抽样、不限取末尾记录。
            var samples = (points ?? Array.Empty<PricePoint>()).Select(p => new Sample(p.Day, p.Price, p.Reason ?? "")).ToArray();
            var width = _parent.rect.width; var height = _parent.rect.height;
            if (_hasRendered && currentPrice == _previousPrice && currentDay == _previousDay && isPartial == _previousPartial
                && Math.Abs(width - _previousWidth) < .5f && Math.Abs(height - _previousHeight) < .5f && samples.SequenceEqual(_previous)) return;

            HideHover(); _hoverReady = false;

            _title.Text!.text = $"当日分时 · 第 {currentDay} 日 · 按变价顺序（{samples.Length} 条）";
            Place(_title, 8, Math.Max(0, height - 22), Math.Max(80, width - (width >= 540 ? 250 : 16)), 21);
            Place(_legend, Math.Max(8, width - 250), Math.Max(0, height - 22), 242, 20);
            Visible(_legend, width >= 540);
            Place(_summary, 8, Math.Max(0, height - 43), Math.Max(80, width - 16), 20);
            Place(_note, 8, 0, Math.Max(80, width - 16), width < 430 ? 47 : 38);

            if (width < 180 || height < 140)
            {
                HidePlot();
                _summary.Text!.text = "现价 " + Price(currentPrice);
                _note.Text!.text = "分时图等待窗口布局完成。";
            }
            else if (samples.Length == 0)
            {
                HidePlot();
                _summary.Text!.text = "现价 " + Price(currentPrice) + "    日内高低 —";
                _note.Text!.text = "当前日暂无可确认的采样，等待实际行情更新；不会模拟缺失走势。";
            }
            else if (samples.Any(p => p.Day != currentDay || p.Price <= 0))
            {
                HidePlot();
                _summary.Text!.text = "现价 " + Price(currentPrice);
                _note.Text!.text = "样本日期或价格与当前日不符，等待行情同步；没有丢弃部分记录继续绘制。";
            }
            else Draw(samples, currentPrice, isPartial, width, height);

            _previous = samples; _previousPrice = currentPrice; _previousDay = currentDay; _previousPartial = isPartial;
            _previousWidth = width; _previousHeight = height; _hasRendered = true;
        }
        catch (Exception ex)
        {
            _faulted = true;
            MelonLogger.Warning("[原生分时图] 绘制失败，本窗口不再重试：" + Environment.NewLine + ex);
            try { HidePlot(); _note.Text!.text = "分时图暂不可用，请查看加载日志。"; } catch { }
        }
    }

    private void Draw(Sample[] samples, decimal currentPrice, bool isPartial, float width, float height)
    {
        EnsurePool(samples.Length);
        const float left = 10, right = 79, top = 52;
        var bottom = width < 430 ? 73f : 64f;
        var plotWidth = width - left - right; var plotHeight = height - top - bottom;
        var recordedLow = samples.Min(p => p.Price); var recordedHigh = samples.Max(p => p.Price);
        var price = currentPrice > 0 ? currentPrice : samples[^1].Price;
        var min = Math.Min(recordedLow, price); var max = Math.Max(recordedHigh, price);
        var range = max - min;
        var padding = range > 0 ? range * .08m : Math.Max(max * .015m, .01m);
        min = Math.Max(0, min - padding); max += padding;
        if (max <= min) max = min + .01m;
        float X(int index) => left + (samples.Length == 1 ? plotWidth / 2 : (float)index / (samples.Length - 1) * plotWidth);
        float Y(decimal value) => bottom + (float)((value - min) / (max - min)) * plotHeight;

        var baseline = samples[0].Price;
        var movement = (price / baseline - 1) * 100;
        _summary.Text!.text = $"现价 {Price(price)}    较{(isPartial ? "首采样" : "开盘")} {(movement > 0 ? "+" : "")}{movement:0.00}%    {(isPartial ? "已记录" : "日内")}高 {Price(recordedHigh)} / 低 {Price(recordedLow)}";
        for (int i = 0; i < _grid.Length; i++)
        {
            var fraction = i / (decimal)(_grid.Length - 1);
            var value = min + (max - min) * fraction;
            var y = bottom + (float)fraction * plotHeight;
            Place(_grid[i], left, y, plotWidth, 1); Visible(_grid[i], true);
            Place(_priceLabels[i], left + plotWidth + 6, y - 9, right - 8, 18);
            _priceLabels[i].Text!.text = Price(value);
            Visible(_priceLabels[i], Math.Abs(y - Y(price)) > 12);
        }

        for (int i = 0; i < _markers.Count; i++)
        {
            if (i >= samples.Length)
            { Visible(_markers[i], false); Visible(_segments[i], false); continue; }
            var x = X(i); var y = Y(samples[i].Price);
            var initialBaseline = i == 0 && !isPartial;
            var kind = initialBaseline ? 0 : PointKind(samples[i].Reason);
            var color = initialBaseline ? CurveColor : kind == 1 ? CustomerColor : kind == 2 ? EventColor : Neutral;
            _markers[i].Image!.color = color;
            var diameter = kind > 0 ? 4f : 2.8f;
            Place(_markers[i], x - diameter / 2, y - diameter / 2, diameter, diameter);
            Visible(_markers[i], true);
            if (i == 0) { Visible(_segments[i], false); continue; }
            var startX = X(i - 1); var startY = Y(samples[i - 1].Price);
            var dx = x - startX; var dy = y - startY;
            var segment = _segments[i];
            segment.Rect.anchorMin = Vector2.zero; segment.Rect.anchorMax = Vector2.zero;
            segment.Rect.pivot = new Vector2(0, .5f);
            segment.Rect.anchoredPosition = new Vector2(startX, startY);
            segment.Rect.sizeDelta = new Vector2((float)Math.Sqrt(dx * dx + dy * dy), 1.5f);
            // 当前代理的 Transform.localEulerAngles setter 是原生绑定，避开 Quaternion 工具重载。
            segment.Rect.localEulerAngles = new Vector3(0, 0, (float)(Math.Atan2(dy, dx) * 180 / Math.PI));
            Visible(segment, true);
        }

        var tickCount = Math.Min(_xLabels.Length, samples.Length);
        for (int i = 0; i < _xLabels.Length; i++)
        {
            if (i >= tickCount) { Visible(_xLabels[i], false); continue; }
            var index = tickCount == 1 ? 0 : (int)Math.Round((double)i * (samples.Length - 1) / (tickCount - 1));
            var text = index == 0 ? (isPartial ? "最早" : "开盘") : index == samples.Length - 1 ? "最新" : "采样";
            if (samples.Length == 1) text += "/最新";
            _xLabels[i].Text!.text = text + " #" + (index + 1);
            Place(_xLabels[i], Math.Clamp(X(index) - 39, left, left + plotWidth - 78), bottom - 22, 78, 20);
            Visible(_xLabels[i], true);
        }

        var currentY = Y(price);
        Place(_currentLine, left, currentY, plotWidth, 1); Visible(_currentLine, true);
        Place(_currentLabel, left + plotWidth + 6, Math.Clamp(currentY + 1, bottom, bottom + plotHeight - 18), right - 8, 18);
        _currentLabel.Text!.text = Price(price) + " 现"; Visible(_currentLabel, true);
        var latestReason = SafeReason(samples[^1].Reason);
        _note.Text!.text = "最新原因：" + (latestReason.Length == 0 ? "行情采样" : latestReason)
            + "\n" + (isPartial ? "当前日采样不完整，接入前走势、真实开盘及全日极值未知；" : "")
            + (samples.Length == 1 ? "当前仅有一条真实样本，等待实际变化；" : "") + "横轴为采样顺序，不代表真实时分。";
        _hoverPlot = new HoverRect(left, bottom, plotWidth, plotHeight);
        _hoverReady = true;
    }

    private void EnsurePool(int required)
    {
        if (_markers.Count >= required) return;
        var capacity = Math.Max(required, Math.Max(32, checked(_markers.Count * 2)));
        // 按实际保存数量扩容，不设静默截断上限，不用聚合替换高低点。
        while (_markers.Count < capacity)
        {
            var index = _markers.Count;
            _segments.Add(Block("Segment." + index, CurveColor));
            _markers.Add(Block("Point." + index, Neutral));
            Visible(_segments[index], false); Visible(_markers[index], false);
        }
    }

    private Node Block(string name, Color color)
    {
        var go = RectObject(name, _rect);
        var image = go.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
        image.color = color; image.raycastTarget = false; image.type = Image.Type.Simple;
        // 保持默认 UI Image 材质，不能借用字体 SDF 材质绘制色块。
        return new Node(go, RectOf(go), image, null);
    }

    private Node Text(string name, float size, TextAlignmentOptions alignment, Color color)
    {
        var go = RectObject(name, _rect);
        var text = go.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
        text.font = _font; text.fontSize = size; text.color = color; text.alignment = alignment;
        text.raycastTarget = false; text.richText = false; text.enableWordWrapping = false;
        return new Node(go, RectOf(go), null, text);
    }

    private static GameObject RectObject(string name, Transform parent)
    {
        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() });
        var go = new GameObject("StockMarket.Intraday." + name, types);
        go.transform.SetParent(parent, false); return go;
    }

    private static RectTransform RectOf(GameObject go) => go.GetComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();

    private static void Place(Node node, float x, float y, float width, float height)
    {
        node.Rect.anchorMin = Vector2.zero; node.Rect.anchorMax = Vector2.zero; node.Rect.pivot = Vector2.zero;
        node.Rect.anchoredPosition = new Vector2(x, y); node.Rect.sizeDelta = new Vector2(Math.Max(0, width), Math.Max(0, height));
    }

    private static void Visible(Node node, bool visible)
    {
        if (node.Root.activeSelf != visible) node.Root.SetActive(visible);
    }

    private void HidePlot()
    {
        _hoverReady = false; HideHover();
        foreach (var node in _segments) Visible(node, false);
        foreach (var node in _markers) Visible(node, false);
        foreach (var node in _grid) if (node != null) Visible(node, false);
        foreach (var node in _priceLabels) if (node != null) Visible(node, false);
        foreach (var node in _xLabels) if (node != null) Visible(node, false);
        if (_currentLabel != null) Visible(_currentLabel, false);
        if (_currentLine != null) Visible(_currentLine, false);
    }

    private static int PointKind(string reason)
    {
        if (reason.Contains("昨日成交", StringComparison.Ordinal) || reason.Contains("成交汇总", StringComparison.Ordinal)) return 0;
        if (reason.Contains("顾客", StringComparison.Ordinal)) return 1;
        if (reason.Length == 0 || reason.Contains("开盘", StringComparison.Ordinal) || reason.Contains("每日", StringComparison.Ordinal)
            || reason.Contains("采样", StringComparison.Ordinal) || reason.Contains("接入", StringComparison.Ordinal)) return 0;
        return 2;
    }

    private static string SafeReason(string reason)
    {
        // 来源已由领域层过滤隐藏事件；这里只清理控制字符，TMP 富文本另行禁用。
        var safe = new string(reason.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return safe.Length > 100 ? safe.Substring(0, 100) + "…" : safe;
    }

    private static string Price(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

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
            var index = ChartHoverMath.NearestSampleIndex(point.x, _hoverPlot.X, _hoverPlot.Width, _previous.Length);
            if (index < 0) { HideHover(); return; }
            var sample = _previous[index];
            var reason = SafeReason(sample.Reason);
            var text = $"第 {sample.Day} 日 · 采样 #{index + 1} / {_previous.Length}\n当时价格 {Price(sample.Price)}\n原因："
                + (reason.Length == 0 ? "行情采样" : reason);
            if (_previousPartial) text += "\n当日记录不完整；序号仅代表已保存采样顺序。";
            var guideX = (float)(_hoverPlot.X + (_previous.Length == 1 ? _hoverPlot.Width / 2 : (double)index / (_previous.Length - 1) * _hoverPlot.Width));
            _hover.Show(this, _rect, _hoverPlot, guideX, text, screen, camera);
        }
        catch (Exception ex) { _hoverReady = false; try { HideHover(); } catch { } HoverWarning(ex); }
    }

    private void HoverWarning(Exception ex)
    {
        if (_hoverWarned) return;
        _hoverWarned = true;
        MelonLogger.Warning("[分时悬停] 本窗口悬停暂不可用：" + ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _hover?.Release(this); } catch (Exception ex) { HoverWarning(ex); }
        _hover = null;
        try { UnityEngine.Object.Destroy(_root); }
        catch (Exception ex) { MelonLogger.Warning("[原生分时图] 清理对象失败：" + ex); }
        // 字体为游戏已有资源，本图只释放自己的 UGUI 对象树。
    }

    private sealed record Sample(int Day, decimal Price, string Reason);
    private sealed record Node(GameObject Root, RectTransform Rect, Image? Image, TextMeshProUGUI? Text);
}
