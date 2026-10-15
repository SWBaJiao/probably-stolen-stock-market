using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using MelonModTemplate.Contracts;
using MelonModTemplate.Market;
using UnityEngine;
using UnityEngine.UI;

namespace MelonModTemplate.UI;

// 使用游戏提供的窗口及控件预制件；业务命令仍全部交由资金和存档适配层执行。
public sealed class NativeStockMarketPanel : IDisposable
{
    private const string WindowId = "MelonModTemplate.stock_market.native";
    private static readonly Color TextColor = new(.95f, .87f, .66f, 1f);
    private static readonly Color MutedColor = new(.72f, .76f, .72f, 1f);
    private static readonly Color AccentColor = new(.43f, .83f, .82f, 1f);
    private static readonly Color ButtonColor = new(.27f, .25f, .21f, 1f);
    private static readonly Color SelectedColor = new(.26f, .40f, .38f, 1f);
    private readonly IMarketHost _host;
    private readonly NativeUiBootstrap _bootstrap = new();
    private readonly Dictionary<string, CustomUIElement> _elements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _minimumHeights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _measuredWidths = new(StringComparer.Ordinal);
    private NativeCandlestickChart? _chart;
    private NativeIntradayChart? _intradayChart;
    private GameObject? _companyDetails;
    private int _chartDays = 30;
    private bool _showDetails;
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
                if (IsCurrentContext()) { _window!.BringToFront(); return; }
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
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + .5f;
                Refresh();
            }
            // 指针命中按帧更新，不受行情文字半秒刷新间隔限制。
            if (_chartDays == 1) _intradayChart?.TickHover();
            else _chart?.TickHover();
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

        BeginPage();
        _builder.BeginRow(12);
        // 宽屏并排显示自选公司与图表，窄屏保持纵向可滚动。
        var wide = _width >= 820;
        if (!wide)
        {
            _builder.End();
            _builder.BeginColumn(8);
        }
        _builder.BeginColumn(4);
        Container(_builder.CurrentParent, wide ? 218 : -1);
        Label("market.guide", "公司行情 · 点击切换", 28, true);
        if (!wide) _builder.BeginGrid(2, (_width - 84) / 2, 38, 4);
        foreach (var company in engine.Config.Companies)
            Button("company." + company.Id, company.Name, "company:" + company.Id, -1, 38);
        if (!wide) _builder.End();
        Label("market.legend", "涨红 / 跌绿 · 较昨收\n成交信号在顾客遣散后结算", 54);
        _builder.End();

        _builder.BeginColumn(5);
        Container(_builder.CurrentParent, -1);
        Label("company.title", "", 30, true);
        _builder.BeginRow(5);
        Button("chart.1", "当日分时", "chart:1", -1, 28);
        Button("chart.7", "近 7 日", "chart:7", -1, 28);
        Button("chart.14", "近 14 日", "chart:14", -1, 28);
        Button("chart.30", "近 30 日", "chart:30", -1, 28);
        _builder.End();
        Button("company.toggle", "公司与商品关联 ▸", "details", -1, 28);
        _builder.BeginColumn(0);
        var chartRoot = _builder.CurrentParent;
        Container(chartRoot, -1, 230);
        var chartLayout = Get<VerticalLayoutGroup>(chartRoot.gameObject);
        if (chartLayout != null) chartLayout.enabled = false;
        var font = _window.frame.titleText.font;
        _chart = new NativeCandlestickChart(chartRoot, font);
        _intradayChart = new NativeIntradayChart(chartRoot, font);
        var hoverBounds = _window.root.transform.Cast<RectTransform>();
        _chart.SetHoverBounds(hoverBounds);
        _intradayChart.SetHoverBounds(hoverBounds);
        _builder.End();

        _builder.BeginColumn(4);
        _companyDetails = _builder.CurrentParent.gameObject;
        Label("company.description", "", 36);
        Label("company.sectors", "", 58);
        Label("company.peers", "", 44);
        Label("company.news", "", 60);
        _builder.End();
        _companyDetails.SetActive(_showDetails);
        Label("order.title", "现货委托 · 整数股", 26, true);
        _builder.BeginRow(5);
        Button("mode.buy", "买入", "mode:buy", 72, 32);
        Button("mode.sell", "卖出", "mode:sell", 72, 32);
        Button("quantity.minus", "−", "quantity:-1", 32, 32);
        _builder.AddInput("股数", _quantity, null, "quantity");
        var input = Remember("quantity", _builder.Last, 32, -1);
        _bindings.Add(new NativeQuantityBinding(input, this));
        Button("quantity.plus", "+", "quantity:1", 32, 32);
        _builder.End();
        Label("order.preview", "", 48);
        Button("order.submit", "确认委托", "order", -1, 34);
        _builder.End();
        _builder.End();
        EndPage();

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
        Label("rumors.flow", "购买请与上门的行情掮客交谈。成交后会收到署名便笺，并自动登记游戏日历。", 48);
        Button("broker.invite", "邀请内幕掮客", "broker:invite", -1, 36);
        Label("broker.invite.note", "营业时可手动邀请，绕过自动来访冷却；有可售线索才加入队尾，同日报价不刷新。", 44);
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
            { FillHeight(scroll.transform.Cast<RectTransform>()); break; }
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
        if (element.image != null) element.image.color = ButtonColor;
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
        // 仅调整本窗口预制件实例的色调；边框 sprite 和字体仍来自游戏素材。
        var background = _window.root.GetComponent<Image>();
        if (background != null) background.color = new Color(.15f, .14f, .12f, .98f);
        if (_window.frame?.titleText != null)
        {
            _window.frame.titleText.color = TextColor;
            _window.frame.titleText.richText = false;
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
            Text("status", _host.Status, MutedColor);
            Text("feedback", _feedback, AccentColor);
            foreach (var company in engine.Config.Companies)
            {
                if (!engine.State.Companies.TryGetValue(company.Id, out var stock)) continue;
                var close = PreviousClose(engine, stock);
                var change = close == 0 ? 0 : stock.Price / close - 1;
                Text("company." + company.Id, $"{company.Name}  {stock.Price:0.00}  {SignedPercent(change)}");
                Tint("company." + company.Id, company.Id == _companyId);
                if (_elements["company." + company.Id].text != null) _elements["company." + company.Id].text.color = change > 0 ? new Color(.96f, .43f, .40f, 1) : change < 0 ? new Color(.37f, .81f, .58f, 1) : TextColor;
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
        Text("company.title", $"{company.Name}   {stock.Price:0.00}   最新变化 {SignedPercent(move)}");
        Text("company.description", company.Description);
        var sectors = engine.Config.Sectors.Where(s => s.Weights.ContainsKey(company.Id)).ToArray();
        Text("company.sectors", "关联商品及成交影响分配：\n" + string.Join("  ·  ", sectors.Select(s => $"{s.Name} {s.Weights[company.Id]:0%}")));
        var peers = sectors.SelectMany(s => s.Weights.Keys).Where(id => id != company.Id).Distinct();
        Text("company.peers", "共同关联公司：\n" + string.Join("、", peers.Select(id => engine.Config.Companies.FirstOrDefault(c => c.Id == id)?.Name ?? id)));
        var intraday = _chartDays == 1;
        _chart?.SetVisible(!intraday);
        _intradayChart?.SetVisible(intraday);
        if (intraday)
            _intradayChart?.Render(stock.IntradayDay == engine.State.LastDay ? stock.IntradayPoints : Array.Empty<PricePoint>(), stock.Price, engine.State.LastDay, stock.IntradayIsPartial);
        else _chart?.Render(stock.Candles, stock.Price, _chartDays, engine.State.LastDay);
        foreach (var days in new[] { 1, 7, 14, 30 }) Tint("chart." + days, _chartDays == days);
        Text("company.toggle", _showDetails ? "收起公司与商品关联 ▾" : "公司与商品关联 ▸");
        var news = engine.State.Ledger.Where(e => e.Kind == "event" && (string.IsNullOrEmpty(e.CompanyId) || e.CompanyId == company.Id)).TakeLast(4).Reverse();
        Text("company.news", "已公开事件：\n" + (news.Any() ? string.Join("\n\n", news.Select(e => $"第 {e.Day} 日  {e.Description}")) : "暂无已公开事件。"));
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
        Text("order.preview", text.ToString());
        Text("order.submit", _buy ? "确认买入" : "确认卖出");
        Enabled("order.submit", valid && quote.Success && _host.CanTrade);
    }

    internal void HandleCommand(string command)
    {
        if (_disposed || !_visible) return;
        if (!IsCurrentContext()) { Hide(); return; }
        try
        {
            if (command == "close") { Hide(); return; }
            if (command.StartsWith("page:", StringComparison.Ordinal)) SetPage(int.Parse(command.Substring(5), CultureInfo.InvariantCulture));
            else if (command.StartsWith("company:", StringComparison.Ordinal)) { _companyId = command.Substring(8); SetPage(0); }
            else if (command.StartsWith("chart:", StringComparison.Ordinal))
                _chartDays = command switch { "chart:1" => 1, "chart:7" => 7, "chart:14" => 14, _ => 30 };
            else if (command == "details") { _showDetails = !_showDetails; _companyDetails?.SetActive(_showDetails); }
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
            else if (command == "broker:invite") _feedback = _host.InviteIntelBroker();
            Refresh();
        }
        catch (Exception ex) { _feedback = "操作未完成，请查看加载日志。"; Warn("command:" + command, "原生股市命令执行失败。", ex); }
    }

    internal void HandleQuantityChanged(string value)
    {
        if (_disposed || !_visible) return;
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
        _intradayChart?.HideHover();
        _page = Math.Clamp(index, 0, 3);
        for (int i = 0; i < _pages.Count; i++) _pages[i].SetActive(i == _page);
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

    private static void Container(RectTransform root, float width, float height = -1)
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

    private static void NormalizeLayout(RectTransform root)
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
        if (_elements.TryGetValue(id, out var element) && element.button != null) element.button.interactable = enabled;
    }

    private void Tint(string id, bool selected)
    {
        if (!_elements.TryGetValue(id, out var element)) return;
        if (element.image != null) element.image.color = selected ? SelectedColor : ButtonColor;
        if (element.text != null) element.text.color = selected ? AccentColor : TextColor;
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
        var window = _window;
        _window = null;
        foreach (var binding in _bindings)
            try { binding.Dispose(); }
            catch (Exception ex) { Warn("unbind", "原生控件监听器移除失败，窗口销毁后将释放控件。", ex); }
        _chart?.Dispose(); _chart = null;
        _intradayChart?.Dispose(); _intradayChart = null; _companyDetails = null;
        _bindings.Clear(); _elements.Clear(); _pages.Clear(); _textCache.Clear(); _minimumHeights.Clear(); _measuredWidths.Clear(); _builder = null;
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
