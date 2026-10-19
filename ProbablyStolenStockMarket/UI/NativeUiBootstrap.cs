using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.UI;

// 只复制游戏素材引用，新建纯 UI 对象，不克隆背包或交易对象及其业务监听器。
internal sealed class NativeUiBootstrap : IDisposable
{
    private readonly MelonLogger.Instance? _logger;
    private readonly List<(string Field, GameObject? Original, GameObject Assigned)> _assigned = new();
    private CustomUIManager? _manager;
    private GameObject? _templates;
    private GameObject? _ownedManager;
    private GameObject? _ownedEvents;
    private TMP_FontAsset? _font;
    private Material? _fontMaterial;
    private ImageSkin? _windowSkin;
    private ImageSkin? _buttonSkin;
    private bool _disposed;

    public NativeUiBootstrap(MelonLogger.Instance? logger = null) => _logger = logger;

    public CustomUIManager Resolve()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeUiBootstrap));
        string stage = "existing manager";
        try
        {
            var manager = CustomUIManager.Instance;
            if (manager != null && Validate(manager, out _))
            { _manager = manager; EnsureEventSystem(); return manager; }

            stage = "loaded native prefab references";
            var sources = Loaded<CustomUIManager>().Where(m => m != null && (manager == null || m.Pointer != manager.Pointer)).ToList();
            // 资源中的 manager 只作预制件引用来源，不克隆可能携带其他逻辑的 GameObject。
            if (manager == null)
            {
                _ownedManager = new GameObject("StockMarket.OwnedCustomUIManager");
                _ownedManager.SetActive(false);
                manager = Add<CustomUIManager>(_ownedManager);
            }
            _manager = manager;
            foreach (var source in sources)
                foreach (string field in RequiredFields)
                    if (GetPrefab(manager, field) == null && GetPrefab(source, field) is { } prefab)
                    { Assign(manager, field, prefab); Log($"stage=borrow prefab field={field} source={source.gameObject.name}/{prefab.name}"); }

            if (RequiredFields.Any(field => GetPrefab(manager, field) == null))
            {
                stage = "native skin resources";
                ResolveSkin();
                _templates = new GameObject("StockMarket.OwnedInactiveTemplates");
                _templates.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_templates);
                stage = "owned UI contracts";
                if (manager.windowPrefab == null) Assign(manager, "windowPrefab", WindowTemplate());
                if (manager.labelPrefab == null) Assign(manager, "labelPrefab", LabelTemplate());
                if (manager.buttonPrefab == null) Assign(manager, "buttonPrefab", ButtonTemplate("Button"));
                if (manager.inputFieldPrefab == null) Assign(manager, "inputFieldPrefab", InputTemplate());
                if (manager.scrollViewPrefab == null) Assign(manager, "scrollViewPrefab", ScrollTemplate());
            }
            stage = "prefab contract validation";
            if (!Validate(manager, out string detail)) throw new InvalidOperationException(detail);
            if (_ownedManager != null && !_ownedManager.activeSelf)
            {
                stage = "native manager Awake";
                _ownedManager.SetActive(true);
                if (CustomUIManager.Instance == null || CustomUIManager.Instance.Pointer != manager.Pointer)
                    throw new InvalidOperationException("原生 Awake 未登记所创建的管理器。");
            }
            stage = "native overlay canvas";
            manager.EnsureOverlayCanvas();
            if (manager.OverlayRoot() == null) throw new InvalidOperationException("原生 OverlayRoot 返回空。");
            stage = "input event system";
            EnsureEventSystem();
            Log("stage=contracts ready window(frame/content/title/close/drag), label(TMP), button(Image/Button/TMP), input(viewport/text/placeholder), scroll(viewport/content) 已逐项通过；实际交互仍需游戏验证。");
            return manager;
        }
        catch (Exception ex)
        {
            Cleanup();
            throw new InvalidOperationException("原生股市 UI 初始化失败 stage=" + stage + "：" + ex.Message, ex);
        }
    }

    private static readonly string[] RequiredFields = { "windowPrefab", "labelPrefab", "buttonPrefab", "inputFieldPrefab", "scrollViewPrefab" };
    private static GameObject? GetPrefab(CustomUIManager manager, string field) => field switch
    {
        "windowPrefab" => manager.windowPrefab, "labelPrefab" => manager.labelPrefab, "buttonPrefab" => manager.buttonPrefab,
        "inputFieldPrefab" => manager.inputFieldPrefab, "scrollViewPrefab" => manager.scrollViewPrefab, _ => null
    };
    private static void SetPrefab(CustomUIManager manager, string field, GameObject? value)
    {
        switch (field)
        {
            case "windowPrefab": manager.windowPrefab = value!; break;
            case "labelPrefab": manager.labelPrefab = value!; break;
            case "buttonPrefab": manager.buttonPrefab = value!; break;
            case "inputFieldPrefab": manager.inputFieldPrefab = value!; break;
            case "scrollViewPrefab": manager.scrollViewPrefab = value!; break;
        }
    }
    private void Assign(CustomUIManager manager, string field, GameObject template)
    {
        var original = GetPrefab(manager, field);
        if (original != null) return;
        SetPrefab(manager, field, template); _assigned.Add((field, original, template));
    }

    private void ResolveSkin()
    {
        // 背景只取原生窗口的背景节点，不能将商品图标或默认白块冒充窗口素材。
        var window = EmporiumEntry.Instance?.invWindow;
        var image = window?.background?.image;
        if (image?.sprite == null)
            image = Loaded<TreeNodeRender>().Select(node => node.window?.background?.image).FirstOrDefault(i => i != null && i.sprite != null);
        if (image?.sprite == null) throw new InvalidOperationException("尚未找到已加载原版窗口背景素材。");
        _windowSkin = Skin(image, "PixelWindow.background");
        var nativeButton = Loaded<Button>().Where(b => b != null && b.image != null && b.image.sprite != null && !b.gameObject.name.StartsWith("StockMarket.", StringComparison.Ordinal))
            .OrderByDescending(b => b.gameObject.activeInHierarchy).FirstOrDefault();
        _buttonSkin = nativeButton?.image?.sprite != null ? Skin(nativeButton.image, "native Button " + nativeButton.gameObject.name) : _windowSkin;
        var text = Loaded<TextMeshProUGUI>().Where(t => t != null && t.font != null && !t.gameObject.name.StartsWith("StockMarket.", StringComparison.Ordinal))
            .OrderByDescending(t => t.gameObject.activeInHierarchy).FirstOrDefault();
        _font = text?.font;
        _fontMaterial = text?.fontSharedMaterial;
        if (_font == null) throw new InvalidOperationException("尚未找到游戏已加载的 TMP 字体资源。");
        Log($"stage=borrow font source={text!.gameObject.name} font={_font.name} material={_fontMaterial?.name}");
    }
    private ImageSkin Skin(Image image, string source)
    {
        Log($"stage=borrow skin source={source}/{image.gameObject.name} sprite={image.sprite.name} texture={image.sprite.texture?.name} material={image.material?.name}");
        return new ImageSkin(image.sprite, image.material, image.type, image.color);
    }

    private GameObject RectObject(string name, Transform? parent)
    {
        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() });
        var go = new GameObject("StockMarket." + name, types);
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }
    private GameObject Template(string name)
    {
        var go = RectObject(name, _templates!.transform);
        // 父级 inactive，模板自身 activeSelf 为 true，原生 Instantiate 后才会可见。
        go.SetActive(true); return go;
    }
    private static T Add<T>(GameObject go) where T : Component => go.AddComponent(Il2CppType.Of<T>()).Cast<T>();
    private static T? Get<T>(GameObject? go) where T : Component => go?.GetComponent(Il2CppType.Of<T>())?.TryCast<T>();
    private static T? Child<T>(GameObject? go) where T : Component => go?.GetComponentInChildren(Il2CppType.Of<T>(), true)?.TryCast<T>();
    private static List<T> Loaded<T>() where T : UnityEngine.Object
    {
        var result = new List<T>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
            if (obj != null && obj.TryCast<T>() is { } found) result.Add(found);
        return result;
    }
    private static RectTransform Rect(GameObject go) => go.transform.Cast<RectTransform>();
    private static void Stretch(RectTransform rect, float left = 0, float bottom = 0, float right = 0, float top = 0)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top);
    }
    private Image Image(GameObject go, ImageSkin skin, bool raycast)
    {
        var image = Add<Image>(go); image.sprite = skin.Sprite; image.material = skin.Material!;
        image.type = skin.Type; image.color = skin.Color; image.raycastTarget = raycast; return image;
    }
    private TextMeshProUGUI Text(GameObject go, string value, float size = 18)
    {
        var text = Add<TextMeshProUGUI>(go); text.font = _font!;
        if (_fontMaterial != null) text.fontSharedMaterial = _fontMaterial;
        text.text = value; text.fontSize = size; text.color = new Color(.93f, .87f, .73f, 1);
        text.enableWordWrapping = true; text.raycastTarget = false; text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }
    private static void Size(GameObject go, float width, float height)
    {
        Rect(go).sizeDelta = new Vector2(width, height);
        var layout = Add<LayoutElement>(go); layout.preferredWidth = width; layout.preferredHeight = height; layout.minHeight = height;
    }
    private static void Vertical(GameObject go)
    {
        var layout = Add<VerticalLayoutGroup>(go); layout.spacing = 4;
        layout.childControlWidth = true; layout.childControlHeight = true;
        layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
    }
    private GameObject LabelTemplate()
    {
        var go = Template("Label"); Text(go, "", 18); Size(go, 700, 32); return go;
    }
    private GameObject ButtonTemplate(string name, Transform? parent = null)
    {
        var go = parent == null ? Template(name) : RectObject(name, parent);
        var image = Image(go, _buttonSkin!, true); var button = Add<Button>(go);
        button.targetGraphic = image; button.navigation = new Navigation { mode = Navigation.Mode.None };
        var label = RectObject(name + ".Text", go.transform); Stretch(Rect(label), 8, 3, 8, 3);
        var text = Text(label, name); text.alignment = TextAlignmentOptions.Center;
        Size(go, 160, 36); return go;
    }
    private GameObject WindowTemplate()
    {
        var go = Template("Window"); Image(go, _windowSkin!, true);
        Add<CanvasGroup>(go); var frame = Add<CustomUIWindowFrame>(go);
        Rect(go).sizeDelta = new Vector2(1040, 750);
        var title = RectObject("Window.Title", go.transform);
        var titleRect = Rect(title); titleRect.anchorMin = new Vector2(0, 1); titleRect.anchorMax = new Vector2(1, 1);
        titleRect.pivot = new Vector2(.5f, 1); titleRect.offsetMin = new Vector2(14, -42); titleRect.offsetMax = new Vector2(-50, -6);
        frame.titleText = Text(title, "股市投资", 22);
        var close = ButtonTemplate("Window.Close", go.transform);
        var closeRect = Rect(close); closeRect.anchorMin = closeRect.anchorMax = new Vector2(1, 1);
        closeRect.pivot = new Vector2(1, 1); closeRect.anchoredPosition = new Vector2(-8, -8); closeRect.sizeDelta = new Vector2(32, 30);
        Child<TextMeshProUGUI>(close)!.text = "×";
        frame.closeButton = Get<Button>(close)!;
        var content = RectObject("Window.Content", go.transform); Stretch(Rect(content), 14, 12, 14, 48); Vertical(content);
        frame.contentRoot = Rect(content);
        frame.dragHandle = Add<UIDragHandler>(go);
        frame.dragHandle.ignoreTransparent = true;
        return go;
    }
    private GameObject InputTemplate()
    {
        var go = Template("Input"); var image = Image(go, _buttonSkin!, true);
        var input = Add<TMP_InputField>(go);
        var viewport = RectObject("Input.Viewport", go.transform); Stretch(Rect(viewport), 8, 4, 8, 4); Add<RectMask2D>(viewport);
        var textGo = RectObject("Input.Text", viewport.transform); Stretch(Rect(textGo));
        var placeholderGo = RectObject("Input.Placeholder", viewport.transform); Stretch(Rect(placeholderGo));
        var text = Text(textGo, "1"); var placeholder = Text(placeholderGo, "正整数股数"); placeholder.color = new Color(.7f, .7f, .65f, .6f);
        input.textViewport = Rect(viewport); input.textComponent = text; input.placeholder = placeholder;
        input.targetGraphic = image; input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.lineType = TMP_InputField.LineType.SingleLine; input.characterLimit = 9;
        input.navigation = new Navigation { mode = Navigation.Mode.None }; input.text = "1";
        Size(go, 150, 36); return go;
    }
    private GameObject ScrollTemplate()
    {
        var go = Template("Scroll"); Image(go, _windowSkin!, true); var scroll = Add<ScrollRect>(go);
        var viewport = RectObject("Scroll.Viewport", go.transform); Stretch(Rect(viewport), 4, 4, 4, 4); Add<RectMask2D>(viewport);
        var content = RectObject("Scroll.Content", viewport.transform);
        var rect = Rect(content); rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f, 1); rect.sizeDelta = Vector2.zero;
        Vertical(content); var fitter = Add<ContentSizeFitter>(content); fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained; fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = Rect(viewport); scroll.content = rect; scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 28;
        Size(go, 900, 360); return go;
    }

    private static bool Validate(CustomUIManager manager, out string detail)
    {
        var frame = Get<CustomUIWindowFrame>(manager.windowPrefab);
        var label = Child<TextMeshProUGUI>(manager.labelPrefab);
        var button = Child<Button>(manager.buttonPrefab);
        var input = Child<TMP_InputField>(manager.inputFieldPrefab);
        var scroll = Child<ScrollRect>(manager.scrollViewPrefab);
        var errors = new List<string>();
        if (frame == null || frame.contentRoot == null || frame.titleText == null || frame.closeButton == null || frame.dragHandle == null) errors.Add("window/frame 引用不完整");
        if (label == null || label.font == null) errors.Add("label/TMP 字体不完整");
        if (button == null || button.targetGraphic == null || Child<TextMeshProUGUI>(manager.buttonPrefab) == null) errors.Add("button/Image/TMP 不完整");
        if (input == null || input.textViewport == null || input.textComponent == null || input.placeholder == null) errors.Add("input/viewport/text/placeholder 不完整");
        if (scroll == null || scroll.viewport == null || scroll.content == null) errors.Add("scroll/viewport/content 不完整");
        foreach (string field in RequiredFields) if (GetPrefab(manager, field) is { } prefab && !prefab.activeSelf) errors.Add(field + " activeSelf=false");
        detail = string.Join("；", errors); return errors.Count == 0;
    }
    private void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        _ownedEvents = new GameObject("StockMarket.OwnedEventSystem"); _ownedEvents.SetActive(false);
        Add<EventSystem>(_ownedEvents); Add<StandaloneInputModule>(_ownedEvents);
        UnityEngine.Object.DontDestroyOnLoad(_ownedEvents); _ownedEvents.SetActive(true);
        if (EventSystem.current == null) throw new InvalidOperationException("自有 EventSystem 未激活。");
        Log("stage=input：原场景没有 EventSystem，已创建独立 EventSystem 与 StandaloneInputModule。");
    }
    private void Log(string text) { if (_logger != null) _logger.Msg("[原生股市引导] " + text); else MelonLogger.Msg("[原生股市引导] " + text); }
    private void Cleanup()
    {
        if (_manager != null)
        {
            try
            {
                bool shared = false;
                if (_manager.windowOrder != null)
                    foreach (var window in _manager.windowOrder)
                        if (window != null && window.id != "ProbablyStolenStockMarket.stock_market.native" && window.IsAlive) shared = true;
                if (shared)
                {
                    Log("stage=dispose：管理器仍被其他窗口使用，保留正在共享的模板、管理器与事件系统，不销毁游戏素材。");
                    _assigned.Clear(); _ownedManager = null; _templates = null; _ownedEvents = null; _manager = null;
                    _font = null; _fontMaterial = null; _windowSkin = null; _buttonSkin = null;
                    return;
                }
            }
            catch { }
        }
        if (_manager != null && _ownedManager == null)
            foreach (var entry in _assigned)
                try { if (GetPrefab(_manager, entry.Field)?.Pointer == entry.Assigned.Pointer) SetPrefab(_manager, entry.Field, entry.Original); } catch { }
        _assigned.Clear();
        if (_ownedManager != null) UnityEngine.Object.Destroy(_ownedManager);
        if (_templates != null) UnityEngine.Object.Destroy(_templates);
        if (_ownedEvents != null) UnityEngine.Object.Destroy(_ownedEvents);
        _ownedManager = null; _templates = null; _ownedEvents = null; _manager = null;
        _font = null; _fontMaterial = null; _windowSkin = null; _buttonSkin = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Cleanup(); }
    private sealed record ImageSkin(Sprite Sprite, Material? Material, Image.Type Type, Color Color);
}
