using System;
using System.Globalization;
using System.IO;
using System.Linq;
using MelonLoader;
using MelonModTemplate.Contracts;
using MelonModTemplate.Market;
using UnityEngine;

namespace MelonModTemplate.UI;

// 界面只绘制数据和提交命令，不直接改资金、持仓或游戏存档。
public sealed class StockMarketPanel : IDisposable
{
    private readonly IMarketHost _host;
    private GUIStyle? _label, _heading, _button, _box, _field, _muted;
    private Font? _font;
    private bool _fontInitialized, _fontMissing, _disposed, _cursorSaved;
    private bool _previousCursorVisible;
    private CursorLockMode _previousCursorLock;
    private int _tab;
    private string _selected = "", _quantity = "1", _feedback = "";
    private bool _buy = true;
    private Vector2 _scroll;
    private float _contentHeight = 900;
    private string _lastDrawError = "";
    private static readonly Color Positive = new(.42f, .89f, .63f, 1);
    private static readonly Color Negative = new(1f, .54f, .50f, 1);
    private static readonly Color Subtle = new(.72f, .77f, .82f, 1);
    public bool IsVisible { get; private set; }

    public StockMarketPanel(IMarketHost host) => _host = host;

    public void Show()
    {
        if (_disposed || IsVisible) return;
        _previousCursorVisible = Cursor.visible;
        _previousCursorLock = Cursor.lockState;
        _cursorSaved = true;
        IsVisible = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void Hide()
    {
        IsVisible = false;
        if (!_cursorSaved) return;
        Cursor.lockState = _previousCursorLock;
        Cursor.visible = _previousCursorVisible;
        _cursorSaved = false;
    }

    public void Draw()
    {
        if (!IsVisible || _disposed) return;
        var originalColor = GUI.color;
        var originalEnabled = GUI.enabled;
        try
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            EnsureStyles();
            GUI.enabled = true;
            var width = Math.Min(1160, Math.Max(320, Screen.width - 24));
            var height = Math.Min(780, Math.Max(260, Screen.height - 24));
            var window = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
            GUI.Box(window, "", _box!);
            var x = window.x + 18;
            var top = window.y + 12;
            var innerWidth = window.width - 36;
            Label(x, top, innerWidth - 100, 30, "远星证券 · 投资面板", _heading!);
            if (Button(window.xMax - 92, top, 72, 29, "关闭")) { Hide(); return; }
            var engine = _host.Engine;
            Label(x, top + 34, innerWidth, 24,
                $"第 {Math.Max(0, engine?.State.LastDay ?? 0)} 日    可用资金：{Money(_host.Cash)}    现货交易 · 无杠杆");
            var tabs = new[] { "行情与交易", "我的持仓", "顾客内幕", "资金流水" };
            var tabWidth = Math.Min(150, (innerWidth - 12) / 4);
            for (var i = 0; i < tabs.Length; i++)
            {
                var saved = GUI.color;
                if (i == _tab) GUI.color = new Color(.65f, .89f, 1f, 1);
                if (Button(x + i * (tabWidth + 4), top + 64, tabWidth, 30, tabs[i]))
                {
                    _tab = i;
                    _scroll = Vector2.zero;
                    _contentHeight = 1000;
                }
                GUI.color = saved;
            }
            var viewport = new Rect(x, top + 105, innerWidth, Math.Max(50, window.height - 215));
            var contentWidth = Math.Max(250, viewport.width - 22);
            _scroll = GUI.BeginScrollView(viewport, _scroll, new Rect(0, 0, contentWidth, _contentHeight));
            try
            {
                if (engine == null)
                {
                    Label(8, 8, contentWidth - 16, 80, "请先进入游戏存档。行情和顾客信息将在适配层就绪后显示。");
                    _contentHeight = 110;
                }
                else
                {
                    _contentHeight = _tab switch
                    {
                        0 => DrawMarket(engine, contentWidth),
                        1 => DrawPortfolio(engine, contentWidth),
                        2 => DrawRumors(engine, contentWidth),
                        _ => DrawLedger(engine, contentWidth)
                    };
                }
            }
            finally { GUI.EndScrollView(); }

            var bottom = window.yMax - 94;
            Label(x, bottom, innerWidth - 164, 38, _host.Status, _muted!);
            if (Button(window.xMax - 174, bottom, 154, 30, "补领股市面板道具"))
                Run(_host.RecoverTerminal);
            Label(x, bottom + 40, innerWidth, 40, _fontMissing
                ? "Chinese font missing: install Microsoft YaHei / Source Han Sans SC. " + _feedback
                : _feedback, _muted!);

            var current = Event.current;
            if (current != null)
            {
                if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape) Hide();
                // 消费 IMGUI 事件，游戏原生输入的阻断由适配层负责。
                if (current.type == EventType.MouseDown || current.type == EventType.MouseUp || current.type == EventType.ScrollWheel || current.type == EventType.KeyDown)
                    current.Use();
            }
        }
        catch (Exception ex)
        {
            if (_lastDrawError != ex.ToString())
            {
                _lastDrawError = ex.ToString();
                MelonLogger.Error("股市面板绘制失败，已关闭并恢复光标：" + ex);
            }
            Hide();
        }
        finally { GUI.color = originalColor; GUI.enabled = originalEnabled; }
    }

    private float DrawMarket(MarketEngine engine, float width)
    {
        if (engine.Config.Companies.Count == 0) return 40;
        if (!engine.Config.Companies.Any(c => c.Id == _selected)) _selected = engine.Config.Companies[0].Id;
        // 窄屏采用上下布局，内容通过同一个滚动区域访问。
        var narrow = width < 760;
        var listWidth = narrow ? width : 340;
        Label(4, 0, listWidth, 24, "公司                 股价 / 较昨收");
        float y = 30;
        foreach (var company in engine.Config.Companies)
        {
            if (!engine.State.Companies.TryGetValue(company.Id, out var stock)) continue;
            var baseline = stock.History.LastOrDefault(p => p.Day < engine.State.LastDay)?.Price ?? engine.Config.InitialPrice;
            var change = baseline == 0 ? 0 : stock.Price / baseline - 1;
            var color = GUI.color;
            GUI.color = company.Id == _selected ? new Color(.65f, .89f, 1f, 1) : Color.white;
            if (Button(4, y, listWidth - 8, 37, $"{company.Name}    {stock.Price:0.00}    {SignedPercent(change)}"))
                _selected = company.Id;
            GUI.color = color;
            y += 43;
        }
        var detailX = narrow ? 4 : 364;
        var detailY = narrow ? y + 18 : 0;
        var detailWidth = width - detailX - 6;
        var selected = engine.Config.Companies.First(c => c.Id == _selected);
        var selectedStock = engine.State.Companies[_selected];
        var dy = detailY;
        Label(detailX, dy, detailWidth, 30, selected.Name + "  " + selectedStock.Price.ToString("0.00"), _heading!); dy += 36;
        Label(detailX, dy, detailWidth, 40, selected.Description); dy += 44;
        var sectors = engine.Config.Sectors.Where(s => s.Weights.ContainsKey(_selected)).ToArray();
        var sectorText = string.Join("、", sectors.Select(s => $"{s.Name} {s.Weights[_selected]:0%}"));
        Label(detailX, dy, detailWidth, 65, "关联商品与分配权重：" + sectorText); dy += 69;
        var peers = sectors.SelectMany(s => s.Weights.Keys).Where(id => id != _selected).Distinct();
        var peerNames = peers.Select(id => engine.Config.Companies.FirstOrDefault(c => c.Id == id)?.Name ?? id);
        Label(detailX, dy, detailWidth, 50, "共同关联公司：" + string.Join("、", peerNames)); dy += 56;
        Label(detailX, dy, detailWidth, 24, "近期价格（最近 24 次变化）", _muted!); dy += 27;
        DrawPriceHistory(new Rect(detailX, dy, detailWidth, 110), selectedStock); dy += 118;
        DrawOrder(engine, detailX, ref dy, detailWidth);
        Label(detailX, dy, detailWidth, 26, "已公开事件", _heading!); dy += 32;
        var news = engine.State.Ledger.Where(e => e.Kind == "event" && (string.IsNullOrEmpty(e.CompanyId) || e.CompanyId == _selected)).Reverse().Take(8).ToArray();
        if (news.Length == 0) { Label(detailX, dy, detailWidth, 35, "暂无已公开事件记录。", _muted!); dy += 40; }
        foreach (var item in news) { Label(detailX, dy, detailWidth, 52, $"第 {item.Day} 日  {item.Description}"); dy += 57; }
        return Math.Max(y, dy) + 20;
    }

    private void DrawOrder(MarketEngine engine, float x, ref float y, float width)
    {
        Label(x, y, width, 26, "现货委托", _heading!); y += 32;
        if (Button(x, y, 84, 29, _buy ? "✓ 买入" : "买入")) _buy = true;
        if (Button(x + 90, y, 84, 29, !_buy ? "✓ 卖出" : "卖出")) _buy = false;
        if (width < 390) y += 35;
        var quantityX = width < 390 ? x : x + 184;
        Label(quantityX, y, 54, 29, "股数");
        _quantity = GUI.TextField(new Rect(quantityX + 54, y, Math.Min(130, Math.Max(60, width - (quantityX - x) - 54)), 29), _quantity, 9, _field!);
        _quantity = new string(_quantity.Where(c => c >= '0' && c <= '9').ToArray());
        y += 36;
        var valid = int.TryParse(_quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) && quantity > 0;
        var quote = engine.QuoteOrder(_selected, _buy ? OrderSide.Buy : OrderSide.Sell, valid ? quantity : 0, _host.Cash);
        if (valid && quote.UnitPrice > 0 && (quote.Success || quote.CashDelta != 0))
        {
            Label(x, y, width, 46, $"单价 {quote.UnitPrice:0.00} · 手续费 {quote.Fee:0.00}\n{(_buy ? "支出" : "到账")} {Math.Abs((long)quote.CashDelta)} · 成交后余额 {(long)_host.Cash + quote.CashDelta}");
            y += 52;
        }
        else { Label(x, y, width, 28, valid ? "当前订单无法生成有效的金额预览。" : "请输入正整数股数。"); y += 34; }
        var enabled = GUI.enabled;
        GUI.enabled = enabled && valid && quote.Success && _host.CanTrade;
        if (Button(x, y, Math.Min(210, width), 32, _buy ? "确认买入" : "确认卖出"))
            Run(() => _host.PlaceOrder(_selected, quantity, _buy));
        GUI.enabled = enabled;
        y += 38;
        if (!quote.Success) { Label(x, y, width, 42, quote.Message, _muted!); y += 45; }
        y += 12;
    }

    private float DrawPortfolio(MarketEngine engine, float width)
    {
        decimal value = 0, cost = 0, realized = 0;
        foreach (var pair in engine.State.Positions)
        {
            if (engine.State.Companies.TryGetValue(pair.Key, out var stock)) value += stock.Price * pair.Value.Quantity;
            cost += pair.Value.Cost;
            realized += pair.Value.RealizedProfit;
        }
        Label(4, 0, width - 8, 56, $"持仓市值：{Money(value)}    持仓成本：{Money(cost)}\n浮动盈亏：{SignedMoney(value - cost)}    已实现盈亏：{SignedMoney(realized)}");
        float y = 68;
        var positions = engine.Config.Companies.Where(c => engine.State.Positions.ContainsKey(c.Id)).ToArray();
        if (positions.Length == 0) { Label(4, y, width - 8, 40, "暂无持仓。可以在行情页选择公司买入。"); return y + 50; }
        foreach (var company in positions)
        {
            var position = engine.State.Positions[company.Id];
            var price = engine.State.Companies[company.Id].Price;
            var marketValue = price * position.Quantity;
            Label(8, y, width - 16, 30, company.Name + "    " + position.Quantity + " 股", _heading!); y += 34;
            Label(8, y, width - 16, 64, $"现价 {price:0.00}    平均成本 {(position.Quantity > 0 ? position.Cost / position.Quantity : 0):0.00}\n成本 {Money(position.Cost)}    市值 {Money(marketValue)}    浮盈 {SignedMoney(marketValue - position.Cost)}\n已实现盈亏 {SignedMoney(position.RealizedProfit)}"); y += 70;
            if (Button(8, y, 132, 29, "查看 / 交易")) { _selected = company.Id; _tab = 0; _scroll = Vector2.zero; }
            y += 48;
        }
        return y + 12;
    }

    private float DrawRumors(MarketEngine engine, float width)
    {
        float y = 0;
        Label(4, y, width - 8, 28, "当前来访顾客", _heading!); y += 34;
        Label(4, y, width - 8, 42, _host.SellerDescription); y += 46;
        Label(4, y, width - 8, 42, _host.SellerReputation, _muted!); y += 46;
        // 未购报价只读取适配层提供的安全摘要，不接触未来事件和真假依据。
        Label(4, y, width - 8, 54, _host.OfferDescription); y += 58;
        var enabled = GUI.enabled;
        GUI.enabled = enabled && _host.CanBuyRumor && _host.CanTrade;
        if (Button(4, y, Math.Min(240, width - 8), 32, _host.OfferCost.HasValue ? $"购买情报 · {_host.OfferCost.Value} 游戏币" : "暂无可购买的情报"))
            Run(_host.BuyRumor);
        GUI.enabled = enabled;
        y += 46;
        Label(4, y, width - 8, 28, "情报簿", _heading!); y += 34;
        var purchased = engine.GetRumors().Where(r => r.Purchased).Reverse().ToArray();
        if (purchased.Length == 0) { Label(4, y, width - 8, 36, "购买后的线索会保留在此处，到期按事件事实验证。", _muted!); return y + 48; }
        foreach (var rumor in purchased)
        {
            var resolution = rumor.Resolution switch { RumorResolution.True => "属实", RumorResolution.False => "失实", RumorResolution.Unverified => "未能验证", _ => "等待验证" };
            Label(8, y, width - 16, 28, $"{rumor.SellerName} · {rumor.Identity} · {resolution}", _heading!); y += 32;
            Label(8, y, width - 16, 43, $"{(rumor.IsGroupIdentity ? "同类来源信誉" : "来源信誉")} {rumor.Reliability:0%} · 验证 {rumor.Correct + rumor.Incorrect} 次 · 观察至第 {rumor.DueDay} 日", _muted!); y += 46;
            Label(8, y, width - 16, 70, rumor.Content); y += 74;
            var companies = rumor.CompanyIds.Select(id => engine.Config.Companies.FirstOrDefault(c => c.Id == id)?.Name ?? id);
            Label(8, y, width - 16, 38, "关联公司：" + string.Join("、", companies)); y += 50;
        }
        return y + 10;
    }

    private float DrawLedger(MarketEngine engine, float width)
    {
        float y = 0;
        Label(4, y, width - 8, 28, "近期流水（新记录在前）", _heading!); y += 35;
        if (engine.State.Ledger.Count == 0) { Label(4, y, width - 8, 35, "暂无流水。"); return y + 45; }
        foreach (var entry in engine.State.Ledger.AsEnumerable().Reverse())
        {
            var kind = entry.Kind switch { "event" => "事件", "day" => "日行情", "customer" => "顾客结算", "buy" => "买入", "sell" => "卖出", "rumor" => "情报", _ => entry.Kind };
            Label(4, y, width - 8, 26, $"第 {entry.Day} 日 · {kind}" + (entry.CashDelta == 0 ? "" : " · 资金 " + SignedMoney(entry.CashDelta)), _muted!); y += 28;
            Label(4, y, width - 8, 58, entry.Description); y += 66;
        }
        return y + 10;
    }

    private void DrawPriceHistory(Rect rect, CompanyState stock)
    {
        GUI.Box(rect, "", _box!);
        var points = stock.History.TakeLast(24).ToArray();
        if (points.Length == 0) { Label(rect.x + 8, rect.y + 30, rect.width - 16, 40, "行情记录将在成交或事件后生成。", _muted!); return; }
        var min = points.Min(p => p.Price);
        var max = points.Max(p => p.Price);
        var span = Math.Max(max - min, 1m);
        var barWidth = (rect.width - 16) / points.Length;
        var old = GUI.color;
        for (var i = 0; i < points.Length; i++)
        {
            var h = 12 + (float)((points[i].Price - min) / span) * (rect.height - 43);
            GUI.color = i == 0 || points[i].Price >= points[i - 1].Price ? Positive : Negative;
            GUI.DrawTexture(new Rect(rect.x + 8 + i * barWidth, rect.yMax - 23 - h, Math.Max(1, barWidth - 2), h), Texture2D.whiteTexture);
        }
        GUI.color = old;
        Label(rect.x + 8, rect.yMax - 22, rect.width - 16, 22, $"{min:0.00} ~ {max:0.00}     第 {points[0].Day} 日 → 第 {points[^1].Day} 日", _muted!);
    }

    private void EnsureStyles()
    {
        if (_label != null) return;
        EnsureFont();
        _label = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true, richText = false };
        _label.normal.textColor = Color.white;
        _heading = new GUIStyle(_label) { fontSize = 19, fontStyle = FontStyle.Bold };
        _muted = new GUIStyle(_label) { fontSize = 14 };
        _muted.normal.textColor = Subtle;
        _button = new GUIStyle(GUI.skin.button) { fontSize = 15, wordWrap = true };
        _field = new GUIStyle(GUI.skin.textField) { fontSize = 16 };
        _box = new GUIStyle(GUI.skin.box);
        if (_font != null)
            foreach (var style in new[] { _label, _heading, _muted, _button, _field, _box }) style.font = _font;
    }

    private void EnsureFont()
    {
        if (_fontInitialized) return;
        _fontInitialized = true;
        var custom = Path.Combine(ModPaths.AssetsDirectory, "Fonts", "StockMarket.ttf");
        if (File.Exists(custom))
        {
            try { var fileFont = new Font(custom); if (AcceptFont(fileFont)) { _font = fileFont; return; } }
            catch (Exception ex) { MelonLogger.Warning("自定义股市字体加载失败：" + ex.Message); }
        }
        var preferred = new[] { "Microsoft YaHei", "Microsoft YaHei UI", "Source Han Sans SC", "Source Han Sans CN", "Noto Sans CJK SC", "Noto Sans SC", "SimHei", "SimSun", "PingFang SC", "Arial Unicode MS" };
        var installed = Array.Empty<string>();
        try { installed = Font.GetOSInstalledFontNames().ToArray(); }
        catch (Exception ex) { MelonLogger.Warning("读取字体列表失败，将尝试常用中文字体：" + ex.Message); }
        var candidates = preferred.Concat(installed.Where(n => preferred.Any(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)))).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in candidates)
        {
            try
            {
                var candidate = Font.CreateDynamicFontFromOSFont(name, 18);
                if (!AcceptFont(candidate)) continue;
                _font = candidate;
                MelonLogger.Msg("股市面板中文字体：" + name);
                return;
            }
            catch { /* 字体不可用时继续尝试下一个，保持游戏原字体回退。 */ }
        }
        _fontMissing = true;
        MelonLogger.Warning("未找到包含中文字符的股市字体。请安装 Microsoft YaHei / Source Han Sans SC，或放置字体：" + custom);
    }

    private static bool AcceptFont(Font? font)
    {
        if (font != null && font.HasCharacter('股') && font.HasCharacter('市') && font.HasCharacter('霆')) return true;
        if (font != null) UnityEngine.Object.Destroy(font);
        return false;
    }

    private void Run(Func<string> action)
    {
        try { _feedback = action(); }
        catch (Exception ex) { _feedback = "操作未完成，请查看加载日志。"; MelonLogger.Error("股市面板命令失败：" + ex); }
    }
    private bool Button(float x, float y, float width, float height, string text) => GUI.Button(new Rect(x, y, width, height), text, _button!);
    private void Label(float x, float y, float width, float height, string text, GUIStyle? style = null) => GUI.Label(new Rect(x, y, Math.Max(1, width), height), text ?? "", style ?? _label!);
    private static string Money(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string SignedMoney(decimal value) => (value > 0 ? "+" : "") + Money(value);
    private static string SignedPercent(decimal value) => (value > 0 ? "+" : "") + value.ToString("0.00%", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        if (_font != null) UnityEngine.Object.Destroy(_font);
        _font = null;
    }
}
