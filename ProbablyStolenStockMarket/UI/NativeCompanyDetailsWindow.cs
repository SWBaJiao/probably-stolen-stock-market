using System;
using ProbablyStolenStockMarket.Localization;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using ProbablyStolenStockMarket.Market;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.UI;

// 公司长文拥有独立滚动区域，生命周期由股市主窗口管理。
internal sealed class NativeCompanyDetailsWindow : IDisposable
{
    private const string WindowId = "ProbablyStolenStockMarket.stock_market.company";
    private readonly Func<string, bool> _select;
    private readonly Action _closed;
    private readonly Action _managedClosed;
    private readonly List<(Button Button, UnityAction Native, Action Managed)> _listeners = new();
    private readonly Dictionary<string, CustomUIElement> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private CustomUIWindow? _window;
    private CustomUIBuilder? _builder;
    private Action? _nativeClosed;
    private MarketEngine? _engine;
    private string _companyId = "";
    private float _width;
    private bool _disposed;

    internal NativeCompanyDetailsWindow(Func<string, bool> select, Action closed)
    { _select = select; _closed = closed; _managedClosed = OnClosed; }

    internal bool IsVisible => !_disposed && _window != null && _window.IsAlive && _window.IsVisible;
    internal GameObject? Root => _window?.root;

