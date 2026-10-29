using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using ProbablyStolenStockMarket.Contracts;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Market;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ProbablyStolenStockMarket.UI;

// 确认页保留报价快照，最终点击时重新核对，防止跨日或读档后执行旧合同。
internal sealed class NativeLoanPanel : IDisposable
{
    private const string WindowId = "ProbablyStolenStockMarket.loan.native";
    private const string StartingDebtRules = "开局现金为 1500 元，固定旧债为 5000 元，不再额外发放本金。\n\n从首个营业日起共有 28 个游戏日可还款。零利息、零罚息，无宽限期。可随时部分还款或提前结清，部分还款不延长期限。\n\n最后营业日的经营费用结算后，现金足够时自动结清；否则进入“抵债停业”结局。欠款结清前不能申请新贷款。";
    private readonly ILoanHost _host;
    private readonly NativeUiBootstrap _bootstrap = new();
    private readonly Dictionary<string, CustomUIElement> _elements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _staticTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _collateralInputs = new(StringComparer.Ordinal);
    private readonly List<Action> _unbind = new();
    private readonly List<object> _roots = new();
    private readonly Action _managedClosed;
    private Action? _nativeClosed;
    private CustomUIWindow? _window;
    private CustomUIBuilder? _builder;
    private MarketEngine? _engine;
    private GameObject? _applicationRoot, _accountRoot, _collateralRoot, _confirmationRoot, _businessRoot, _documentRoot;
    private Texture2D? _paperTexture, _sealTexture;
    private bool _artworkLoaded, _showDocument, _resetScroll;
    private static readonly Color PaperInk = new(.20f, .16f, .13f, 1);
    private static readonly Color PaperRed = new(.43f, .13f, .15f, 1);
    private LoanQuote? _pendingQuote;
    private string _identity = "", _draftIdentity = "", _amount = "100", _days = "7", _repayment = "", _feedback = "";
    private LoanKind _kind = LoanKind.Unsecured;
    private bool _visible, _disposed, _refreshing, _cursorSaved, _oldCursorVisible;
    private CursorLockMode _oldCursorLock;
    private float _width, _nextRefresh;
    private int _languageRevision;

    internal NativeLoanPanel(ILoanHost host) { _host = host; _managedClosed = OnClosed; }
    internal bool IsVisible => _visible;

    internal void Show(bool contractDocument = false)
    {
        if (_disposed) return;
        try
        {
            if (_visible && IsCurrentContext()) { _showDocument = contractDocument; _resetScroll = true; Refresh(); _window?.BringToFront(); return; }
            Hide();
            _engine = _host.Engine;
            if (_engine == null) return;
            _identity = _host.LoanSaveIdentity;
            if (_draftIdentity != _identity)
            {
                _draftIdentity = _identity; _amount = "100"; _days = "7"; _kind = LoanKind.Unsecured;
                _collateralInputs.Clear();
            }
            _pendingQuote = null; _feedback = ""; _showDocument = contractDocument; _resetScroll = true;
            _repayment = _engine.State.Loan.Contract?.AmountToClose.ToString(CultureInfo.InvariantCulture) ?? "";
            Build();
            _oldCursorVisible = Cursor.visible; _oldCursorLock = Cursor.lockState; _cursorSaved = true;
            Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
            _visible = true; _host.LoanWindowChanged(true);
            _window!.Show(); _window.BringToFront(); Refresh();
        }
        catch (Exception ex) { Report("打开贷款窗口失败", ex); Hide(); }
    }

