using System;
using ProbablyStolenStockMarket.Localization;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.UI;

// 同一原生窗口的两张图共用一个提示框，按拥有者令牌隐藏，避免另一张图误清当前提示。
internal sealed class ChartHoverOverlay
{
    private static readonly Dictionary<IntPtr, ChartHoverOverlay> Shared = new();
    private readonly RectTransform _window;
    private readonly GameObject _box, _guide;
    private readonly RectTransform _boxRect, _guideRect;
    private readonly TextMeshProUGUI _label;
    private object? _owner;
    private int _references;
    private bool _disposed;
    private string _text = "";
    private float _measuredWidth = -1, _measuredHeight;
    private string _measuredText = "";

    public IntPtr WindowPointer => _window.Pointer;

    public static ChartHoverOverlay Acquire(RectTransform window, TMP_FontAsset font)
    {
        if (Shared.TryGetValue(window.Pointer, out var existing) && !existing._disposed)
        { existing._references++; return existing; }
        var created = new ChartHoverOverlay(window, font) { _references = 1 };
        Shared[window.Pointer] = created;
        return created;
    }

    private ChartHoverOverlay(RectTransform window, TMP_FontAsset font)
    {
        _window = window;
        _box = CreateRect("Tooltip", window);
        _boxRect = RectOf(_box);
        try
        {
            var layout = _box.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
            layout.ignoreLayout = true;
            var background = _box.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            background.color = StockUiTheme.TooltipBackground; background.raycastTarget = false;
            var outline = _box.AddComponent(Il2CppType.Of<Outline>()).Cast<Outline>();
            outline.effectColor = StockUiTheme.Accent;
            outline.effectDistance = new Vector2(1, -1);
            outline.useGraphicAlpha = false;
            var group = _box.AddComponent(Il2CppType.Of<CanvasGroup>()).Cast<CanvasGroup>();
            group.blocksRaycasts = false; group.interactable = false;
            var textObject = CreateRect("Text", _boxRect);
            _label = textObject.AddComponent(Il2CppType.Of<TextMeshProUGUI>()).Cast<TextMeshProUGUI>();
            _label.font = font; _label.fontSize = 14; _label.color = StockUiTheme.Text;
            _label.richText = false; _label.raycastTarget = false; _label.enableWordWrapping = true;
            _label.alignment = TextAlignmentOptions.TopLeft;
            _label.overflowMode = TextOverflowModes.Ellipsis;
            var textRect = RectOf(textObject);
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(9, 7); textRect.offsetMax = new Vector2(-9, -7);
            _guide = CreateRect("Guide", window);
            _guideRect = RectOf(_guide);
            var image = _guide.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
            image.color = StockUiTheme.Accent; image.raycastTarget = false;
            var guideLayout = _guide.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
            guideLayout.ignoreLayout = true;
            _box.SetActive(false); _guide.SetActive(false);
        }
        catch
        {
            if (_guide != null) UnityEngine.Object.Destroy(_guide);
            UnityEngine.Object.Destroy(_box);
            throw;
        }
    }