    internal void Show(MarketEngine engine, string companyId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(NativeCompanyDetailsWindow));
        _engine = engine; _companyId = companyId;
        if (_window == null || !_window.IsAlive) Build();
        Refresh(engine, companyId);
        _window!.Show(); _window.BringToFront();
    }

    internal void BringToFront() { if (IsVisible) _window!.BringToFront(); }

    private void Build()
    {
        var manager = CustomUIManager.Instance;
        var overlay = manager.GetLayerRoot(CustomUIManager.LAYER_OVERLAY);
        var bounds = overlay != null ? overlay.rect : new Rect(0, 0, Screen.width, Screen.height);
        _width = Math.Min(780, Math.Max(320, bounds.width - 48));
        var height = Math.Min(650, Math.Max(280, bounds.height - 48));
        _builder = CustomUIManager.Window(WindowId, Localizer.Text("公司介绍与公开消息"), CustomUIManager.LAYER_OVERLAY);
        _window = _builder.Handle;
        // Escape 统一由主窗口分发，避免同一次按键关闭两个窗口。
        _builder.SetSize(_width, height).Center().SetDraggable(true).SetCloseOnEscape(false);
        _nativeClosed = _managedClosed;
        _window.onClose = _nativeClosed;
        StyleFrame();
        _builder.BeginRow(6);
        Button("上一家公司", () => Move(-1), 120);
        Button("下一家公司", () => Move(1), 120);
        _builder.End();
        _builder.BeginScroll(Math.Max(100, height - 170));
        NativeStockMarketPanel.Container(_builder.CurrentContainer, -1);
        Label("name", 28, true);
        Label("story", 80);
        Label("news.title", 28, true);
        Label("news", 80);
        Label("native.title", 28, true);
        Label("native", 80);
        _builder.End();
        Button("返回行情", Dispose, -1);
        NativeStockMarketPanel.NormalizeLayout(_window.frame.contentRoot);
        _window = _builder.Show(); _builder = null;
    }

    private void Label(string id, float height, bool heading = false)
    {
        _builder!.AddLabel("", "details." + id);
        var element = _builder.Last;
        NativeStockMarketPanel.Container(element.root.GetComponent<RectTransform>(), -1, height);
        if (element.text != null)
        {
            NativeUiBootstrap.ApplyCurrentFont(element.text);
            element.text.fontSize = heading ? 19 : 17;
            element.text.color = heading ? StockUiTheme.Accent : StockUiTheme.Text;
            element.text.fontStyle = heading ? FontStyles.Bold : FontStyles.Normal;
            element.text.richText = false; element.text.raycastTarget = false;
            element.text.enableWordWrapping = true; element.text.alignment = TextAlignmentOptions.TopLeft;
        }
        _labels.Add(id, element);
    }

    private void Button(string text, Action action, float width)
    {
        _builder!.AddButton(Localizer.Text(text), null, null);
        var element = _builder.Last;
        NativeStockMarketPanel.Container(element.root.GetComponent<RectTransform>(), width, 36);
        if (element.text != null) { NativeUiBootstrap.ApplyCurrentFont(element.text); element.text.color = StockUiTheme.Text; element.text.fontSize = 16; element.text.richText = false; }
        var button = element.button ?? throw new InvalidOperationException("公司窗口按钮缺少 Button。");
        button.targetGraphic = NativeStockMarketPanel.Surface(element.root, "CompanyButton", Color.white, 2, 2, 2, 2);
        NativeStockMarketPanel.SetButtonColors(button, false);
        Action managed = () => { if (!_disposed) action(); };
        var native = new UnityAction(managed.Invoke);
        button.onClick.AddListener(native);
        _listeners.Add((button, native, managed));
    }

    private void StyleFrame()
    {
        var window = _window!;
        var background = window.root.GetComponent<Image>();
        if (background != null) background.color = Color.white;
        NativeStockMarketPanel.Surface(window.root, "CompanyBackground", StockUiTheme.Background, 4, 4, 4, 4);
        var bar = NativeStockMarketPanel.Surface(window.root, "CompanyTitleBar", StockUiTheme.TitleBackground, 5, 5, 5, 5);
        var rect = bar.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0, 1); rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(5, -46); rect.offsetMax = new Vector2(-5, -5);
        bar.transform.SetSiblingIndex(1);
        if (window.frame.titleText != null) { NativeUiBootstrap.ApplyCurrentFont(window.frame.titleText); window.frame.titleText.color = StockUiTheme.Text; window.frame.titleText.richText = false; }
        if (window.frame.closeButton != null)
        {
            var close = window.frame.closeButton;
            close.targetGraphic = NativeStockMarketPanel.Surface(close.gameObject, "CompanyClose", Color.white, 2, 2, 2, 2);
            NativeStockMarketPanel.SetButtonColors(close, false);
            foreach (var label in close.gameObject.GetComponentsInChildren<TextMeshProUGUI>(true)) label.color = StockUiTheme.Text;
        }
    }

    internal void Refresh(MarketEngine engine, string companyId)
    {
        if (_disposed || _window == null || !_window.IsAlive) return;
        _engine = engine; _companyId = companyId;
        var company = engine.Config.Companies.FirstOrDefault(c => c.Id == companyId);
        if (company == null) return;
        _window.SetTitle(Localizer.Text(company.Name + " · 公司介绍"));
        Text("name", company.Name);
        Text("story", company.Description);
        Text("news.title", "股市专用新闻");
        Text("news", StockNewsText.Format(engine, engine.GetPublicStockNews(company.Id), 12));
        Text("native.title", "原版事件公开记录");
        // 只读公开流水，不读取排程或未购买情报，避免公司介绍泄露未来事件。
        var news = engine.State.Ledger.Where(e => e.Kind == "event" && (string.IsNullOrEmpty(e.CompanyId) || e.CompanyId == company.Id)).TakeLast(12).Reverse().ToArray();
        Text("native", news.Length == 0 ? "暂无原版事件公开记录。" : string.Join("\n\n", news.Select(e => $"第 {e.Day} 日  {e.Description}")));
    }

    private void Text(string id, string value)
    {
        if (!_labels.TryGetValue(id, out var element)) return;
        value = Localizer.Text(value);
        var changed = !_texts.TryGetValue(id, out var old) || old != value;
        if (changed) { element.SetText(value); _texts[id] = value; }
        if (element.text == null) return;
        var actual = element.root.GetComponent<RectTransform>().rect.width;
        var preferred = element.text.GetPreferredValues(value, Math.Max(160, actual > 20 ? actual - 12 : _width - 100), float.PositiveInfinity);
        var layout = element.root.GetComponent<LayoutElement>();
        if (layout != null) { layout.minHeight = 28; layout.preferredHeight = Math.Max(28, preferred.y + 12); }
    }

    private void Move(int direction)
    {
        if (_engine == null || _engine.Config.Companies.Count == 0) return;
        var companies = _engine.Config.Companies;
        var index = companies.FindIndex(c => c.Id == _companyId);
        var next = companies[(Math.Max(0, index) + direction + companies.Count) % companies.Count].Id;
        var engine = _engine;
        if (_select(next)) Refresh(engine, next);
    }

    private void OnClosed() => Release(false);

    public void Dispose() => Release(true);

    private void Release(bool close)
    {
        if (_disposed) return;
        _disposed = true;
        var window = _window; _window = null;
        var nativeClosed = _nativeClosed;
        foreach (var entry in _listeners)
        {
            try { entry.Button.onClick.RemoveListener(entry.Native); }
            catch { /* 原生窗口先销毁时，托管委托仍在此处统一释放。 */ }
            GC.KeepAlive(entry.Managed);
        }
        _listeners.Clear(); _labels.Clear(); _texts.Clear(); _engine = null; _builder = null;
        try { if (window != null) { window.onClose = null; if (close && window.IsAlive) window.Close(); } }
        finally { _nativeClosed = null; _closed(); GC.KeepAlive(nativeClosed); GC.KeepAlive(_managedClosed); }
    }
}