    internal void Tick()
    {
        if (!_visible || _disposed) return;
        try
        {
            if (!IsCurrentContext() || _window == null || !_window.IsAlive || !_window.IsVisible)
            { Hide(); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { Hide(); return; }
            Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
            if (_languageRevision != Localizer.Revision)
            {
                ReleaseWindow(true); Build(); _window!.Show(); _window.BringToFront();
            }
            if (Time.unscaledTime >= _nextRefresh) { Refresh(); _nextRefresh = Time.unscaledTime + .25f; }
        }
        catch (Exception ex) { Report("更新贷款窗口失败", ex); Hide(); }
    }

    internal void Hide()
    {
        _visible = false; _pendingQuote = null;
        try { ReleaseWindow(true); }
        finally
        {
            if (_cursorSaved) { Cursor.lockState = _oldCursorLock; Cursor.visible = _oldCursorVisible; _cursorSaved = false; }
            _engine = null; _identity = ""; _host.LoanWindowChanged(false);
        }
    }

    private void Build()
    {
        var manager = _bootstrap.Resolve();
        if (manager == null || manager.windowPrefab == null || manager.labelPrefab == null || manager.buttonPrefab == null
            || manager.inputFieldPrefab == null || manager.scrollViewPrefab == null) throw new InvalidOperationException("贷款窗口原生控件尚未就绪。");
        var engine = _engine ?? throw new InvalidOperationException("贷款窗口没有活动存档。");
        var bounds = manager.GetLayerRoot(CustomUIManager.LAYER_OVERLAY)?.rect ?? new Rect(0, 0, Screen.width, Screen.height);
        _width = Math.Min(800, Math.Max(360, bounds.width - 32));
        var height = Math.Min(760, Math.Max(350, bounds.height - 32));
        _builder = CustomUIManager.Window(WindowId, Localizer.Text("周转贷款 · 731207"), CustomUIManager.LAYER_OVERLAY);
        _window = _builder.Handle;
        _builder.SetSize(_width, height).Center().SetDraggable(true).SetCloseOnEscape(false);
        _nativeClosed = _managedClosed;
        _window.onClose = _nativeClosed;
        StyleFrame();
        Label("summary", 32, true);
        _builder.BeginScroll(Math.Max(120, height - 175));
        NativeStockMarketPanel.Container(_builder.CurrentContainer, -1);
        Button("document.open", "打开贷款合同", () => { _showDocument = true; _resetScroll = true; Refresh(); }, -1);
        BuildDocument();
        _builder.BeginColumn(8); _businessRoot = _builder.CurrentContainer.gameObject;
        Label("status", 28);

        _builder.BeginColumn(8); _applicationRoot = _builder.CurrentContainer.gameObject;
        _builder.BeginRow(6);
        Button("unsecured", "无抵押贷款", () => ChooseKind(LoanKind.Unsecured), 140);
        Button("secured", "抵押贷款", () => ChooseKind(LoanKind.Secured), 140);
        _builder.End();
        Label("amount.title", 24, value: "贷款本金");
        InputField("amount", "输入借款金额", _amount, value => { _amount = value; InvalidateQuote(); });
        Label("days.title", 24, value: "贷款期限：1～28 个游戏日，最多 4 周");
        InputField("days", "输入贷款天数", _days, value => { _days = value; InvalidateQuote(); }, 2);
        _builder.BeginRow(6);
        foreach (var days in new[] { 7, 14, 21, 28 })
        {
            var chosen = days;
            Button("days." + days, days + " 天", () => SetDays(chosen), Math.Max(58, (_width - 90) / 4));
        }
        _builder.End();
        _builder.BeginColumn(5); _collateralRoot = _builder.CurrentContainer.gameObject;
        Label("collateral.title", 44, value: "选择抵押股份。只锁定所选数量，全部结清后解除抵押。没有可靠历史收盘价的股票暂不接受抵押。");
        foreach (var company in engine.Config.Companies)
        {
            var id = company.Id;
            if (!_collateralInputs.ContainsKey(id)) _collateralInputs[id] = "0";
            _builder.BeginRow(8);
            Label("shares." + id, 42, width: Math.Max(150, _width - 220));
            InputField("collateral." + id, "抵押股数", _collateralInputs[id], value => { _collateralInputs[id] = value; InvalidateQuote(); }, 9, 110);
            _builder.End();
        }
        _builder.End();
        Label("quote", 150);
        Button("review", "查看合同并确认", Review, -1);
        _builder.BeginColumn(6); _confirmationRoot = _builder.CurrentContainer.gameObject;
        Label("confirmation", 130, true);
        Button("confirm", "确认签约并放款", Confirm, -1);
        Button("cancel", "返回修改合同", () => { _pendingQuote = null; Refresh(); }, -1);
        _builder.End();
        _builder.End();

        _builder.BeginColumn(8); _accountRoot = _builder.CurrentContainer.gameObject;
        Label("contract", 180);
        Label("repayment.title", 24, value: "还款金额（先偿还罚息、再偿还利息和本金）");
        InputField("repayment", "输入还款金额", _repayment, value => _repayment = value);
        Button("repay", "偿还指定金额", () => Repay(false), -1);
        Button("repay.all", "全部结清并解除抵押", () => Repay(true), -1);
        _builder.End();
        Label("rules", 150, value: "贷款按剩余本金计算单利，放款当天不计息。期限越长，签约日利率越高；提前还款按实际计息天数和签约利率计算，无手续费。\n\n到期后有 2 天宽限期，第 3 天开始罚息或处置抵押股份。到期后第 7 天仍可营业还款，第 8 天开始时仍有欠款，将触发“抵债停业”失败结局。部分还款不会重置期限。\n\n默认逾期罚息为每日剩余本金的 0.5%，累计罚息不超过原始本金的 10%，罚息不再产生利息。抵押股份按处置时市价出售，扣除正常交易手续费后偿债，多余资金返还；不足部分继续作为欠款，原清算期限不变。具体罚息与处置手续费以签约合同为准。\n\n同一时间只能持有一笔贷款，不接受展期或追加本金。结清后等待 3 天才能重新申请。");
        _builder.End();
        _builder.End();
        Label("feedback", 34);
        Button("close", "结束通话", Hide, -1);
        NativeStockMarketPanel.NormalizeLayout(_window.frame.contentRoot);
        _window = _builder.Show(); _builder = null;
        _languageRevision = Localizer.Revision;
    }

    private void Refresh()
    {
        if (_refreshing || !_visible || _engine == null || _window == null) return;
        _refreshing = true;
        try
        {
            var engine = _engine;
            var contract = engine.State.Loan.Contract;
            bool startingDebt = contract?.Source == LoanSource.StartingDebt;
            var document = contract ?? engine.State.Loan.LastClosedContract;
            if (document == null) _showDocument = false;
            _documentRoot?.SetActive(_showDocument);
            _businessRoot?.SetActive(!_showDocument);
            _elements["document.open"].root.SetActive(!_showDocument && document != null);
            if (_showDocument && document != null) RefreshDocument(document);
            foreach (var pair in _staticTexts) Text(pair.Key, pair.Value);
            string accountName = startingDebt ? "负债店主" : "周转贷款";
            if (_window.frame.titleText != null) _window.frame.titleText.text = Localizer.Text(accountName + " · 731207");
            Text("summary", $"第 {engine.State.LastDay} 日 · 可用现金 {_host.LoanCash} · {accountName}");
            Text("status", startingDebt ? "请在最后营业日结束前准备足够现金结清旧债，当前不能申请新贷款。"
                : contract != null ? "你当前还有一笔贷款未结清，暂时无法办理新的贷款。" : _host.LoanStatus);
            if (startingDebt)
            {
                Text("repayment.title", "还款金额（直接抵扣固定欠款）");
                Text("rules", StartingDebtRules);
            }
            Text("repay.all", startingDebt ? "全部结清" : "全部结清并解除抵押");
            _applicationRoot?.SetActive(contract == null && _host.LoanContactUnlocked);
            _accountRoot?.SetActive(contract != null);
            _collateralRoot?.SetActive(_kind == LoanKind.Secured);
            _confirmationRoot?.SetActive(_pendingQuote != null && contract == null);
            SetButtonSelected("unsecured", _kind == LoanKind.Unsecured);
            SetButtonSelected("secured", _kind == LoanKind.Secured);
            foreach (var company in engine.Config.Companies)
            {
                var quantity = engine.State.Positions.TryGetValue(company.Id, out var position) ? position.Quantity : 0;
                var available = engine.GetAvailableShares(company.Id);
                var value = engine.GetCollateralUnitValue(company.Id);
                Text("shares." + company.Id, $"{company.Name}\n持有 {quantity} / 可用 {available} / 抵押估值 {Money(value)}");
            }
            if (contract == null)
            {
                var quote = Quote(out var inputError);
                Text("quote", QuoteText(quote, inputError));
                Enabled("review", _host.CanUseLoans && quote?.Success == true && inputError.Length == 0);
                Enabled("confirm", _host.CanUseLoans && _pendingQuote != null);
                if (_pendingQuote != null) Text("confirmation", "请核对合同，确认后资金将立即到账：\n\n" + QuoteText(_pendingQuote, ""));
            }
            else
            {
                var text = new StringBuilder();
                if (startingDebt)
                {
                    text.AppendLine("负债店主 · 开局债务");
                    text.AppendLine($"原始欠款 {contract.OriginalPrincipal} · 剩余欠款 {contract.AmountToClose}");
                    text.AppendLine($"最后还款日：第 {contract.DueDay} 日营业结束");
                    text.AppendLine(engine.State.LastDay >= contract.DueDay ? "今天营业结束后必须结清。"
                        : $"距最后还款日还有 {contract.DueDay - engine.State.LastDay} 天（含今天仍可继续营业）。");
                    text.AppendLine("零利息、零罚息，无宽限期。");
                }
                else
                {
                    text.AppendLine(contract.Kind == LoanKind.Secured ? "抵押贷款" : "无抵押贷款");
                    text.AppendLine($"原始本金 {contract.OriginalPrincipal} · 剩余本金 {Money(contract.RemainingPrincipal)}");
                    text.AppendLine($"累计利息 {Money(contract.AccruedInterest)} · 累计罚息 {Money(contract.AccruedPenalty)}");
                    text.AppendLine($"日利率 {Percent(contract.DailyRate)} · 当前结清金额 {contract.AmountToClose}");
                    text.AppendLine($"放款日 {contract.StartDay} · 到期日 {contract.DueDay} · 最终清算日 {contract.EndingDay}");
                    text.AppendLine(RiskText(contract));
                    if (engine.State.LastDay > contract.DueDay) text.AppendLine($"已经逾期 {engine.State.LastDay - contract.DueDay} 天，部分还款不会延长清算期限。");
                }
                foreach (var pair in contract.Collateral)
                {
                    var name = engine.Config.Companies.FirstOrDefault(c => c.Id == pair.Key)?.Name ?? pair.Key;
                    text.AppendLine($"{name}：{pair.Value} 股" + (contract.CollateralDisposed ? "（已处置）" : "（已锁定）"));
                }
                Text("contract", text.ToString());
                Enabled("repay", _host.CanUseLoans && TryPositive(_repayment, out _));
                Enabled("repay.all", _host.CanUseLoans && _host.LoanCash >= contract.AmountToClose);
            }
            Text("feedback", _feedback);
            if (_resetScroll)
            {
                Canvas.ForceUpdateCanvases();
                foreach (var scroll in _window.root.GetComponentsInChildren<ScrollRect>(true))
                {
                    if (scroll.content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
                    scroll.verticalNormalizedPosition = 1;
                }
                _resetScroll = false;
            }
        }
        finally { _refreshing = false; }
    }

    private void BuildDocument()
    {
        _builder!.BeginColumn(14); _documentRoot = _builder.CurrentContainer.gameObject;
        var paper = _builder.CurrentContainer;
        NativeStockMarketPanel.Container(paper, -1);
        var layout = paper.GetComponent<VerticalLayoutGroup>();
        if (layout != null) layout.padding = new RectOffset(38, 38, 34, 38);
        LoadContractArtwork();
        AddArtwork(paper, _paperTexture, "ContractPaper", new Color(.97f, .91f, .77f, 1), true);
        PaperLabel("document.title", 40, 25, PaperRed, TextAlignmentOptions.Center);
        PaperLabel("document.number", 32, 11, PaperInk, TextAlignmentOptions.Center);
        PaperLabel("document.terms.title", 30, 18, PaperRed);
        PaperLabel("document.terms", 380, 15, PaperInk);
        PaperLabel("document.account.title", 30, 18, PaperRed);
        PaperLabel("document.account", 90, 15, PaperInk);
        _builder.BeginRow(12);
        _builder.BeginColumn(4);
        NativeStockMarketPanel.Container(_builder.CurrentContainer, Math.Max(90, (_width - 140) / 2));
        PaperLabel("document.lender", 32, 13, PaperRed);
        var seal = new GameObject("LoanOfficeSeal", typeof(RectTransform), typeof(LayoutElement), typeof(RawImage));
        seal.transform.SetParent(_builder.CurrentContainer, false);
        NativeStockMarketPanel.Container(seal.GetComponent<RectTransform>(), 106, 106);
        var sealImage = seal.GetComponent<RawImage>();
        sealImage.texture = _sealTexture; sealImage.color = Color.white; sealImage.raycastTarget = false;
        seal.SetActive(_sealTexture != null);
        _builder.End();
        _builder.BeginColumn(4);
        NativeStockMarketPanel.Container(_builder.CurrentContainer, -1);
        PaperLabel("document.signature.space", 64, 12, PaperInk);
        PaperLabel("document.signature.title", 24, 12, PaperInk, TextAlignmentOptions.TopRight);
        PaperLabel("document.signature", 48, 24, PaperInk, TextAlignmentOptions.TopRight);
        var signature = _elements["document.signature"].text;
        if (signature != null)
        {
            signature.fontStyle = FontStyles.Italic;
            signature.enableAutoSizing = true; signature.fontSizeMin = 12; signature.fontSizeMax = 24;
        }
        PaperLabel("document.signed", 28, 11, PaperRed, TextAlignmentOptions.TopRight);
        _builder.End();
        _builder.End();
        PaperLabel("document.footer", 44, 11, PaperInk, TextAlignmentOptions.Center);
        Button("document.back", "返回贷款业务", () => { _showDocument = false; _resetScroll = true; Refresh(); }, -1);
        _builder.End();
    }

    private void PaperLabel(string id, float height, float size, Color color, TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
    {
        Label(id, height);
        var text = _elements[id].text;
        if (text == null) return;
        text.fontSize = size; text.color = color; text.alignment = alignment;
        text.lineSpacing = 4;
    }

    private void RefreshDocument(LoanContract contract)
    {
        // 昵称及已翻译的条款直接进入纯文本控件，避免把玩家昵称当作待翻译文案。
        Text("document.title", contract.Source == LoanSource.StartingDebt ? "旧债确认书" : "贷款合同");
        Text("document.number", LoanContractDocument.Number(contract), false);
        Text("document.terms.title", "签约条款");
        Text("document.terms", LoanContractDocument.Terms(contract, Localizer.Text,
            id => Localizer.Text(_engine!.Config.Companies.FirstOrDefault(c => c.Id == id)?.Name ?? id)), false);
        Text("document.account.title", "即时账单");
        Text("document.account", LoanContractDocument.Account(contract, _engine!.State.LastDay, Localizer.Text), false);
        Text("document.lender", "贷款专员 · 沈衡");
        Text("document.signature.title", "借款人签名");
        string name = LoanContractDocument.NormalizeBorrowerName(contract.BorrowerName);
        Text("document.signature", name == LoanContractDocument.DefaultBorrowerName ? Localizer.Text(name) : name, false);
        Text("document.signed", contract.ClosedDay >= 0 ? "已结清" : "签约当日留存");
        Text("document.footer", "本合同随存档保存，可通过贷款专线或日历重新查阅。");
    }

    private void LoadContractArtwork()
    {
        if (_artworkLoaded) return;
        _artworkLoaded = true;
        _paperTexture = LoadTexture("loan_contract_paper.png");
        _sealTexture = LoadTexture("loan_contract_seal.png");
    }

    private static Texture2D? LoadTexture(string filename)
    {
        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                { name = filename, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(texture, ModPaths.ReadAssetBytes("Textures", "UI", filename), false))
                throw new InvalidOperationException("合同图片解码失败。");
            return texture;
        }
        catch (Exception ex)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
            Report("读取合同素材失败：" + filename, ex); return null;
        }
    }

    private static void AddArtwork(RectTransform parent, Texture2D? texture, string name, Color fallback, bool background)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(LayoutElement), typeof(RawImage));
        root.transform.SetParent(parent, false);
        if (background) root.transform.SetAsFirstSibling();
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
        root.GetComponent<LayoutElement>().ignoreLayout = true;
        var image = root.GetComponent<RawImage>();
        image.texture = texture; image.color = texture == null ? fallback : Color.white; image.raycastTarget = false;
    }

    private LoanQuote? Quote(out string error)
    {
        error = "";
        if (_engine == null) { error = "请先进入游戏存档。"; return null; }
        if (!TryPositive(_amount, out var principal)) error = "请输入有效的正整数借款金额。";
        if (!TryPositive(_days, out var days) || days > 28) error = "贷款期限必须为 1～28 天。";
        var collateral = new Dictionary<string, int>(StringComparer.Ordinal);
        if (_kind == LoanKind.Secured)
            foreach (var pair in _collateralInputs)
            {
                if (!int.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity < 0)
                { error = "抵押数量必须为非负整数。"; continue; }
                if (quantity > 0) collateral[pair.Key] = quantity;
            }
        return _engine.QuoteLoan(_kind, principal, days, collateral);
    }

    private string QuoteText(LoanQuote? quote, string error)
    {
        if (quote == null) return error;
        var text = new StringBuilder();
        text.AppendLine(quote.Kind == LoanKind.Secured ? "抵押贷款报价" : "无抵押贷款报价");
        text.AppendLine($"服务费用基准 {Money(quote.ServiceBaseline)} · 最高可借 {quote.MaximumPrincipal}");
        if (quote.Kind == LoanKind.Secured) text.AppendLine($"所选股票估值 {Money(quote.CollateralValue)}，抵押率最高 50%。");
        text.AppendLine($"到账本金 {quote.Principal} · 期限 {quote.DurationDays} 天 · 日利率 {Percent(quote.DailyRate)}");
        text.AppendLine($"预计利息 {Money(quote.ExpectedInterest)} · 到期应还 {quote.ExpectedRepayment}");
        text.AppendLine($"到期日 {quote.DueDay} · 最终清算日 {quote.EndingDay}");
        text.AppendLine(RiskText());
        text.Append(error.Length > 0 ? error : quote.Message);
        return text.ToString();
    }

    private string RiskText(LoanContract? contract = null)
    {
        var engine = _engine;
        if (engine == null) return "";
        var penaltyRate = contract?.PenaltyDailyRate ?? engine.Config.Loans.PenaltyDailyRate;
        var penaltyCap = contract?.PenaltyCapRate ?? engine.Config.Loans.PenaltyCapRate;
        var feeRate = contract?.LiquidationFeeRate ?? engine.Config.FeeRate;
        var minimumFee = contract?.LiquidationMinimumFee ?? engine.Config.MinimumFee;
        return $"罚息：每日剩余本金的 {Percent(penaltyRate)}，累计不超过原始本金的 {Percent(penaltyCap)}，罚息不计复利。\n抵押清算：每笔股票处置按成交额的 {Percent(feeRate)} 收取手续费，最低 {minimumFee} 游戏币。多余资金返还，不足部分继续欠款，原清算期限不变。";
    }

    private void Review()
    {
        if (!IsCurrentContext() || !_host.CanUseLoans) return;
        var quote = Quote(out var error);
        if (quote?.Success != true || error.Length > 0) { _feedback = error.Length > 0 ? error : quote?.Message ?? "贷款报价不可用。"; Refresh(); return; }
        _pendingQuote = quote; _feedback = "合同尚未签署，请点击“确认签约并放款”。"; Refresh();
    }

    private void Confirm()
    {
        var pending = _pendingQuote;
        // 先消费确认，原生按钮回调重入时无法再次放款。
        _pendingQuote = null;
        if (pending == null || !IsCurrentContext() || !_host.CanUseLoans || pending.Day != _engine!.State.LastDay)
        { _feedback = "合同确认已失效，请重新查看报价。"; Refresh(); return; }
        var current = Quote(out var error);
        if (current?.Success != true || error.Length > 0)
        { _feedback = error.Length > 0 ? error : current?.Message ?? "贷款报价不可用。"; Refresh(); return; }
        if (!SameQuote(pending, current))
        { _feedback = "贷款报价已经变化，请重新核对合同。"; _pendingQuote = current; Refresh(); return; }
        _feedback = _host.TakeLoan(current.Kind, current.Principal, current.DurationDays, new Dictionary<string, int>(current.Collateral));
        if (_engine.State.Loan.Contract is { } contract)
        {
            _repayment = contract.AmountToClose.ToString(CultureInfo.InvariantCulture);
            _elements["repayment"].SetString(_repayment, false);
            _showDocument = true; _resetScroll = true;
        }
        Refresh();
    }

    private void Repay(bool all)
    {
        if (!IsCurrentContext() || !_host.CanUseLoans || _engine!.State.Loan.Contract is not { } contract) return;
        if (!all && !TryPositive(_repayment, out _)) { _feedback = "请输入有效的正整数还款金额。"; Refresh(); return; }
        var amount = all ? contract.AmountToClose : int.Parse(_repayment, CultureInfo.InvariantCulture);
        _feedback = _host.RepayLoan(amount); Refresh();
    }

    private void ChooseKind(LoanKind kind) { _kind = kind; InvalidateQuote(); }
    private void SetDays(int days)
    {
        _days = days.ToString(CultureInfo.InvariantCulture);
        _elements["days"].SetString(_days, false); InvalidateQuote();
    }
    private void InvalidateQuote() { _pendingQuote = null; _feedback = ""; if (!_refreshing) Refresh(); }
    private bool IsCurrentContext() => _engine != null && ReferenceEquals(_engine, _host.Engine) && _identity == _host.LoanSaveIdentity;
    private static bool SameQuote(LoanQuote a, LoanQuote b) => a.Day == b.Day && a.Kind == b.Kind && a.Principal == b.Principal
        && a.DurationDays == b.DurationDays && a.DailyRate == b.DailyRate && a.DueDay == b.DueDay && a.EndingDay == b.EndingDay
        && a.MaximumPrincipal == b.MaximumPrincipal && a.ExpectedRepayment == b.ExpectedRepayment && a.CollateralValue == b.CollateralValue
        && a.Collateral.Count == b.Collateral.Count && a.Collateral.All(p => b.Collateral.TryGetValue(p.Key, out var q) && q == p.Value);
    private static bool TryPositive(string value, out int result) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0;
    private static string Money(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Percent(decimal value) => value.ToString("0.##%", CultureInfo.InvariantCulture);

    private void Label(string id, float height, bool heading = false, string value = "", float width = -1)
    {
        _builder!.AddLabel(Localizer.Text(value), "loan." + id);
        var element = _builder.Last; _elements[id] = element;
        if (value.Length > 0) _staticTexts[id] = value;
        NativeStockMarketPanel.Container(element.root.GetComponent<RectTransform>(), width, height);
        if (element.text == null) return;
        NativeUiBootstrap.ApplyCurrentFont(element.text);
        element.text.fontSize = heading ? 18 : 16; element.text.color = heading ? StockUiTheme.Accent : StockUiTheme.Text;
        element.text.richText = false; element.text.enableWordWrapping = true; element.text.raycastTarget = false;
        element.text.alignment = TextAlignmentOptions.TopLeft;
    }

    private void Text(string id, string value, bool translate = true)
    {
        if (!_elements.TryGetValue(id, out var element)) return;
        value = translate ? Localizer.Text(value) : value; element.SetText(value);
        if (element.text == null) return;
        var rect = element.root.GetComponent<RectTransform>();
        var preferred = element.text.GetPreferredValues(value, Math.Max(140, rect.rect.width > 20 ? rect.rect.width - 12 : _width - 150), float.PositiveInfinity);
        var layout = element.root.GetComponent<LayoutElement>();
        if (layout != null) { layout.minHeight = 28; layout.preferredHeight = Math.Max(28, preferred.y + 10); }
    }

    private void Button(string id, string text, Action action, float width)
    {
        _builder!.AddButton(Localizer.Text(text), null, null);
        var element = _builder.Last; _elements[id] = element;
        NativeStockMarketPanel.Container(element.root.GetComponent<RectTransform>(), width, 36);
        if (element.text != null) { NativeUiBootstrap.ApplyCurrentFont(element.text); element.text.color = StockUiTheme.Text; element.text.fontSize = 16; element.text.richText = false; }
        var button = element.button ?? throw new InvalidOperationException("贷款按钮缺少 Button。");
        button.targetGraphic = NativeStockMarketPanel.Surface(element.root, "LoanButton", Color.white, 2, 2, 2, 2);
        NativeStockMarketPanel.SetButtonColors(button, false);
        Action managed = () => { if (_visible && !_disposed) { try { action(); } catch (Exception ex) { Report("贷款操作失败", ex); _feedback = "操作未完成，请重新查询贷款状态。"; Refresh(); } } };
        var native = new UnityAction(managed.Invoke);
        button.onClick.AddListener(native); _roots.Add(managed); _roots.Add(native);
        _unbind.Add(() => button.onClick.RemoveListener(native));
    }

    private void InputField(string id, string placeholder, string value, Action<string> changed, int digits = 9, float width = -1)
    {
        _builder!.AddInput(Localizer.Text(placeholder), value, null, "loan." + id);
        var element = _builder.Last; _elements[id] = element;
        NativeStockMarketPanel.Container(element.root.GetComponent<RectTransform>(), width, 36);
        var input = element.input ?? throw new InvalidOperationException("贷款输入框缺少 TMP_InputField。");
        input.targetGraphic = NativeStockMarketPanel.Surface(element.root, "LoanInput", Color.white, 2, 2, 2, 2);
        NativeStockMarketPanel.SetButtonColors(input, false);
        var colors = input.colors; colors.normalColor = StockUiTheme.Surface; input.colors = colors;
        input.characterLimit = digits; input.contentType = TMP_InputField.ContentType.IntegerNumber;
        input.customCaretColor = true; input.caretColor = StockUiTheme.Accent;
        if (input.textComponent != null) { NativeUiBootstrap.ApplyCurrentFont(input.textComponent); input.textComponent.color = StockUiTheme.Text; input.textComponent.fontSize = 16; }
        if (input.placeholder != null) { input.placeholder.color = StockUiTheme.Muted; var text = (input.placeholder as TMP_Text); if (text != null) NativeUiBootstrap.ApplyCurrentFont(text); }
        Action<string> managed = text => { if (_visible && !_disposed && !_refreshing) { changed(text); Refresh(); } };
        var native = new UnityAction<string>(managed.Invoke);
        input.onValueChanged.AddListener(native); _roots.Add(managed); _roots.Add(native);
        _unbind.Add(() => input.onValueChanged.RemoveListener(native));
    }

    private void Enabled(string id, bool enabled) { if (_elements.TryGetValue(id, out var element) && element.button != null) element.button.interactable = enabled; }
    private void SetButtonSelected(string id, bool selected) { if (_elements.TryGetValue(id, out var element) && element.button != null) NativeStockMarketPanel.SetButtonColors(element.button, selected); }

    private void StyleFrame()
    {
        var window = _window!;
        var background = window.root.GetComponent<Image>();
        if (background != null) background.color = Color.white;
        NativeStockMarketPanel.Surface(window.root, "LoanBackground", StockUiTheme.Background, 4, 4, 4, 4);
        if (window.frame.titleText != null) { NativeUiBootstrap.ApplyCurrentFont(window.frame.titleText); window.frame.titleText.color = StockUiTheme.Text; window.frame.titleText.richText = false; }
        if (window.frame.closeButton != null)
        {
            var close = window.frame.closeButton;
            close.targetGraphic = NativeStockMarketPanel.Surface(close.gameObject, "LoanClose", Color.white, 2, 2, 2, 2);
            NativeStockMarketPanel.SetButtonColors(close, false);
        }
    }

    private void OnClosed()
    {
        _visible = false; _pendingQuote = null;
        try { ReleaseWindow(false); }
        finally
        {
            if (_cursorSaved) { Cursor.lockState = _oldCursorLock; Cursor.visible = _oldCursorVisible; _cursorSaved = false; }
            _engine = null; _identity = ""; _host.LoanWindowChanged(false);
        }
    }

    private void ReleaseWindow(bool close)
    {
        var window = _window; _window = null;
        foreach (var unbind in _unbind) try { unbind(); } catch { /* 原生控件可能已随场景销毁，继续释放其余监听。 */ }
        _unbind.Clear();
        if (window != null) try { window.onClose = null; if (close && window.IsAlive) window.Close(); } catch (Exception ex) { Report("关闭贷款窗口失败", ex); }
        _elements.Clear(); _staticTexts.Clear(); _roots.Clear(); _builder = null; _nativeClosed = null;
        _applicationRoot = null; _accountRoot = null; _collateralRoot = null; _confirmationRoot = null; _businessRoot = null; _documentRoot = null;
        GC.KeepAlive(_managedClosed);
    }

    private static void Report(string stage, Exception ex) => UnityEngine.Debug.LogWarning("[贷款窗口] " + stage + "：" + ex);
    public void Dispose()
    {
        if (_disposed) return;
        Hide(); _disposed = true; _bootstrap.Dispose();
        if (_paperTexture != null) UnityEngine.Object.Destroy(_paperTexture);
        if (_sealTexture != null) UnityEngine.Object.Destroy(_sealTexture);
        _paperTexture = null; _sealTexture = null;
    }
}
