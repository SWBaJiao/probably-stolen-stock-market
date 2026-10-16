using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using MelonModTemplate.Contracts;
using MelonModTemplate.Market;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MelonModTemplate.UI;

// 使用游戏提供的窗口及控件预制件；业务命令仍全部交由资金和存档适配层执行。
public sealed class NativeStockMarketPanel : IDisposable
{
    private const string WindowId = "MelonModTemplate.stock_market.native";
    private static readonly Color TextColor = StockUiTheme.Text;
    private static readonly Color MutedColor = StockUiTheme.Muted;
    private static readonly Color AccentColor = StockUiTheme.Accent;
    private readonly IMarketHost _host;
    private readonly NativeUiBootstrap _bootstrap = new();
    private readonly Dictionary<string, CustomUIElement> _elements = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _minimumHeights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _measuredWidths = new(StringComparer.Ordinal);
    private NativeCandlestickChart? _chart;
    private NativeCompanyDetailsWindow? _companyWindow;
    private GameObject? _companyBackdrop;
    private CanvasGroup? _parentInputGroup;
    private bool _parentWasInteractable, _parentBlockedRaycasts, _restoreEscapeAfterRelease;
    private int _chartDays = 30;
    private readonly List<IDisposable> _bindings = new();
    private readonly List<GameObject> _pages = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Action _managedClosed;
    private Il2CppSystem.Action? _nativeClosed;
    private CustomUIWindow? _window;
    private CustomUIBuilder? _builder;
    private MarketEngine? _boundEngine;
    private string _companyId = "", _quantity = "1", _feedback = "", _saveIdentity = "";
    private bool _buy = true, _visible, _disposed, _cursorSaved, _oldCursorVisible;
    private CursorLockMode _oldCursorLock;
    private int _page;
    private float _nextRefresh, _width = 1040, _height = 750;
    private bool _refreshing;

    public NativeStockMarketPanel(IMarketHost host)
    {
        _host = host;
        _managedClosed = OnNativeClosed;
    }

    public bool IsVisible => _visible;

    public void Show()
    {
        if (_disposed) return;
        try
        {
            if (_visible && IsAlive())
            {
                if (IsCurrentContext()) { if (_companyWindow?.IsVisible == true) _companyWindow.BringToFront(); else _window!.BringToFront(); return; }
                Hide();
            }
            var engine = _host.Engine;
            if (engine == null) { Warn("no save", "进入存档后才能打开原生股市窗口。"); return; }
            _boundEngine = engine;
            _saveIdentity = engine.State.SaveIdentity;
            if (!IsAlive()) Build();
            if (_window == null) return;
            SaveCursor();
            _visible = true;
            _window.Show();
            _window.BringToFront();
            Refresh();
            _nextRefresh = Time.unscaledTime + .5f;
        }
        catch (Exception ex)
        {
            Warn("show", "原生股市窗口创建失败；没有操作玩家背包或原版交易窗口。", ex);
            Hide();
        }
    }

    public void Hide()
    {
        _visible = false;
        try { RestoreCursor(); }
        catch (Exception ex) { Warn("cursor", "恢复原光标状态失败，继续释放原生窗口。", ex); }
        ReleaseWindow(true);
    }