    public bool TryPointer(RectTransform chart, HoverRect plot, out Vector2 chartPoint, out Vector2 screenPoint, out Camera? camera)
    {
        chartPoint = default; screenPoint = default; camera = null;
        if (_disposed || !Application.isFocused || !chart.gameObject.activeInHierarchy || !_window.gameObject.activeInHierarchy || !TryCamera(chart, out camera)) return false;
        var mouse = Input.mousePosition;
        screenPoint = new Vector2(mouse.x, mouse.y);
        if (!RectTransformUtility.RectangleContainsScreenPoint(_window, screenPoint, camera)
            || !RectTransformUtility.ScreenPointToLocalPointInRectangle(chart, screenPoint, camera, out var local)) return false;
        var bounds = chart.rect;
        chartPoint = new Vector2(local.x - bounds.xMin, local.y - bounds.yMin);
        if (!ChartHoverMath.Contains(plot, chartPoint.x, chartPoint.y)) return false;

        // 检查每一级实际裁切组件；滚动区域移出鼠标后即使对象仍 active，也不能显示旧提示。
        Transform? ancestor = chart;
        while (ancestor != null)
        {
            var go = ancestor.gameObject;
            if (!go.activeInHierarchy) return false;
            var group = Get<CanvasGroup>(go);
            if (group != null && group.alpha <= .001f) return false;
            var rectMask = Get<RectMask2D>(go);
            var mask = Get<Mask>(go);
            if ((rectMask != null && rectMask.enabled) || (mask != null && mask.enabled))
            {
                var clippingRect = Get<RectTransform>(go);
                if (clippingRect == null || !RectTransformUtility.RectangleContainsScreenPoint(clippingRect, screenPoint, camera)) return false;
                if (rectMask != null && rectMask.enabled)
                {
                    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(clippingRect, screenPoint, camera, out var clipPoint)) return false;
                    var clip = clippingRect.rect; var padding = rectMask.padding;
                    if (!ChartHoverMath.Contains(new HoverRect(clip.xMin + padding.x, clip.yMin + padding.y,
                        clip.width - padding.x - padding.z, clip.height - padding.y - padding.w), clipPoint.x, clipPoint.y)) return false;
                }
            }
            ancestor = ancestor.parent;
        }
        return true;
    }

    public void Show(object owner, RectTransform chart, HoverRect plot, float guideX, string text, Vector2 screenPoint, Camera? camera)
    {
        if (_disposed || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_window, screenPoint, camera, out var anchor))
        { Hide(owner); return; }
        var rect = _window.rect;
        if (rect.width < 30 || rect.height < 30) { Hide(owner); return; }
        text = Localizer.Text(text);
        if (_text != text) { _label.text = text; _text = text; }
        if (!VisibleWindowBounds(camera, out var visibleBounds)) { Hide(owner); return; }
        if (visibleBounds.Width < 40 || visibleBounds.Height < 30) { Hide(owner); return; }
        // 先确定屏幕与窗口交集允许的真实宽度，再测量折行高度，不能先按宽框测高后缩成窄框。
        var available = ChartHoverMath.FitTooltip(new HoverPoint(anchor.x, anchor.y), 310, 1, visibleBounds);
        var preferredWidth = (float)available.Width;
        if (_measuredText != text || Math.Abs(_measuredWidth - preferredWidth) > .1f)
        {
            _measuredHeight = _label.GetPreferredValues(text, Math.Max(1, preferredWidth - 18), float.PositiveInfinity).y + 16;
            _measuredText = text; _measuredWidth = preferredWidth;
        }
        var fitted = ChartHoverMath.FitTooltip(new HoverPoint(anchor.x, anchor.y), preferredWidth, Math.Max(40, _measuredHeight), visibleBounds);
        Place(_boxRect, (float)(fitted.X - rect.xMin), (float)(fitted.Y - rect.yMin), (float)fitted.Width, (float)fitted.Height);
        if (_guideRect.parent?.Pointer != chart.Pointer) _guideRect.SetParent(chart, false);
        Place(_guideRect, guideX - .5f, (float)plot.Y, 1, (float)plot.Height);
        _guideRect.SetAsLastSibling();
        _boxRect.SetAsLastSibling();
        _owner = owner;
        if (!_guide.activeSelf) _guide.SetActive(true);
        if (!_box.activeSelf) _box.SetActive(true);
    }

    public void Hide(object owner)
    {
        if (_disposed || !ReferenceEquals(_owner, owner)) return;
        _owner = null;
        _box.SetActive(false); _guide.SetActive(false);
        // 图表可能先于另一个持有者销毁，隐藏时把共享引导线归还窗口，不能留在将销毁的图下。
        if (_guideRect.parent?.Pointer != _window.Pointer) _guideRect.SetParent(_window, false);
    }

    public void Release(object owner)
    {
        if (_disposed) return;
        try { Hide(owner); } catch { }
        if (--_references > 0) return;
        _disposed = true;
        Shared.Remove(_window.Pointer);
        // 引导线可能已挂到图表下，必须先于该图销毁；从不释放游戏字体和材质。
        try { UnityEngine.Object.Destroy(_guide); } catch { }
        try { UnityEngine.Object.Destroy(_box); } catch { }
    }

    private bool VisibleWindowBounds(Camera? camera, out HoverRect result)
    {
        var window = _window.rect;
        var canvas = _window.gameObject.GetComponentInParent(Il2CppType.Of<Canvas>(), true)?.TryCast<Canvas>();
        var screen = canvas?.rootCanvas?.pixelRect ?? new Rect(0, 0, Screen.width, Screen.height);
        result = default;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_window, new Vector2(screen.xMin, screen.yMin), camera, out var low)
            || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_window, new Vector2(screen.xMax, screen.yMax), camera, out var high)) return false;
        var screenBounds = new HoverRect(Math.Min(low.x, high.x), Math.Min(low.y, high.y), Math.Abs(high.x - low.x), Math.Abs(high.y - low.y));
        return ChartHoverMath.TryIntersect(new HoverRect(window.xMin, window.yMin, window.width, window.height), screenBounds, out result);
    }

    private static bool TryCamera(RectTransform rect, out Camera? camera)
    {
        camera = null;
        var canvas = rect.gameObject.GetComponentInParent(Il2CppType.Of<Canvas>(), true)?.TryCast<Canvas>();
        if (canvas == null || !canvas.enabled) return false;
        canvas = canvas.rootCanvas ?? canvas;
        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return true;
        camera = canvas.worldCamera;
        if (camera == null) camera = Get<GraphicRaycaster>(canvas.gameObject)?.eventCamera;
        return camera != null;
    }

    private static GameObject CreateRect(string name, Transform parent)
    {
        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() });
        var go = new GameObject("StockMarket.ChartHover." + name, types);
        go.transform.SetParent(parent, false); return go;
    }
    private static T? Get<T>(GameObject go) where T : Component => go.GetComponent(Il2CppType.Of<T>())?.TryCast<T>();
    private static RectTransform RectOf(GameObject go) => go.GetComponent(Il2CppType.Of<RectTransform>()).Cast<RectTransform>();
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.zero; rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(width, height);
    }
}