    public void Tick()
    {
        if (!_visible || _disposed) return;
        try
        {
            var engine = _host.Engine;
            // 不允许保留上一个存档的原生控件、输入或委托。
            if (engine == null || !IsCurrentContext() || !IsAlive() || !_window!.IsVisible)
            { Hide(); return; }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (_companyWindow?.IsVisible == true)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) CloseCompanyDetails();
                else if (Time.unscaledTime >= _nextRefresh)
                { _companyWindow.Refresh(engine, _companyId); _nextRefresh = Time.unscaledTime + .5f; }
                _chart?.HideHover();
                return;
            }
            if (_companyWindow != null) CloseCompanyDetails();
            // 子窗口处理过的 Escape 必须先松开，才能重新交给原生主窗口。
            if (_restoreEscapeAfterRelease && !Input.GetKey(KeyCode.Escape))
            { _window.closeOnEscape = true; _restoreEscapeAfterRelease = false; }
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + .5f;
                Refresh();
            }
            // 指针命中按帧更新，不受行情文字半秒刷新间隔限制。
            _chart?.TickHover();
        }
        catch (Exception ex) { Warn("tick", "原生股市窗口更新失败，已关闭并释放自己的控件。", ex); Hide(); }
    }

    private void Build()
    {
        var manager = _bootstrap.Resolve();
        if (manager == null) throw new InvalidOperationException("原生 CustomUI 管理器初始化失败。");
        if (manager.windowPrefab == null || manager.labelPrefab == null || manager.buttonPrefab == null || manager.inputFieldPrefab == null || manager.scrollViewPrefab == null)
            throw new InvalidOperationException("游戏的原生自定义窗口、文本、按钮、输入或滚动预制件尚未配置。");
        var engine = _host.Engine ?? throw new InvalidOperationException("没有活动存档。");
        manager.EnsureOverlayCanvas();
        var canvas = manager.OverlayRoot();
        var bounds = canvas != null ? canvas.rect : new Rect(0, 0, Screen.width, Screen.height);
        _width = Math.Min(1040f, Math.Max(420f, bounds.width - 32f));
        _height = Math.Min(750f, Math.Max(350f, bounds.height - 32f));
        _builder = CustomUIManager.Window(WindowId, "远星证券 · 股市投资", CustomUIManager.LAYER_OVERLAY);
        _window = _builder.window;
        _builder.SetSize(_width, _height).Center().SetDraggable(true).SetCloseOnEscape(true);
        _nativeClosed = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(_managedClosed)
            ?? throw new InvalidOperationException("原生窗口关闭回调转换失败。");
        _window.onClose = _nativeClosed;
        StyleWindow();
        Label("summary", "", 32, true);
        _builder.BeginRow(6);
        var navWidth = (_width - 66) / 4;
        Button("nav.market", "行情与委托", "page:0", navWidth);
        Button("nav.portfolio", "我的持仓", "page:1", navWidth);
        Button("nav.rumors", "内幕档案", "page:2", navWidth);
        Button("nav.ledger", "资金与行情流水", "page:3", navWidth);
        _builder.End();

        var wide = _width >= 820;
        // 正常窗口固定委托区；极矮窗口保留整页滚动，避免强行裁掉交易控件。
        var scrollMarket = wide ? _height < 640 : _height < 750;
        if (scrollMarket) BeginPage();
        else BeginFixedPage();
        _builder.BeginRow(12);
        // 公司列表独立滚动，不再用十家公司的总高度撑高行情页。
        if (!wide)
        {
            _builder.End();
            _builder.BeginColumn(8);
        }
        if (!scrollMarket) FillHeight(_builder.CurrentParent);
        _builder.BeginColumn(4);
        Container(_builder.CurrentParent, wide ? 218 : -1, wide ? -1 : 112);
        if (wide && !scrollMarket) FillHeight(_builder.CurrentParent);
        Label("market.guide", "公司行情 · 点击切换", 28, true);
        _builder.BeginScroll(wide ? 260 : 76);
        Container(_builder.CurrentParent, -1);
        ConfigureCompanyScroll(wide && !scrollMarket, wide ? 260 : 76);
        if (!wide) _builder.BeginGrid(2, (_width - 84) / 2, 38, 4);
        foreach (var company in engine.Config.Companies)
            Button("company." + company.Id, company.Name, "company:" + company.Id, -1, 38);
        if (!wide) _builder.End();
        _builder.End();
        if (wide) Label("market.legend", "涨红 / 跌绿 · 较昨收\n昨日成交日结 · 事件与余波", 54);
        _builder.End();

        _builder.BeginColumn(5);
        Container(_builder.CurrentParent, -1);
        if (!scrollMarket) FillHeight(_builder.CurrentParent);
        _builder.BeginRow(5);
        // 名称和介绍入口紧邻，行情数字使用本行余下空间，不再占用图表高度。
        var companyName = Label("company.title", "", 30, true);
        Container(companyName.root.transform.Cast<RectTransform>(), 88, 30);
        if (companyName.text != null) companyName.text.enableWordWrapping = false;
        Button("company.introduction", "介绍", "details", 48, 28);
        var companyQuote = Label("company.quote", "", 30, true);
        if (companyQuote.text != null)
        {
            companyQuote.text.fontSize = wide ? 18 : 15;
            companyQuote.text.enableWordWrapping = false;
        }
        _builder.End();
        _builder.BeginRow(5);
        Button("chart.7", "近 7 日", "chart:7", -1, 28);
        Button("chart.14", "近 14 日", "chart:14", -1, 28);
        Button("chart.30", "近 30 日", "chart:30", -1, 28);
        _builder.End();
        _builder.BeginColumn(0);
        var chartRoot = _builder.CurrentParent;
        Container(chartRoot, -1);
        if (scrollMarket) Container(chartRoot, -1, 250);
        else
        {
            // 图表吸收标题、周期和固定委托区之外的全部剩余高度。
            FillHeight(chartRoot);
            Get<LayoutElement>(chartRoot.gameObject)!.minHeight = 120;
        }
        var chartLayout = Get<VerticalLayoutGroup>(chartRoot.gameObject);
        if (chartLayout != null) chartLayout.enabled = false;
        var font = _window.frame.titleText.font;
        _chart = new NativeCandlestickChart(chartRoot, font);
        var hoverBounds = _window.root.transform.Cast<RectTransform>();
        _chart.SetHoverBounds(hoverBounds);
        _builder.End();

        _builder.BeginRow(5);
        Button("mode.buy", "买入", "mode:buy", 72, 32);
        Button("mode.sell", "卖出", "mode:sell", 72, 32);
        Button("quantity.minus", "−", "quantity:-1", 32, 32);
        _builder.AddInput("股数", _quantity, null, "quantity");
        var input = Remember("quantity", _builder.Last, 32, -1);
        StyleInput(input);
        _bindings.Add(new NativeQuantityBinding(input, this));
        Button("quantity.plus", "+", "quantity:1", 32, 32);
        _builder.End();
        Label("order.preview", "", 48);
        Button("order.submit", "确认委托", "order", -1, 34);
        _builder.End();
        _builder.End();
        if (scrollMarket) EndPage();
        else _builder.End();

        BeginPage();
        Label("portfolio.total", "", 62, true);
        var positionColumns = _width >= 760 ? 2 : 1;
        _builder.BeginGrid(positionColumns, (_width - 72) / positionColumns - 5, 92, 8);
        foreach (var company in engine.Config.Companies)
            Button("position." + company.Id, company.Name, "company:" + company.Id, -1, 92);
        _builder.End();
        Label("portfolio.note", "点击持仓卡片查看 K 线并委托。持仓成本与已实现盈亏均包含手续费。", 36);
        EndPage();

        BeginPage();
        Label("seller.title", "行情掮客与消息来源", 32, true);
        Label("seller.identity", "", 58);
        Label("seller.reputation", "", 62);
        Label("seller.offer", "", 78);
        Label("rumors.flow", "持有股市面板后，可用游戏电话预约掮客次日到访。购买后会收到署名便笺，并登记游戏日历。", 48);
        Label("broker.contact", "", 64);
        Label("rumors.title", "已购买情报簿", 34, true);
        Label("rumors.book", "", 180);
        EndPage();

        BeginPage();
        Label("ledger.title", "近期资金与行情流水（新记录在前）", 34, true);
        Label("ledger.entries", "", 150);
        EndPage();

        Label("status", "", 32);
        Label("feedback", "", 26);
        _builder.BeginRow(6);
        Button("terminal.recover", "补领股市面板道具", "recover", (_width - 72) / 3);
        Button("refresh", "刷新当前数据", "refresh", (_width - 72) / 3);
        Button("close", "关闭", "close", (_width - 72) / 3);
        _builder.End();
        NormalizeLayout(_window.frame.contentRoot);
        SetPage(_page);
        if (!engine.Config.Companies.Any(c => c.Id == _companyId)) _companyId = engine.Config.Companies[0].Id;
        _window = _builder.Show();
        _builder = null;
        MelonLogger.Msg("[原生股市] 已使用游戏 CustomUI 窗体、按钮、TMP 字体和滚动预制件创建独立投资窗口。");
    }

    private void BeginPage()
    {
        _builder!.BeginColumn(4);
        var page = _builder.CurrentParent;
        _pages.Add(page.gameObject);
        Container(page, -1);
        FillHeight(page);
        _builder.BeginScroll(Math.Max(100, _height - 248));
        Container(_builder.CurrentParent, -1);
        // 页面吃掉标题与页脚以外的剩余高度；缩小窗口时只缩滚动区域。
        for (Transform? ancestor = _builder.CurrentParent; ancestor != null && ancestor.Pointer != page.Pointer; ancestor = ancestor.parent)
            if (Get<ScrollRect>(ancestor.gameObject) is { } scroll)
            {
                FillHeight(scroll.transform.Cast<RectTransform>());
                Surface(scroll.gameObject, "ScrollFill", StockUiTheme.Background, 2, 2, 2, 2);
                break;
            }
    }

    private void BeginFixedPage()
    {
        _builder!.BeginColumn(0);
        var page = _builder.CurrentParent;
        _pages.Add(page.gameObject);
        Container(page, -1);
        FillHeight(page);
    }

    private void ConfigureCompanyScroll(bool fill, float height)
    {
        for (Transform? ancestor = _builder!.CurrentParent; ancestor != null; ancestor = ancestor.parent)
            if (Get<ScrollRect>(ancestor.gameObject) is { } scroll)
            {
                var root = scroll.transform.Cast<RectTransform>();
                // 显式覆盖预制件遗留的最小高度，窄屏选择器才能真正保持紧凑。
                if (fill) FillHeight(root);
                else
                {
                    Container(root, -1, height);
                    var fitter = Get<ContentSizeFitter>(root.gameObject);
                    if (fitter != null) fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                }
                Surface(scroll.gameObject, "CompanyScrollFill", StockUiTheme.Background, 2, 2, 2, 2);
                return;
            }
        throw new InvalidOperationException("公司列表缺少原生滚动容器。");
    }

    private void EndPage() { _builder!.End(); _builder.End(); }

    private CustomUIElement Label(string id, string text, float height, bool heading = false)
    {
        _builder!.AddLabel(text, id);
        var element = Remember(id, _builder.Last, height);
        _minimumHeights[id] = height;
        if (element.text != null)
        {
            element.text.fontSize = heading ? 18 : 16;
            element.text.color = heading ? AccentColor : TextColor;
            element.text.fontStyle = heading ? FontStyles.Bold : FontStyles.Normal;
            element.text.enableWordWrapping = true;
            element.text.richText = false;
            element.text.alignment = TextAlignmentOptions.TopLeft;
        }
        return element;
    }

    private CustomUIElement Button(string id, string text, string command, float width, float height = 34)
    {
        // 由游戏工厂生成全新通用按钮，绝不复制带有成交逻辑的商店按钮。
        _builder!.AddButton(text, null, id);
        var element = Remember(id, _builder.Last, height, width);

        if (element.text != null) { element.text.color = TextColor; element.text.fontSize = 16; element.text.richText = false; element.text.enableWordWrapping = true; }
        if (element.image != null) element.image.color = Color.white;
        if (element.button != null)
        {
            element.button.targetGraphic = Surface(element.root, "ButtonFill", Color.white, 2, 2, 2, 2);
            StyleButton(id);
        }
        _bindings.Add(new NativeButtonBinding(element, this, command));
        return element;
    }

    private CustomUIElement Remember(string id, CustomUIElement element, float height, float width = -1)
    {
        _elements.Add(id, element);
        Container(element.root.transform.Cast<RectTransform>(), width, height);
        return element;
    }

    private void StyleWindow()
    {
        if (_window?.root == null) return;
        // 保留游戏边框与控件，内侧加实色层；不让原版背景纹理乘色后变成近黑色。
        var background = _window.root.GetComponent<Image>();
        if (background != null) background.color = Color.white;
        Surface(_window.root, "OpaqueBackground", StockUiTheme.Background, 4, 4, 4, 4);
        var titleBar = Surface(_window.root, "TitleBar", StockUiTheme.TitleBackground, 5, 5, 5, 5);
        var titleRect = titleBar.transform.Cast<RectTransform>();
        titleRect.anchorMin = new Vector2(0, 1); titleRect.anchorMax = Vector2.one;
        titleRect.offsetMin = new Vector2(5, -46); titleRect.offsetMax = new Vector2(-5, -5);
        titleBar.transform.SetSiblingIndex(1);
        if (_window.frame?.closeButton != null)
        {
            var close = _window.frame.closeButton;
            close.targetGraphic = Surface(close.gameObject, "CloseFill", Color.white, 2, 2, 2, 2);
            SetButtonColors(close, false);
            foreach (var label in close.gameObject.GetComponentsInChildren(Il2CppType.Of<TextMeshProUGUI>(), true))
                label.Cast<TextMeshProUGUI>().color = TextColor;
        }
        if (_window.frame?.titleText != null)
        {
            _window.frame.titleText.color = TextColor;
            _window.frame.titleText.richText = false;
            _window.frame.titleText.fontStyle = FontStyles.Bold;
            try
            {
                var font = _window.frame.titleText.font;
                if (font != null && !font.HasCharacter('股', true, true))
                    Warn("font", "游戏原生 TMP 字体未包含“股”字。窗口沿用游戏字体，请在游戏语言设置及字体回退中检查中文支持。");
            }
            catch (Exception ex) { Warn("font", "原生字体覆盖检查失败，继续使用游戏字体。", ex); }
        }
    }

    private void Refresh()
    {
        if (_refreshing || !_visible || _host.Engine == null) return;
        _refreshing = true;
        try
        {
            var engine = _host.Engine;
            Text("summary", $"第 {Math.Max(0, engine.State.LastDay)} 日    可用资金 {Money(_host.Cash)}    现货交易 · 无杠杆");
            Text("status", _host.Status, _host.CanTrade ? StockUiTheme.Falling : StockUiTheme.Warning);
            Text("feedback", _feedback, StockUiTheme.Warning);
            foreach (var company in engine.Config.Companies)
            {
                if (!engine.State.Companies.TryGetValue(company.Id, out var stock)) continue;
                var close = PreviousClose(engine, stock);
                var change = close == 0 ? 0 : stock.Price / close - 1;
                Text("company." + company.Id, $"{company.Name}  {stock.Price:0.00}  {SignedPercent(change)}");
                Tint("company." + company.Id, company.Id == _companyId);
                if (_elements["company." + company.Id].text != null) _elements["company." + company.Id].text.color = change > 0 ? StockUiTheme.Rising : change < 0 ? StockUiTheme.Falling : TextColor;
            }
            RefreshCompany(engine);
            RefreshPortfolio(engine);
            RefreshRumors(engine);
            RefreshLedger(engine);
            RefreshOrder();
            for (int index = 0; index < 4; index++)
                Tint(new[] { "nav.market", "nav.portfolio", "nav.rumors", "nav.ledger" }[index], index == _page);
        }
        finally { _refreshing = false; }
    }

    private void RefreshCompany(MarketEngine engine)
    {
        var company = engine.Config.Companies.FirstOrDefault(c => c.Id == _companyId);
        if (company == null || !engine.State.Companies.TryGetValue(company.Id, out var stock)) return;
        var latest = stock.History.Count > 1 ? stock.History[^2].Price : engine.Config.InitialPrice;
        var move = latest == 0 ? 0 : stock.Price / latest - 1;
        Text("company.title", company.Name);
        Text("company.quote", $"现价 {stock.Price:0.00} · 最新 {SignedPercent(move)}");
        _chart?.Render(stock.Candles, stock.Price, _chartDays, engine.State.LastDay);
        foreach (var days in new[] { 7, 14, 30 }) Tint("chart." + days, _chartDays == days);
    }

    private void RefreshPortfolio(MarketEngine engine)
    {
        decimal value = 0, cost = 0, realized = 0;
        foreach (var pair in engine.State.Positions)
        {
            if (engine.State.Companies.TryGetValue(pair.Key, out var stock)) value += stock.Price * pair.Value.Quantity;
            cost += pair.Value.Cost;
            realized += pair.Value.RealizedProfit;
        }
        Text("portfolio.total", $"持仓市值 {Money(value)}    持仓成本 {Money(cost)}\n浮动盈亏 {SignedMoney(value - cost)}    已实现盈亏 {SignedMoney(realized)}");
        foreach (var company in engine.Config.Companies)
        {
            engine.State.Positions.TryGetValue(company.Id, out var position);
            var quantity = position?.Quantity ?? 0;
            var ownCost = position?.Cost ?? 0;
            var price = engine.State.Companies[company.Id].Price;
            Text("position." + company.Id, $"{company.Name}   {quantity} 股   现价 {price:0.00}\n成本 {Money(ownCost)}   均价 {(quantity > 0 ? ownCost / quantity : 0):0.00}   浮盈 {SignedMoney(price * quantity - ownCost)}   已实现 {SignedMoney(position?.RealizedProfit ?? 0)}\n点击查看行情 / 提交委托");
        }
    }

    private void RefreshRumors(MarketEngine engine)
    {
        Text("seller.identity", _host.SellerDescription);
        Text("seller.reputation", _host.SellerReputation, MutedColor);
        // 未购情报仅展示 host 的安全报价，不读取未来事件或 RumorOffer 内部字段。
        Text("seller.offer", _host.OfferDescription);
        Text("broker.contact", _host.BrokerContactDescription);
        var purchased = engine.GetRumors().Where(r => r.Purchased).Reverse().Take(30).ToArray();
        var intel = engine.GetIntelRecords().ToDictionary(r => r.OfferId);
        var book = new StringBuilder();
        foreach (var rumor in purchased)
        {
            var resolution = rumor.Resolution switch { RumorResolution.True => "属实", RumorResolution.False => "失实", RumorResolution.Unverified => "未能验证", _ => "等待验证" };
            var source = rumor.SellerName == rumor.Identity ? rumor.SellerName : $"{rumor.SellerName} · {rumor.Identity}";
            book.AppendLine($"{source} · {resolution}");
            book.AppendLine($"{(rumor.IsGroupIdentity ? "同类来源信誉" : "来源信誉")} {rumor.Reliability:0%} · 验证 {rumor.Correct + rumor.Incorrect} 次 · 观察至第 {rumor.DueDay} 日");
            if (intel.TryGetValue(rumor.Id, out var record))
            {
                var delivery = record.PaperState switch { IntelPaperState.Delivered => "已交付", IntelPaperState.Uncertain => "位置待核实", _ => "待柜台交付" };
                book.AppendLine($"第 {record.PurchaseDay} 日购买 · 日历第 {record.ObservationStartDay}～{record.ObservationEndDay} 日 · 纸条{delivery}");
            }
            book.AppendLine(rumor.Content);
            book.AppendLine("关联公司：" + string.Join("、", rumor.CompanyIds.Select(id => engine.Config.Companies.FirstOrDefault(c => c.Id == id)?.Name ?? id)));
            book.AppendLine();
        }
        if (purchased.Length == 0) book.Append("暂无已购线索。购买后保存在此处；到期按实际事件验证。");
        else book.Append("显示最近 30 条已购买情报。");
        Text("rumors.book", book.ToString());
    }

    private void RefreshLedger(MarketEngine engine)
    {
        var entries = engine.State.Ledger.TakeLast(100).Reverse().ToArray();
        var text = new StringBuilder();
        foreach (var entry in entries)
        {
            var kind = entry.Kind switch { "event" => "事件", "day" => "日行情", "customer" => "顾客结算", "buy" => "买入", "sell" => "卖出", "rumor" => "情报", _ => entry.Kind };
            text.AppendLine($"第 {entry.Day} 日 · {kind}" + (entry.CashDelta == 0 ? "" : " · 资金 " + SignedMoney(entry.CashDelta)));
            text.AppendLine(entry.Description);
            text.AppendLine();
        }
        if (entries.Length == 0) text.Append("暂无流水。");
        else text.Append("显示最近 100 条记录。");
        Text("ledger.entries", text.ToString());
    }

    private void RefreshOrder()
    {
        var engine = _host.Engine;
        if (engine == null) return;
        Tint("mode.buy", _buy); Tint("mode.sell", !_buy);
        var valid = int.TryParse(_quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) && quantity > 0;
        var quote = engine.QuoteOrder(_companyId, _buy ? OrderSide.Buy : OrderSide.Sell, valid ? quantity : 0, _host.Cash);
        var text = new StringBuilder();
        if (valid && quote.UnitPrice > 0 && (quote.Success || quote.CashDelta != 0))
        {
            text.AppendLine($"{(_buy ? "买入" : "卖出")} {quantity} 股 · 单价 {quote.UnitPrice:0.00} · 手续费 {quote.Fee:0.00}");
            text.AppendLine($"资金变动 {SignedMoney(quote.CashDelta)} · 成交后余额 {(long)_host.Cash + quote.CashDelta}");
        }
        if (!valid) text.Append("请输入正整数股数。");
        else if (!_host.CanTrade) text.Append(_host.Status);
        else if (!quote.Success) text.Append(quote.Message);
        Text("order.preview", text.ToString(), valid && quote.Success && _host.CanTrade ? TextColor : StockUiTheme.Warning);
        Text("order.submit", _buy ? "确认买入" : "确认卖出");
        Enabled("order.submit", valid && quote.Success && _host.CanTrade);
    }

    internal void HandleCommand(string command)
    {
        if (_disposed || !_visible) return;
        if (!IsCurrentContext()) { Hide(); return; }
        if (_companyWindow?.IsVisible == true) { if (command == "details") _companyWindow.BringToFront(); return; }
        try
        {
            if (command == "close") { Hide(); return; }
            if (command.StartsWith("page:", StringComparison.Ordinal)) SetPage(int.Parse(command.Substring(5), CultureInfo.InvariantCulture));
            else if (command.StartsWith("company:", StringComparison.Ordinal)) { _companyId = command.Substring(8); SetPage(0); }
            else if (command.StartsWith("chart:", StringComparison.Ordinal))
                _chartDays = command switch { "chart:7" => 7, "chart:14" => 14, _ => 30 };
            else if (command == "details") OpenCompanyDetails();
            else if (command == "mode:buy") _buy = true;
            else if (command == "mode:sell") _buy = false;
            else if (command.StartsWith("quantity:", StringComparison.Ordinal))
            {
                int.TryParse(_quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity);
                var delta = int.Parse(command.Substring(9), CultureInfo.InvariantCulture);
                _quantity = Math.Clamp((long)quantity + delta, 1, 999999999).ToString(CultureInfo.InvariantCulture);
                _elements["quantity"].SetString(_quantity, false);
            }
            else if (command == "order") SubmitOrder();
            else if (command == "recover") _feedback = _host.RecoverTerminal();
            Refresh();
        }
        catch (Exception ex) { _feedback = "操作未完成，请查看加载日志。"; Warn("command:" + command, "原生股市命令执行失败。", ex); }
    }

    internal void HandleQuantityChanged(string value)
    {
        if (_disposed || !_visible || _companyWindow?.IsVisible == true) return;
        if (!IsCurrentContext()) { Hide(); return; }
        _quantity = value ?? "";
        try { RefreshOrder(); }
        catch (Exception ex) { Warn("quantity", "委托数量预览失败。", ex); }
    }

    private void SubmitOrder()
    {
        MelonLogger.Msg($"[原生股市委托] 点击{(_buy ? "买入" : "卖出")} company={_companyId} quantity={_quantity} canTrade={_host.CanTrade}");
        if (!IsCurrentContext()) { Hide(); return; }
        var engine = _host.Engine;
        if (engine == null || !_host.CanTrade) { _feedback = _host.Status; return; }
        if (!int.TryParse(_quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
        { _feedback = "请输入正整数股数。"; return; }
        // 点击时重新读取资金与最新行情，不能依赖上一次定时刷新的报价。
        var quote = engine.QuoteOrder(_companyId, _buy ? OrderSide.Buy : OrderSide.Sell, quantity, _host.Cash);
        _feedback = quote.Success ? _host.PlaceOrder(_companyId, quantity, _buy) : quote.Message;
    }

    private void SetPage(int index)
    {
        _chart?.HideHover();
        _page = Math.Clamp(index, 0, 3);
        for (int i = 0; i < _pages.Count; i++) _pages[i].SetActive(i == _page);
    }

    private void OpenCompanyDetails()
    {
        if (_companyWindow?.IsVisible == true) { _companyWindow.BringToFront(); return; }
        if (_window == null || !IsCurrentContext() || _host.Engine == null) return;
        try
        {
            _chart?.HideHover();
            EventSystem.current?.SetSelectedGameObject(null);
            _window.closeOnEscape = false;
            _restoreEscapeAfterRelease = false;
            var group = Get<CanvasGroup>(_window.root) ?? _window.root.AddComponent(Il2CppType.Of<CanvasGroup>()).Cast<CanvasGroup>();
            _parentWasInteractable = group.interactable;
            _parentBlockedRaycasts = group.blocksRaycasts;
            _parentInputGroup = group;
            group.interactable = false;
            group.blocksRaycasts = false;
            _companyWindow = new NativeCompanyDetailsWindow(SelectCompanyFromDetails, OnCompanyDetailsClosed);
            _companyWindow.Show(_host.Engine, _companyId);
            var childRoot = _companyWindow.Root ?? throw new InvalidOperationException("公司窗口缺少原生根节点。");
            var layer = childRoot.transform.parent ?? throw new InvalidOperationException("公司窗口缺少原生覆盖层。");
            var backdrop = Surface(layer.gameObject, "CompanyModalBackdrop", new Color(0, 0, 0, .65f), 0, 0, 0, 0);
            _companyBackdrop = backdrop.gameObject;
            backdrop.raycastTarget = true;
            backdrop.transform.SetSiblingIndex(childRoot.transform.GetSiblingIndex());
            _companyWindow.BringToFront();
        }
        catch { CloseCompanyDetails(); throw; }
    }

    private bool SelectCompanyFromDetails(string companyId)
    {
        if (!_visible || !IsCurrentContext()) { Hide(); return false; }
        _companyId = companyId;
        RefreshCompany(_host.Engine!);
        return true;
    }

    private void CloseCompanyDetails()
    {
        var child = _companyWindow;
        _companyWindow = null;
        try { child?.Dispose(); }
        finally { OnCompanyDetailsClosed(); }
    }

    private void OnCompanyDetailsClosed()
    {
        _companyWindow = null;
        if (_companyBackdrop != null) UnityEngine.Object.Destroy(_companyBackdrop);
        _companyBackdrop = null;
        if (_parentInputGroup != null)
        {
            _parentInputGroup.interactable = _parentWasInteractable;
            _parentInputGroup.blocksRaycasts = _parentBlockedRaycasts;
            _parentInputGroup = null;
        }
        EventSystem.current?.SetSelectedGameObject(null);
        // 点击关闭同样延后恢复 Escape，避免原生窗口更新顺序导致连续关闭。
        if (_window != null) _restoreEscapeAfterRelease = true;
        _nextRefresh = 0;
    }

    private void Text(string id, string value, Color? color = null)
    {
        if (!_elements.TryGetValue(id, out var element)) return;
        if (id == "feedback") element.root.SetActive(!string.IsNullOrWhiteSpace(value));
        bool changed = !_textCache.TryGetValue(id, out var previous) || previous != value;
        if (changed) { element.SetText(value); _textCache[id] = value; }
        if (_minimumHeights.TryGetValue(id, out var minimum) && element.text != null)
        {
            var actualWidth = element.root.transform.Cast<RectTransform>().rect.width;
            var available = Math.Max(100, actualWidth > 20 ? actualWidth - 12 : _width - 90);
            if (changed || !_measuredWidths.TryGetValue(id, out var lastWidth) || Math.Abs(lastWidth - available) > 1)
            {
                try
                {
                    var preferred = element.text.GetPreferredValues(value, available, float.PositiveInfinity);
                    var layout = Get<LayoutElement>(element.root);
                    if (layout != null) { layout.preferredHeight = Math.Max(minimum, preferred.y + 8); layout.minHeight = minimum; }
                    _measuredWidths[id] = available;
                }
                catch (Exception ex) { Warn("measure:" + id, "原生文本高度计算失败，将使用初始高度。", ex); }
            }
        }
        if (color.HasValue && element.text != null) element.text.color = color.Value;
    }

    internal static void Container(RectTransform root, float width, float height = -1)
    {
        var layout = Get<LayoutElement>(root.gameObject) ?? root.gameObject.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
        // 清除模板遗留的固定宽度；负值表示填充所在列。
        layout.minWidth = width < 0 ? 0 : width;
        layout.preferredWidth = width < 0 ? 0 : width;
        layout.flexibleWidth = width < 0 ? 1 : 0;
        if (height >= 0) { layout.minHeight = height; layout.preferredHeight = height; layout.flexibleHeight = 0; }
    }

    private static T? Get<T>(GameObject root) where T : Component => root.GetComponent(Il2CppType.Of<T>())?.TryCast<T>();

    private static void FillHeight(RectTransform root)
    {
        var layout = Get<LayoutElement>(root.gameObject) ?? root.gameObject.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
        layout.minHeight = 0; layout.preferredHeight = 0; layout.flexibleHeight = 1;
        var fitter = Get<ContentSizeFitter>(root.gameObject);
        if (fitter != null) fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
    }

    internal static void NormalizeLayout(RectTransform root)
    {
        foreach (var component in root.gameObject.GetComponentsInChildren(Il2CppType.Of<VerticalLayoutGroup>(), true))
        {
            var group = component.Cast<VerticalLayoutGroup>();
            group.childControlWidth = true; group.childForceExpandWidth = true;
            group.childControlHeight = true; group.childForceExpandHeight = false;
        }
        foreach (var component in root.gameObject.GetComponentsInChildren(Il2CppType.Of<HorizontalLayoutGroup>(), true))
        {
            var group = component.Cast<HorizontalLayoutGroup>();
            group.childControlWidth = true; group.childForceExpandWidth = false;
            group.childControlHeight = true; group.childForceExpandHeight = false;
        }
        foreach (var component in root.gameObject.GetComponentsInChildren(Il2CppType.Of<ContentSizeFitter>(), true))
            component.Cast<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
    }

    private void Enabled(string id, bool enabled)
    {
        if (_elements.TryGetValue(id, out var element) && element.button != null)
        { element.button.interactable = enabled; StyleButton(id); }
    }

    private void Tint(string id, bool selected)
    {
        if (selected) _selectedButtons.Add(id); else _selectedButtons.Remove(id);
        StyleButton(id);
    }

    private void StyleButton(string id)
    {
        if (!_elements.TryGetValue(id, out var element) || element.button == null) return;
        bool selected = _selectedButtons.Contains(id);
        SetButtonColors(element.button, selected);
        if (element.image != null) element.image.color = selected ? AccentColor : Color.white;
        if (element.text != null)
        {
            element.text.color = element.button.interactable ? TextColor : MutedColor;
            element.text.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
        }
    }

    internal static void SetButtonColors(Selectable button, bool selected)
    {
        button.transition = Selectable.Transition.ColorTint;
        var colors = button.colors;
        // 当前 IL2CPP 代理裁剪了部分 ColorBlock setter，使用已还原的结构字段。
        colors.m_NormalColor = selected ? StockUiTheme.Selected : StockUiTheme.Button;
        colors.m_HighlightedColor = selected ? StockUiTheme.SelectedHover : StockUiTheme.Hover;
        colors.m_SelectedColor = colors.m_HighlightedColor;
        colors.m_PressedColor = StockUiTheme.Pressed;
        colors.m_DisabledColor = StockUiTheme.Disabled;
        colors.m_ColorMultiplier = 1; colors.m_FadeDuration = .1f;
        button.colors = colors;
    }

    private static void StyleInput(CustomUIElement element)
    {
        if (element.input == null) return;
        var input = element.input;
        input.targetGraphic = Surface(element.root, "InputFill", Color.white, 2, 2, 2, 2);
        SetButtonColors(input, false);
        var colors = input.colors; colors.m_NormalColor = StockUiTheme.Surface; input.colors = colors;
        input.customCaretColor = true; input.caretColor = AccentColor;
        input.selectionColor = new Color(.35f, .65f, .85f, .55f);
        if (input.textComponent != null) { input.textComponent.color = TextColor; input.textComponent.fontSize = 17; }
        if (input.placeholder != null) input.placeholder.color = MutedColor;
    }

    internal static Image Surface(GameObject parent, string name, Color color, float left, float top, float right, float bottom)
    {
        var types = new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() });
        var go = new GameObject("StockMarket." + name, types);
        go.transform.SetParent(parent.transform, false); go.transform.SetAsFirstSibling();
        var rect = go.transform.Cast<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top);
        var layout = go.AddComponent(Il2CppType.Of<LayoutElement>()).Cast<LayoutElement>();
        layout.ignoreLayout = true;
        var image = go.AddComponent(Il2CppType.Of<Image>()).Cast<Image>();
        image.color = color; image.raycastTarget = false;
        return image;
    }

    private bool IsAlive()
    {
        try { return _window != null && _window.IsAlive; }
        catch { return false; }
    }

    private bool IsCurrentContext()
    {
        var current = _host.Engine;
        return current != null && ReferenceEquals(current, _boundEngine) && current.State.SaveIdentity == _saveIdentity;
    }

    private void OnNativeClosed()
    {
        _visible = false;
        try { RestoreCursor(); }
        catch (Exception ex) { Warn("cursor", "恢复原光标状态失败，继续释放原生窗口。", ex); }
        ReleaseWindow(false);
    }

    private void ReleaseWindow(bool close)
    {
        CloseCompanyDetails();
        _restoreEscapeAfterRelease = false;
        var window = _window;
        _window = null;
        foreach (var binding in _bindings)
            try { binding.Dispose(); }
            catch (Exception ex) { Warn("unbind", "原生控件监听器移除失败，窗口销毁后将释放控件。", ex); }
        _chart?.Dispose(); _chart = null;
        _bindings.Clear(); _elements.Clear(); _pages.Clear(); _textCache.Clear(); _minimumHeights.Clear(); _measuredWidths.Clear(); _builder = null;
        _selectedButtons.Clear();
        _boundEngine = null; _saveIdentity = "";
        if (window != null)
        {
            try { window.onClose = null; if (close && window.IsAlive) window.Close(); }
            catch (Exception ex) { Warn("close", "独立原生股市窗口清理失败。", ex); }
        }
        _nativeClosed = null;
        GC.KeepAlive(_managedClosed);
    }

    private void SaveCursor()
    {
        if (!_cursorSaved)
        {
            _oldCursorLock = Cursor.lockState; _oldCursorVisible = Cursor.visible; _cursorSaved = true;
        }
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
    }

    private void RestoreCursor()
    {
        if (!_cursorSaved) return;
        Cursor.lockState = _oldCursorLock; Cursor.visible = _oldCursorVisible; _cursorSaved = false;
    }

    private void Warn(string stage, string text, Exception? exception = null)
    {
        if (!_warned.Add(stage)) return;
        MelonLogger.Warning("[原生股市] " + stage + ": " + text + (exception == null ? "" : Environment.NewLine + exception));
    }

    private static decimal PreviousClose(MarketEngine engine, CompanyState stock) => stock.Candles.LastOrDefault(p => p.Day < engine.State.LastDay)?.Close ?? stock.History.LastOrDefault(p => p.Day < engine.State.LastDay)?.Price ?? engine.Config.InitialPrice;
    private static string Money(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string SignedMoney(decimal value) => (value > 0 ? "+" : "") + Money(value);
    private static string SignedPercent(decimal value) => (value > 0 ? "+" : "") + value.ToString("0.00%", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        try { _bootstrap.Dispose(); }
        catch (Exception ex) { Warn("bootstrap dispose", "原生股市资源释放失败。", ex); }
    }
}
